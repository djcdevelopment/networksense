<#
.SYNOPSIS
Sync the Docker-backed Lumberjacks Companion source/runtime inputs to i5 and start it.

.DESCRIPTION
This is the one-command i5 lane for Companion development. It copies the minimal build context
needed by tools/companion/docker-compose.yml into C:\deploy\baseline\i5-companion on the i5,
verifies SHA-256 on every copied file through Deploy-ToI5.ps1, then starts the Companion with
Start-I5Companion.ps1.

The script intentionally does not copy the whole repository. It syncs:

- Lumberjacks/Directory.Build.props
- Lumberjacks/Directory.Packages.props
- Lumberjacks/src/Game.Companion/**
- Lumberjacks/tools/companion/docker-compose.yml
- Lumberjacks/tools/companion/docker-compose.valheim.yml
- Lumberjacks/tools/companion/Start-WorkbenchHostRunner.ps1
- Lumberjacks/tools/companion/latest-bootstrap.json

Before copying Game.Companion, it removes only the prior remote
C:\deploy\baseline\i5-companion\src\Game.Companion directory, after validating that the resolved
target remains below the configured RemoteRoot. This prevents stale source files from surviving a
rename/delete while keeping cleanup scoped to the staging checkout.

.PARAMETER RemoteRoot
The baseline staging checkout on the i5.

.PARAMETER NoStart
Copy and verify files, but do not restart the i5 Companion.

.PARAMETER DryRun
Print the deploy operations without copying, cleanup, or starting.

.EXAMPLE
.\tools\i5\Sync-I5Companion.ps1
#>
[CmdletBinding()]
param(
    [string]$RemoteRoot = 'C:\deploy\baseline\i5-companion',
    [switch]$NoStart,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$LumberjacksRoot = Join-Path $RepoRoot 'Lumberjacks'
$DeployScript = Join-Path $PSScriptRoot 'Deploy-ToI5.ps1'
$StartScript = Join-Path $PSScriptRoot 'Start-I5Companion.ps1'
$SshArgs = @('-o', 'BatchMode=yes', '-o', 'ConnectTimeout=8', 'i5')

function RemotePath {
    param([string]$Path)
    return ($Path -replace '\\', '/').TrimEnd('/')
}

function Invoke-RemoteCleanup {
    param([string]$Root)

    $remote = @'
$ErrorActionPreference = 'Stop'
$remoteRoot = '__REMOTE_ROOT__'
$rootFull = [IO.Path]::GetFullPath($remoteRoot)
$targets = @(
    (Join-Path $rootFull 'src\Game.Companion')
)
foreach ($target in $targets) {
    $targetFull = [IO.Path]::GetFullPath($target)
    if (-not $targetFull.StartsWith($rootFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "refusing cleanup outside remote root: $targetFull"
    }
    if (Test-Path -LiteralPath $targetFull) {
        Remove-Item -LiteralPath $targetFull -Recurse -Force
        Write-Output ("removed stale remote directory: {0}" -f $targetFull)
    }
}
New-Item -ItemType Directory -Force -Path `
    (Join-Path $rootFull 'src'), `
    (Join-Path $rootFull 'tools\companion') | Out-Null
'@
    $escaped = $remote.Replace('__REMOTE_ROOT__', $Root.Replace("'", "''"))
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($escaped))
    ssh @SshArgs "powershell.exe -NoProfile -EncodedCommand $encoded"
    if ($LASTEXITCODE -ne 0) { throw 'remote cleanup failed' }
}

function Deploy {
    param([string[]]$Path, [string]$Dest, [string[]]$ExcludeDirectoryName = @())

    if ($DryRun) {
        & $DeployScript -Path $Path -Dest (RemotePath $Dest) -ExcludeDirectoryName $ExcludeDirectoryName -DryRun
    } else {
        & $DeployScript -Path $Path -Dest (RemotePath $Dest) -ExcludeDirectoryName $ExcludeDirectoryName
    }
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$remoteRootSlash = RemotePath $RemoteRoot
$plan = @(
    [pscustomobject]@{
        Path = @(
            (Join-Path $LumberjacksRoot 'Directory.Build.props'),
            (Join-Path $LumberjacksRoot 'Directory.Packages.props')
        )
        Dest = $remoteRootSlash
    },
    [pscustomobject]@{
        Path = @((Join-Path $LumberjacksRoot 'src\Game.Companion'))
        Dest = "$remoteRootSlash/src"
        ExcludeDirectoryName = @('bin', 'obj')
    },
    [pscustomobject]@{
        Path = @(
            (Join-Path $LumberjacksRoot 'tools\companion\docker-compose.yml'),
            (Join-Path $LumberjacksRoot 'tools\companion\docker-compose.valheim.yml'),
            (Join-Path $LumberjacksRoot 'tools\companion\latest-bootstrap.json'),
            (Join-Path $LumberjacksRoot 'tools\companion\Start-WorkbenchHostRunner.ps1')
        )
        Dest = "$remoteRootSlash/tools/companion"
    }
)

Write-Host "sync i5 Companion -> $remoteRootSlash"
if ($DryRun) {
    Write-Host 'dry run: remote cleanup and Companion restart will be skipped'
} else {
    Invoke-RemoteCleanup -Root $RemoteRoot
}

foreach ($entry in $plan) {
    Deploy -Path $entry.Path -Dest $entry.Dest -ExcludeDirectoryName $entry.ExcludeDirectoryName
}

if ($NoStart) {
    Write-Host 'sync complete; start skipped by -NoStart'
    exit 0
}
if ($DryRun) {
    Write-Host 'dry run: start skipped'
    exit 0
}

& $StartScript -RemoteRoot $RemoteRoot
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
