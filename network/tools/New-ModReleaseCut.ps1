#Requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Prepare', 'Verify')]
    [string] $Mode = 'Prepare',
    [string] $ReleaseTag = 'mod-v0.5.80-split-proof',
    [string] $ExpectedPluginVersion = '0.5.80',
    [ValidateSet('public', 'interim')]
    [string] $DependencyProfile = 'public',
    [string] $OutputDirectory = '',
    [string] $ValheimDirectory = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim',
    [switch] $SynchronizeManifest,
    [switch] $RequireTagAtHead,
    [switch] $Overwrite
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $repoRoot 'tools\Assert-RepoIdentity.ps1') -DefineOnly
Assert-RepoIdentity | Out-Null
. (Join-Path $PSScriptRoot 'lib\ModRelease.ps1')

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot ("artifacts\releases\{0}" -f $ReleaseTag)
} elseif (-not [IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot $OutputDirectory
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)

$tagVersion = Get-ReleaseTagVersion -ReleaseTag $ReleaseTag
$identity = Get-ModSourceIdentity -RepositoryRoot $repoRoot
if ($identity.plugin_version -ne $ExpectedPluginVersion) {
    throw "source PluginVersion mismatch: expected=$ExpectedPluginVersion actual=$($identity.plugin_version)"
}
if ($tagVersion -ne $identity.plugin_version) {
    throw "release tag version $tagVersion does not match source PluginVersion $($identity.plugin_version)"
}

if ($Mode -eq 'Verify') {
    & (Join-Path $PSScriptRoot 'Test-ModReleaseBundle.ps1') `
        -ArtifactDirectory $OutputDirectory `
        -ExpectedTag $ReleaseTag `
        -ExpectedDependencyProfile $DependencyProfile `
        -ValheimDirectory $ValheimDirectory
    return
}

$expectedWebsite = 'https://github.com/djcdevelopment/networksense'
if ($identity.manifest_version -ne $identity.plugin_version -or
    $identity.manifest_website -ne $expectedWebsite) {
    if (-not $SynchronizeManifest) {
        throw ('manifest identity differs from source; rerun with -SynchronizeManifest, review and commit the change, then prepare again')
    }
    $manifest = [IO.File]::ReadAllText($identity.manifest_path) | ConvertFrom-Json
    $manifest.version_number = $identity.plugin_version
    $manifest.website_url = $expectedWebsite
    Write-Utf8Json -Value $manifest -Path $identity.manifest_path
    throw 'manifest synchronized from PluginVersion; review and commit it before preparing a release bundle'
}

$dirty = @(& git -C $repoRoot status --porcelain --untracked-files=all)
if ($LASTEXITCODE -ne 0) { throw 'could not inspect source worktree state' }
if ($dirty.Count -ne 0) {
    throw "release preparation requires a clean tracked worktree:`n$($dirty -join [Environment]::NewLine)"
}
$sourceRevision = (@(& git -C $repoRoot rev-parse HEAD)).Trim()
if ($LASTEXITCODE -ne 0) { throw 'could not resolve source revision' }

if ($RequireTagAtHead) {
    $tagRevision = @(& git -C $repoRoot rev-list -n 1 $ReleaseTag 2>$null)
    if ($LASTEXITCODE -ne 0 -or ($tagRevision -join '').Trim() -ne $sourceRevision) {
        throw "required tag $ReleaseTag does not resolve to checked-out HEAD $sourceRevision"
    }
}

$knownAssets = @('ComfyNetworkSense.dll', 'release-manifest.json', 'boundary-receipt.json', 'SHA256SUMS')
if (Test-Path -LiteralPath $OutputDirectory) {
    $existing = @(Get-ChildItem -LiteralPath $OutputDirectory -Force)
    if ($existing.Count -gt 0 -and -not $Overwrite) {
        throw "release output is not empty; use -Overwrite to replace only known assets: $OutputDirectory"
    }
    if ($Overwrite) {
        foreach ($asset in $knownAssets) {
            $candidate = Join-Path $OutputDirectory $asset
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                Remove-Item -LiteralPath $candidate -Force
            }
        }
        $remaining = @(Get-ChildItem -LiteralPath $OutputDirectory -Force)
        if ($remaining.Count -gt 0) {
            throw "release output contains unrecognized content and was not cleared: $OutputDirectory"
        }
    }
} else {
    New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
}

$pluginPath = Join-Path $ValheimDirectory 'BepInEx\plugins\ComfyNetworkSense.dll'
$pluginBefore = if (Test-Path -LiteralPath $pluginPath -PathType Leaf) {
    Get-LowerSha256 -Path $pluginPath
} else { '' }

$buildRoot = Join-Path $repoRoot ("artifacts\release-build\{0}" -f $ReleaseTag)
New-Item -ItemType Directory -Force -Path $buildRoot | Out-Null
$project = Join-Path $repoRoot 'network\mod\ComfyNetworkSense\ComfyNetworkSense.csproj'
$buildArguments = @(
    'build', $project,
    '-c', 'Release',
    '-t:Rebuild',
    '--nologo',
    '-v:minimal',
    '-p:ComfyCopyToPlugins=false',
    "-p:ComfyDependencyProfile=$DependencyProfile",
    "-p:ValheimDir=$ValheimDirectory",
    "-p:PluginOutputPath=$buildRoot",
    "-p:OutputPath=$buildRoot"
)
& dotnet @buildArguments
if ($LASTEXITCODE -ne 0) { throw 'copy-disabled NetworkSense Release build failed' }

