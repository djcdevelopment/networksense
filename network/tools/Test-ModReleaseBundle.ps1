#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ArtifactDirectory,
    [string] $ExpectedTag = '',
    [ValidateSet('', 'public', 'interim')]
    [string] $ExpectedDependencyProfile = '',
    [string] $ValheimDirectory = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim',
    [switch] $SkipExternalBuildInputVerification
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $repoRoot 'tools\Assert-RepoIdentity.ps1') -DefineOnly
Assert-RepoIdentity | Out-Null
. (Join-Path $PSScriptRoot 'lib\ModRelease.ps1')

$artifactRoot = (Resolve-Path -LiteralPath $ArtifactDirectory).Path
$requiredNames = @(
    'ComfyNetworkSense.dll',
    'release-manifest.json',
    'boundary-receipt.json',
    'SHA256SUMS'
)
$actualNames = @(Get-ChildItem -LiteralPath $artifactRoot -File | Select-Object -ExpandProperty Name)
$unexpected = @($actualNames | Where-Object { $_ -notin $requiredNames })
$missing = @($requiredNames | Where-Object { $_ -notin $actualNames })
if ($unexpected.Count -gt 0 -or $missing.Count -gt 0) {
    throw ('release bundle must contain exactly the four declared assets; missing=[{0}] unexpected=[{1}]' -f
        ($missing -join ', '), ($unexpected -join ', '))
}

$dllPath = Join-Path $artifactRoot 'ComfyNetworkSense.dll'
$manifestPath = Join-Path $artifactRoot 'release-manifest.json'
$receiptPath = Join-Path $artifactRoot 'boundary-receipt.json'
$checksumsPath = Join-Path $artifactRoot 'SHA256SUMS'
$manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
$receipt = [IO.File]::ReadAllText($receiptPath) | ConvertFrom-Json

if ([string]$manifest.schema -ne 'comfy-mod-release-manifest/v1') {
    throw "release manifest schema is not comfy-mod-release-manifest/v1"
}
if ([string]$receipt.schema -ne 'comfy-mod-boundary-receipt/v1') {
    throw "boundary receipt schema is not comfy-mod-boundary-receipt/v1"
}
if ([string]$manifest.repository -ne 'djcdevelopment/networksense' -or
    [string]$receipt.repository -ne 'djcdevelopment/networksense') {
    throw 'release bundle repository identity is not djcdevelopment/networksense'
}

$tag = [string]$manifest.tag
if ([string]::IsNullOrWhiteSpace($tag) -or [string]$receipt.tag -ne $tag) {
    throw 'manifest and boundary receipt tags do not agree'
}
if ($ExpectedTag -and $tag -ne $ExpectedTag) {
    throw "release tag mismatch: expected=$ExpectedTag actual=$tag"
}
$tagVersion = Get-ReleaseTagVersion -ReleaseTag $tag

$sourceIdentity = Get-ModSourceIdentity -RepositoryRoot $repoRoot
if ($sourceIdentity.plugin_version -ne $tagVersion -or
    [string]$manifest.plugin.version -ne $tagVersion -or
    [string]$receipt.plugin_version -ne $tagVersion) {
    throw 'tag, source PluginVersion, manifest, and boundary receipt versions do not agree'
}
if ([string]$manifest.plugin.name -ne $sourceIdentity.plugin_name -or
    [string]$manifest.plugin.guid -ne $sourceIdentity.plugin_guid) {
    throw 'release manifest plugin name/GUID does not match source'
}
if ($sourceIdentity.manifest_version -ne $tagVersion) {
    throw "Thunderstore manifest version does not match release tag: $($sourceIdentity.manifest_version)"
}
if ($sourceIdentity.baked_release_id -ne [string]$manifest.plugin.baked_release_id -or
    $sourceIdentity.baked_release_id -ne [string]$receipt.baked_release_id) {
    throw 'source and release bundle baked release_id values do not agree'
}

