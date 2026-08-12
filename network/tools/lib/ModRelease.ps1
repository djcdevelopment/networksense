#Requires -Version 5.1

function Get-LowerSha256 {
    param([Parameter(Mandatory = $true)][string] $Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Write-Utf8Json {
    param(
        [Parameter(Mandatory = $true)] $Value,
        [Parameter(Mandatory = $true)][string] $Path
    )

    $json = $Value | ConvertTo-Json -Depth 30
    [IO.File]::WriteAllText(
        [IO.Path]::GetFullPath($Path),
        $json + [Environment]::NewLine,
        (New-Object Text.UTF8Encoding($false)))
}

function Get-StringSha256 {
    param([Parameter(Mandatory = $true)][string] $Value)

    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
        return ([BitConverter]::ToString($algorithm.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function Get-RelativeSlashPath {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Path
    )

    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $pathFull = [IO.Path]::GetFullPath($Path)
    if (-not $pathFull.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "path is outside root: root=$rootFull path=$pathFull"
    }
    return $pathFull.Substring($rootFull.Length).Replace('\', '/')
}

function Get-FileEvidence {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $RelativePath
    )

    $item = Get-Item -LiteralPath $Path -ErrorAction Stop
    if ($item.PSIsContainer) { throw "expected a file: $Path" }
    return [ordered]@{
        path = $RelativePath.Replace('\', '/')
        sha256 = Get-LowerSha256 -Path $item.FullName
        bytes = [long]$item.Length
    }
}

function Get-ModSourceIdentity {
    param([Parameter(Mandatory = $true)][string] $RepositoryRoot)

    $sourcePath = Join-Path $RepositoryRoot 'network\mod\ComfyNetworkSense\ComfyNetworkSense.cs'
    $manifestPath = Join-Path $RepositoryRoot 'network\mod\ComfyNetworkSense\manifest.json'
    $source = [IO.File]::ReadAllText($sourcePath)
    $guidMatch = [regex]::Match($source, 'public const string PluginGuid = "([^"]+)";')
    $nameMatch = [regex]::Match($source, 'public const string PluginName = "([^"]+)";')
    $versionMatch = [regex]::Match($source, 'public const string PluginVersion = "([^"]+)";')
    $releaseMatch = [regex]::Match($source, 'public const string ReleaseId = "([^"]+)";')
    if (-not $guidMatch.Success -or -not $nameMatch.Success -or
        -not $versionMatch.Success -or -not $releaseMatch.Success) {
        throw 'could not read PluginGuid, PluginName, PluginVersion, and ReleaseId from ComfyNetworkSense.cs'
    }

    $manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
    return [ordered]@{
        source_path = $sourcePath
        manifest_path = $manifestPath
        plugin_guid = $guidMatch.Groups[1].Value
        plugin_name = $nameMatch.Groups[1].Value
        plugin_version = $versionMatch.Groups[1].Value
        baked_release_id = $releaseMatch.Groups[1].Value
        manifest_version = [string]$manifest.version_number
        manifest_website = [string]$manifest.website_url
    }
}

function Get-ReleaseTagVersion {
    param([Parameter(Mandatory = $true)][string] $ReleaseTag)

    $match = [regex]::Match(
        $ReleaseTag,
        '^mod-v(?<version>\d+\.\d+\.\d+)(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?$')
    if (-not $match.Success) {
        throw "release tag must match mod-v<semver>[-label]: $ReleaseTag"
    }
    return $match.Groups['version'].Value
}

function Get-ModSourceEvidence {
    param([Parameter(Mandatory = $true)][string] $RepositoryRoot)

    $paths = @(& git -C $RepositoryRoot ls-files -- `
        'network/mod/.gitattributes' `
        'network/mod/ComfyNetworkSense' `
        'eng/dependencies.public.props' `
        'eng/dependencies.interim.props' `
        'nuget.config' `
        'nuget.interim.config')
    if ($LASTEXITCODE -ne 0) { throw 'git ls-files failed while recording mod source inputs' }

    $records = @()
    foreach ($relative in ($paths | Sort-Object -Unique)) {
        $extension = [IO.Path]::GetExtension($relative).ToLowerInvariant()
        $leaf = [IO.Path]::GetFileName($relative)
        if ($extension -notin @('.cs', '.csproj', '.props', '.targets', '.config', '.json') -and
            $leaf -ne '.gitattributes') {
            continue
        }
        $records += Get-FileEvidence -Path (Join-Path $RepositoryRoot $relative) -RelativePath $relative
    }
    if ($records.Count -eq 0) { throw 'no tracked mod source inputs were found' }

    $composite = @($records | ForEach-Object {
        "{0} {1} {2}" -f $_.sha256, $_.bytes, $_.path
    }) -join "`n"
    return [ordered]@{
        sha256 = Get-StringSha256 -Value ($composite + "`n")
        files = @($records)
    }
}

function Get-ModCompileInputEvidence {
    param([Parameter(Mandatory = $true)][string] $ValheimDirectory)

    $inputs = @(
        [ordered]@{ scope = 'bepinex'; path = 'BepInEx/core/0Harmony.dll' },
        [ordered]@{ scope = 'bepinex'; path = 'BepInEx/core/BepInEx.dll' },
        [ordered]@{ scope = 'valheim'; path = 'valheim_Data/Managed/assembly_valheim.dll' },
        [ordered]@{ scope = 'valheim'; path = 'valheim_Data/Managed/assembly_utils.dll' },
        [ordered]@{ scope = 'valheim'; path = 'valheim_Data/Managed/System.Runtime.Serialization.dll' },
        [ordered]@{ scope = 'unity'; path = 'valheim_Data/Managed/UnityEngine.dll' },
        [ordered]@{ scope = 'unity'; path = 'valheim_Data/Managed/UnityEngine.CoreModule.dll' },
        [ordered]@{ scope = 'unity'; path = 'valheim_Data/Managed/UnityEngine.IMGUIModule.dll' },
        [ordered]@{ scope = 'unity'; path = 'valheim_Data/Managed/UnityEngine.JSONSerializeModule.dll' },
        [ordered]@{ scope = 'unity'; path = 'valheim_Data/Managed/UnityEngine.InputLegacyModule.dll' },
        [ordered]@{ scope = 'unity'; path = 'valheim_Data/Managed/UnityEngine.PhysicsModule.dll' },
        [ordered]@{ scope = 'unity'; path = 'valheim_Data/Managed/UnityEngine.TextRenderingModule.dll' }
    )

    $records = @()
    foreach ($input in $inputs) {
        $fullPath = Join-Path $ValheimDirectory ($input.path.Replace('/', '\'))
        $record = Get-FileEvidence -Path $fullPath -RelativePath $input.path
        $records += [ordered]@{
            scope = $input.scope
            path = $record.path
            sha256 = $record.sha256
            bytes = $record.bytes
        }
    }
    return @($records)
}

function Get-DependencyPackageEvidence {
    param(
        [Parameter(Mandatory = $true)][string] $RepositoryRoot,
        [Parameter(Mandatory = $true)][ValidateSet('public', 'interim')][string] $DependencyProfile
    )

    $dependencies = @(
        [ordered]@{ id = 'Comfy.Quest.Contracts'; public = '0.1.0'; interim = '0.1.0-local' },
        [ordered]@{ id = 'Comfy.Transport.Contracts'; public = '0.1.0'; interim = '0.1.0-local' }
    )
    $records = @()
    foreach ($dependency in $dependencies) {
        $version = [string]$dependency.$DependencyProfile
        if ($DependencyProfile -eq 'interim') {
            $relative = "packages-local/{0}.{1}.nupkg" -f $dependency.id, $version
            $fullPath = Join-Path $RepositoryRoot ($relative.Replace('/', '\'))
            $source = 'packages-local'
        } else {
            $globalRoot = $env:NUGET_PACKAGES
            if ([string]::IsNullOrWhiteSpace($globalRoot)) {
                $line = @(& dotnet nuget locals global-packages --list) | Select-Object -Last 1
                if ($LASTEXITCODE -ne 0 -or $line -notmatch '^global-packages:\s*(.+)$') {
                    throw 'could not resolve the NuGet global-packages directory'
                }
                $globalRoot = $Matches[1].Trim()
            }
            $relative = "{0}/{1}/{0}.{1}.nupkg" -f $dependency.id.ToLowerInvariant(), $version
            $fullPath = Join-Path $globalRoot ($relative.Replace('/', '\'))
            $source = 'nuget-global-packages'
        }
        $evidence = Get-FileEvidence -Path $fullPath -RelativePath $relative
        $records += [ordered]@{
            id = $dependency.id
            version = $version
            source = $source
            path = $evidence.path
            sha256 = $evidence.sha256
            bytes = $evidence.bytes
        }
    }
    return @($records)
}

function Invoke-ModAssemblyInspection {
    param(
        [Parameter(Mandatory = $true)][string] $RepositoryRoot,
        [Parameter(Mandatory = $true)][string] $DllPath
    )

    $project = Join-Path $RepositoryRoot 'network\tools\release-inspector\NetworkSense.ReleaseInspector.csproj'
    & dotnet build $project -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'release inspector build failed' }
    $inspector = Join-Path $RepositoryRoot `
        'network\tools\release-inspector\bin\Release\net8.0\NetworkSense.ReleaseInspector.dll'
    $json = @(& dotnet $inspector $DllPath)
    if ($LASTEXITCODE -ne 0) { throw 'release inspector failed' }
    return (($json -join [Environment]::NewLine) | ConvertFrom-Json)
}

function Read-ReleaseChecksums {
    param([Parameter(Mandatory = $true)][string] $Path)

    $result = @{}
    foreach ($line in [IO.File]::ReadAllLines($Path)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -notmatch '^([0-9a-fA-F]{64})  ([^\\/]+)$') {
            throw "invalid SHA256SUMS row: $line"
        }
        $name = $Matches[2]
        if ($result.ContainsKey($name)) { throw "duplicate SHA256SUMS asset: $name" }
        $result[$name] = $Matches[1].ToLowerInvariant()
    }
    return $result
}