$builtDll = Join-Path $buildRoot 'ComfyNetworkSense.dll'
if (-not (Test-Path -LiteralPath $builtDll -PathType Leaf)) {
    throw "Release build did not emit expected DLL: $builtDll"
}
$pluginAfter = if (Test-Path -LiteralPath $pluginPath -PathType Leaf) {
    Get-LowerSha256 -Path $pluginPath
} else { '' }
if ($pluginBefore -ne $pluginAfter) {
    throw 'live BepInEx plugin changed during a copy-disabled release build'
}

$dllPath = Join-Path $OutputDirectory 'ComfyNetworkSense.dll'
Copy-Item -LiteralPath $builtDll -Destination $dllPath
$inspection = Invoke-ModAssemblyInspection -RepositoryRoot $repoRoot -DllPath $dllPath
$metadataReleaseId = $inspection.metadata.PSObject.Properties['LumberjacksModReleaseId'].Value
$expectedAssemblyVersion = "$($identity.plugin_version).0"
if ([string]$inspection.assembly_name -ne 'ComfyNetworkSense' -or
    [string]$inspection.assembly_version -ne $expectedAssemblyVersion -or
    [string]$inspection.file_version -ne $identity.plugin_version -or
    [string]$metadataReleaseId -ne $identity.baked_release_id) {
    throw 'built DLL identity, PluginVersion, or baked release_id does not match source'
}

$sourceEvidence = Get-ModSourceEvidence -RepositoryRoot $repoRoot
$compileInputs = @(Get-ModCompileInputEvidence -ValheimDirectory $ValheimDirectory)
$packages = @(Get-DependencyPackageEvidence -RepositoryRoot $repoRoot -DependencyProfile $DependencyProfile)
$configRelative = if ($DependencyProfile -eq 'public') { 'nuget.config' } else { 'nuget.interim.config' }
$configEvidence = Get-FileEvidence -Path (Join-Path $repoRoot $configRelative) -RelativePath $configRelative
$dotnetSdk = (@(& dotnet --version)).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($dotnetSdk)) {
    throw 'could not record the .NET SDK version'
}
$dllEvidence = Get-FileEvidence -Path $dllPath -RelativePath 'ComfyNetworkSense.dll'

$receipt = [ordered]@{
    schema = 'comfy-mod-boundary-receipt/v1'
    generated_utc = [DateTime]::UtcNow.ToString('o')
    repository = 'djcdevelopment/networksense'
    tag = $ReleaseTag
    source_revision = $sourceRevision
    source_clean = $true
    plugin_version = $identity.plugin_version
    baked_release_id = $identity.baked_release_id
    dll = [ordered]@{
        name = 'ComfyNetworkSense.dll'
        sha256 = $dllEvidence.sha256
        bytes = $dllEvidence.bytes
        assembly_version = [string]$inspection.assembly_version
        file_version = [string]$inspection.file_version
        module_version_id = [string]$inspection.module_version_id
    }
    source = $sourceEvidence
    build = [ordered]@{
        configuration = 'Release'
        dotnet_sdk = $dotnetSdk
        dependency_profile = $DependencyProfile
        copy_to_plugins_disabled = $true
        live_plugin_sha256_before = $pluginBefore
        live_plugin_sha256_after = $pluginAfter
        nuget_config = $configEvidence
        packages = $packages
        compile_inputs = $compileInputs
    }
    checks = [ordered]@{
        tag_matches_plugin_version = $true
        manifest_matches_plugin_version = $true
        assembly_matches_source = $true
        source_boundary_recorded = $true
        live_plugin_unchanged = $true
    }
    result = 'pass'
}
$receiptPath = Join-Path $OutputDirectory 'boundary-receipt.json'
Write-Utf8Json -Value $receipt -Path $receiptPath
$receiptHash = Get-LowerSha256 -Path $receiptPath

$releaseManifest = [ordered]@{
    schema = 'comfy-mod-release-manifest/v1'
    repository = 'djcdevelopment/networksense'
    tag = $ReleaseTag
    source_revision = $sourceRevision
    plugin = [ordered]@{
        name = $identity.plugin_name
        guid = $identity.plugin_guid
        version = $identity.plugin_version
        baked_release_id = $identity.baked_release_id
    }
    artifact = [ordered]@{
        name = 'ComfyNetworkSense.dll'
        sha256 = $dllEvidence.sha256
        bytes = $dllEvidence.bytes
        assembly_version = [string]$inspection.assembly_version
        file_version = [string]$inspection.file_version
    }
    boundary_receipt = [ordered]@{
        name = 'boundary-receipt.json'
        sha256 = $receiptHash
    }
    build = [ordered]@{
        configuration = 'Release'
        dotnet_sdk = $dotnetSdk
        dependency_profile = $DependencyProfile
        nuget_config = [ordered]@{
            path = $configEvidence.path
            sha256 = $configEvidence.sha256
        }
    }
}
$manifestPath = Join-Path $OutputDirectory 'release-manifest.json'
Write-Utf8Json -Value $releaseManifest -Path $manifestPath

$checksumRows = @(
    ('{0}  ComfyNetworkSense.dll' -f (Get-LowerSha256 -Path $dllPath)),
    ('{0}  release-manifest.json' -f (Get-LowerSha256 -Path $manifestPath)),
    ('{0}  boundary-receipt.json' -f (Get-LowerSha256 -Path $receiptPath))
)
[IO.File]::WriteAllText(
    (Join-Path $OutputDirectory 'SHA256SUMS'),
    ($checksumRows -join "`n") + "`n",
    (New-Object Text.UTF8Encoding($false)))

& (Join-Path $PSScriptRoot 'Test-ModReleaseBundle.ps1') `
    -ArtifactDirectory $OutputDirectory `
    -ExpectedTag $ReleaseTag `
    -ExpectedDependencyProfile $DependencyProfile `
    -ValheimDirectory $ValheimDirectory

Write-Output ("prepared copy-disabled NetworkSense release bundle: {0}" -f $OutputDirectory)