$sourceRevision = (@(& git -C $repoRoot rev-parse HEAD)).Trim()
if ($LASTEXITCODE -ne 0) { throw 'could not resolve source revision' }
if ([string]$manifest.source_revision -ne $sourceRevision -or
    [string]$receipt.source_revision -ne $sourceRevision) {
    throw "release source_revision does not match checked-out HEAD $sourceRevision"
}
$dirty = @(& git -C $repoRoot status --porcelain --untracked-files=all)
if ($LASTEXITCODE -ne 0) { throw 'could not inspect source worktree state' }
if ($dirty.Count -ne 0 -or -not [bool]$receipt.source_clean) {
    throw 'release verification requires the recorded source checkout to be clean'
}

$dllHash = Get-LowerSha256 -Path $dllPath
$dllBytes = [long](Get-Item -LiteralPath $dllPath).Length
if ([string]$manifest.artifact.name -ne 'ComfyNetworkSense.dll' -or
    [string]$receipt.dll.name -ne 'ComfyNetworkSense.dll') {
    throw 'release artifact name is not ComfyNetworkSense.dll'
}
if ([string]$manifest.artifact.sha256 -ne $dllHash -or
    [string]$receipt.dll.sha256 -ne $dllHash) {
    throw 'ComfyNetworkSense.dll SHA-256 mismatch'
}
if ([long]$manifest.artifact.bytes -ne $dllBytes -or [long]$receipt.dll.bytes -ne $dllBytes) {
    throw 'ComfyNetworkSense.dll byte-count mismatch'
}

$inspection = Invoke-ModAssemblyInspection -RepositoryRoot $repoRoot -DllPath $dllPath
$expectedAssemblyVersion = "$tagVersion.0"
$metadataReleaseId = $inspection.metadata.PSObject.Properties['LumberjacksModReleaseId'].Value
if ([string]$inspection.schema -ne 'comfy-mod-assembly-inspection/v1' -or
    [string]$inspection.assembly_name -ne 'ComfyNetworkSense' -or
    [string]$inspection.assembly_version -ne $expectedAssemblyVersion -or
    [string]$inspection.file_version -ne $tagVersion) {
    throw 'managed assembly identity/version does not match the release tag'
}
if ([string]$metadataReleaseId -ne $sourceIdentity.baked_release_id) {
    throw 'managed assembly baked LumberjacksModReleaseId does not match source'
}
if ([string]$manifest.artifact.assembly_version -ne [string]$inspection.assembly_version -or
    [string]$manifest.artifact.file_version -ne [string]$inspection.file_version -or
    [string]$receipt.dll.assembly_version -ne [string]$inspection.assembly_version -or
    [string]$receipt.dll.file_version -ne [string]$inspection.file_version) {
    throw 'recorded assembly versions do not match the DLL metadata'
}

$profile = [string]$receipt.build.dependency_profile
if ($profile -notin @('public', 'interim')) { throw "invalid dependency profile in receipt: $profile" }
if ($ExpectedDependencyProfile -and $profile -ne $ExpectedDependencyProfile) {
    throw "dependency profile mismatch: expected=$ExpectedDependencyProfile actual=$profile"
}
if ([string]$manifest.build.dependency_profile -ne $profile) {
    throw 'manifest and receipt dependency profiles do not agree'
}
if ([string]$manifest.build.configuration -ne 'Release' -or
    [string]$receipt.build.configuration -ne 'Release' -or
    -not [bool]$receipt.build.copy_to_plugins_disabled) {
    throw 'release bundle was not recorded as a copy-disabled Release build'
}
if ([string]$manifest.build.dotnet_sdk -ne [string]$receipt.build.dotnet_sdk -or
    [string]::IsNullOrWhiteSpace([string]$receipt.build.dotnet_sdk)) {
    throw 'manifest and receipt .NET SDK records do not agree'
}

$expectedConfigPath = if ($profile -eq 'public') { 'nuget.config' } else { 'nuget.interim.config' }
if ([string]$receipt.build.nuget_config.path -ne $expectedConfigPath -or
    [string]$manifest.build.nuget_config.path -ne $expectedConfigPath) {
    throw "dependency profile $profile must use $expectedConfigPath"
}
$configHash = Get-LowerSha256 -Path (Join-Path $repoRoot $expectedConfigPath)
if ([string]$receipt.build.nuget_config.sha256 -ne $configHash -or
    [string]$manifest.build.nuget_config.sha256 -ne $configHash) {
    throw 'recorded NuGet configuration hash does not match source'
}

