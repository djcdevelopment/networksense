#Requires -Version 5.1
<#
.SYNOPSIS
Deploy one ComfyNetworkSense DLL to the AM4 Valheim server with SHA256 verification.

.DESCRIPTION
Stages the DLL over BatchMode SSH, verifies it before the atomic install, preserves the
previous DLL as a timestamped backup, restarts the dedicated-server container, and waits
for both the requested plugin version and the game-server-ready log line. The final JSON
receipt records the local, host-mounted, and container-visible hashes.
#>
[CmdletBinding()]
param(
    [string] $DllPath = '',

    [string] $SshTarget = 'am4',

    [string] $Container = 'comfy-valheim-server-am4-valheim-server-1',

    [string] $RemotePluginPath =
        '/home/derek/comfy-valheim-lab/server-state/config/bepinex/plugins/ComfyNetworkSense.dll',

    [string] $ContainerPluginPath =
        '/config/bepinex/plugins/ComfyNetworkSense.dll',

    [ValidateRange(30, 600)]
    [int] $ReadyWaitSeconds = 300,

    [string] $OutputPath = ''
)

$ErrorActionPreference = 'Stop'
$sshOptions = @('-o', 'BatchMode=yes', '-o', 'ConnectTimeout=8')

if ([string]::IsNullOrWhiteSpace($DllPath)) {
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    $DllPath = Join-Path $repoRoot `
        'network\mod\ComfyNetworkSense\bin\Release\ComfyNetworkSense.dll'
}
$dll = (Resolve-Path -LiteralPath $DllPath -ErrorAction Stop).Path
if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) {
    throw "DLL is not a file: $dll"
}
if ($SshTarget -notmatch '^[A-Za-z0-9._-]+$') {
    throw 'SshTarget must be an SSH alias or hostname token.'
}
if ($Container -notmatch '^[A-Za-z0-9._-]+$') {
    throw 'Container must be a Docker container-name token.'
}
foreach ($remotePath in @($RemotePluginPath, $ContainerPluginPath)) {
    if (-not $remotePath.StartsWith('/') -or
        $remotePath.Contains("'") -or
        $remotePath.Contains('"') -or
        $remotePath.Contains("`r") -or
        $remotePath.Contains("`n")) {
        throw 'Remote plugin paths must be absolute and contain no quotes or newlines.'
    }
}

$localHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $dll).Hash.ToLowerInvariant()
$versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($dll)
$version = [string]$versionInfo.FileVersion
if ([string]::IsNullOrWhiteSpace($version) -or $version -notmatch '^\d+\.\d+\.\d+$') {
    throw "DLL has no safe three-part file version: $version"
}

$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$remoteDirectory = $RemotePluginPath.Substring(0, $RemotePluginPath.LastIndexOf('/'))
$stagedPath = "$RemotePluginPath.new-$($localHash.Substring(0, 12))"
$backupPath = "$RemotePluginPath.backup-$stamp"

$null = & ssh @sshOptions $SshTarget 'true' 2>$null
if ($LASTEXITCODE -ne 0) {
    throw "AM4 SSH lane is unavailable: $SshTarget"
}

$prepare = "set -eu; install -d -m 755 '$remoteDirectory'; rm -f '$stagedPath'"
$null = & ssh @sshOptions $SshTarget $prepare
if ($LASTEXITCODE -ne 0) {
    throw 'AM4 staging-directory preparation failed.'
}

& scp -q @sshOptions -- $dll "${SshTarget}:$stagedPath"
if ($LASTEXITCODE -ne 0) {
    throw 'AM4 DLL staging copy failed.'
}

$install = @"
set -eu
actual=`$(sha256sum '$stagedPath' | cut -d' ' -f1)
test "`$actual" = '$localHash'
if [ -f '$RemotePluginPath' ]; then cp -p '$RemotePluginPath' '$backupPath'; fi
chmod 0644 '$stagedPath'
mv -f '$stagedPath' '$RemotePluginPath'
sha256sum '$RemotePluginPath'
"@
$hostInstallOutput = @(& ssh @sshOptions $SshTarget $install)
if ($LASTEXITCODE -ne 0) {
    throw 'AM4 atomic DLL install or staged SHA256 verification failed.'
}
$hostHash = (($hostInstallOutput -join "`n") -split '\s+')[0].ToLowerInvariant()
if ($hostHash -ne $localHash) {
    throw "AM4 host hash mismatch after install: local=$localHash host=$hostHash"
}

$restartStartedUtc = [DateTime]::UtcNow
$restartOutput = @(& ssh @sshOptions $SshTarget "docker restart '$Container'")
if ($LASTEXITCODE -ne 0) {
    throw "AM4 container restart failed: $($restartOutput -join ' ')"
}

$loaded = $false
$ready = $false
$deadline = (Get-Date).AddSeconds($ReadyWaitSeconds)
do {
    Start-Sleep -Seconds 2
    $since = $restartStartedUtc.ToString('yyyy-MM-ddTHH:mm:ssZ')
    $recentLogs = @(& ssh @sshOptions $SshTarget `
        "docker logs --since '$since' '$Container' 2>&1")
    if ($LASTEXITCODE -ne 0) {
        throw 'AM4 log read failed while waiting for restart readiness.'
    }
    $logText = $recentLogs -join "`n"
    $loaded = $logText -match ([regex]::Escape("Loading [ComfyNetworkSense $version]"))
    $ready = $logText -match 'Game server connected'
} until (($loaded -and $ready) -or (Get-Date) -ge $deadline)

$hashLine = @(& ssh @sshOptions $SshTarget `
    "docker exec '$Container' sha256sum '$ContainerPluginPath'")
if ($LASTEXITCODE -ne 0) {
    throw 'AM4 container-visible DLL hash read failed.'
}
$containerHash = (($hashLine -join "`n") -split '\s+')[0].ToLowerInvariant()
if ($containerHash -ne $localHash) {
    throw "AM4 container hash mismatch: local=$localHash container=$containerHash"
}
if (-not $loaded -or -not $ready) {
    throw "AM4 restart did not prove plugin_loaded=$loaded server_ready=$ready within $ReadyWaitSeconds seconds."
}

$receipt = [ordered]@{
    schema_version = 1
    event = 'am4_network_sense_deploy'
    timestamp_utc = [DateTime]::UtcNow.ToString('o')
    ssh_target = $SshTarget
    container = $Container
    version = $version
    local_path = $dll
    remote_plugin_path = $RemotePluginPath
    container_plugin_path = $ContainerPluginPath
    backup_path = $backupPath
    local_sha256 = $localHash
    host_sha256 = $hostHash
    container_sha256 = $containerHash
    plugin_loaded = $loaded
    server_ready = $ready
    result = 'passed'
}
$json = $receipt | ConvertTo-Json -Depth 6
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $outputFullPath = [IO.Path]::GetFullPath($OutputPath)
    $outputDirectory = Split-Path -Parent $outputFullPath
    if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
        New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
    }
    [IO.File]::WriteAllText(
        $outputFullPath,
        $json + [Environment]::NewLine,
        (New-Object Text.UTF8Encoding($false)))
}
$json