$currentSource = Get-ModSourceEvidence -RepositoryRoot $repoRoot
if ([string]$receipt.source.sha256 -ne [string]$currentSource.sha256 -or
    @($receipt.source.files).Count -ne @($currentSource.files).Count) {
    throw 'source-aware boundary digest does not match the checked-out build inputs'
}

$packages = @($receipt.build.packages)
$expectedPackageVersions = if ($profile -eq 'public') {
    @('Comfy.Quest.Contracts|0.1.0', 'Comfy.Transport.Contracts|0.1.0')
} else {
    @('Comfy.Quest.Contracts|0.1.0-local', 'Comfy.Transport.Contracts|0.1.0-local')
}
if ($packages.Count -ne 2) { throw 'boundary receipt must record exactly two contract packages' }
foreach ($package in $packages) {
    $key = '{0}|{1}' -f [string]$package.id, [string]$package.version
    if ($key -notin $expectedPackageVersions -or [string]$package.sha256 -notmatch '^[0-9a-f]{64}$' -or
        [long]$package.bytes -le 0) {
        throw "invalid contract package evidence: $key"
    }
}

$compileInputs = @($receipt.build.compile_inputs)
if ($compileInputs.Count -ne 12) { throw 'boundary receipt must record exactly 12 Valheim+BepInEx compile inputs' }
foreach ($input in $compileInputs) {
    if ([string]$input.scope -notin @('bepinex', 'valheim', 'unity') -or
        [string]$input.path -notmatch '^(BepInEx/core|valheim_Data/Managed)/[^/]+\.dll$' -or
        [string]$input.sha256 -notmatch '^[0-9a-f]{64}$' -or [long]$input.bytes -le 0) {
        throw "invalid compile-input evidence: $($input.path)"
    }
}

if (-not $SkipExternalBuildInputVerification) {
    $currentPackages = @(Get-DependencyPackageEvidence -RepositoryRoot $repoRoot -DependencyProfile $profile)
    foreach ($expected in $currentPackages) {
        $recorded = @($packages | Where-Object { $_.id -eq $expected.id -and $_.version -eq $expected.version })
        if ($recorded.Count -ne 1 -or [string]$recorded[0].sha256 -ne [string]$expected.sha256 -or
            [long]$recorded[0].bytes -ne [long]$expected.bytes) {
            throw "contract package evidence mismatch: $($expected.id) $($expected.version)"
        }
    }

    $currentCompileInputs = @(Get-ModCompileInputEvidence -ValheimDirectory $ValheimDirectory)
    foreach ($expected in $currentCompileInputs) {
        $recorded = @($compileInputs | Where-Object { $_.path -eq $expected.path })
        if ($recorded.Count -ne 1 -or [string]$recorded[0].sha256 -ne [string]$expected.sha256 -or
            [long]$recorded[0].bytes -ne [long]$expected.bytes) {
            throw "compile-input evidence mismatch: $($expected.path)"
        }
    }
}

$receiptHash = Get-LowerSha256 -Path $receiptPath
if ([string]$manifest.boundary_receipt.name -ne 'boundary-receipt.json' -or
    [string]$manifest.boundary_receipt.sha256 -ne $receiptHash) {
    throw 'manifest boundary-receipt hash does not match boundary-receipt.json'
}

$checksums = Read-ReleaseChecksums -Path $checksumsPath
if ($checksums.Count -ne 3) { throw 'SHA256SUMS must contain exactly three rows' }
$expectedChecksums = @{
    'ComfyNetworkSense.dll' = $dllHash
    'release-manifest.json' = Get-LowerSha256 -Path $manifestPath
    'boundary-receipt.json' = $receiptHash
}
foreach ($assetName in $expectedChecksums.Keys) {
    if (-not $checksums.ContainsKey($assetName) -or $checksums[$assetName] -ne $expectedChecksums[$assetName]) {
        throw "SHA256SUMS mismatch for $assetName"
    }
}

Write-Output ("verified NetworkSense release bundle: tag={0} revision={1} version={2} profile={3} dll_sha256={4}" -f
    $tag, $sourceRevision, $tagVersion, $profile, $dllHash)
