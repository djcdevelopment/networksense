<#
.SYNOPSIS
Start the Docker-backed Lumberjacks Companion on the i5 with the Valheim directory mounted.

.DESCRIPTION
Runs over the existing `i5` ssh alias. The script writes a project-local compose env file on the
i5, then starts the Companion with both docker-compose.yml and docker-compose.valheim.yml.

This is the durable i5 lane for the Companion updater. Starting with only the base compose file
creates a read-only dashboard: it cannot see /valheim, cannot find the ComfyNetworkSense config,
and cannot perform client-pull mod updates.

.PARAMETER RemoteRoot
The baseline staging checkout on the i5.

.PARAMETER ValheimPath
The Windows Valheim install path on the i5.

.EXAMPLE
.\tools\i5\Start-I5Companion.ps1
#>
[CmdletBinding()]
param(
    [string]$RemoteRoot = 'C:\deploy\baseline\i5-companion',
    [string]$ValheimPath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
)

$ErrorActionPreference = 'Stop'
$sshOptions = @('-o', 'BatchMode=yes', '-o', 'ConnectTimeout=8')
$sshAlias = 'i5'
$sshArgs = $sshOptions + $sshAlias

$remote = @'
$ErrorActionPreference = 'Stop'
$remoteRoot = '__REMOTE_ROOT__'
$valheim = '__VALHEIM_PATH__'
if (-not (Test-Path -LiteralPath $remoteRoot)) { throw "Companion staging root not found: $remoteRoot" }
if (-not (Test-Path -LiteralPath $valheim)) { throw "Valheim path not found: $valheim" }

Set-Location $remoteRoot
$envFile = Join-Path $remoteRoot 'tools\companion\.env'
$latestBootstrapFile = Join-Path $remoteRoot 'tools\companion\latest-bootstrap.json'
$bootstrapRelease = 'unknown'
if (Test-Path -LiteralPath $latestBootstrapFile) {
    try {
        $latestBootstrap = Get-Content -LiteralPath $latestBootstrapFile -Raw | ConvertFrom-Json
        if ($latestBootstrap.release) { $bootstrapRelease = $latestBootstrap.release }
    } catch {
        Write-Output ("Could not read latest bootstrap pointer: {0}" -f $_.Exception.Message)
    }
}
Set-Content -LiteralPath $envFile -Value @(
    'LUMBERJACKS_VALHEIM_HOST_PATH=' + $valheim
    'LUMBERJACKS_COMPANION_BOOTSTRAP_RELEASE=' + $bootstrapRelease
) -Encoding ascii

function Test-DockerServer {
    param([int]$TimeoutSeconds = 8)

    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        return [pscustomobject]@{ Ok = $false; Detail = 'docker CLI not found on i5' }
    }

    $pipe = Test-Path -LiteralPath '\\.\pipe\dockerDesktopLinuxEngine'
    $job = Start-Job -ScriptBlock { docker version --format '{{.Server.Version}}' 2>&1 }
    if (-not (Wait-Job $job -Timeout $TimeoutSeconds)) {
        Stop-Job $job -ErrorAction SilentlyContinue
        Remove-Job $job -Force -ErrorAction SilentlyContinue
        return [pscustomobject]@{ Ok = $false; Detail = "docker server did not answer within $TimeoutSeconds seconds; linux engine pipe present=$pipe" }
    }

    $output = Receive-Job $job -ErrorAction SilentlyContinue
    Remove-Job $job -Force -ErrorAction SilentlyContinue
    if ($output) {
        $server = ($output | Select-Object -Last 1).ToString().Trim()
        if ($server -and $server -notmatch 'error|failed|Cannot connect|pipe') {
            return [pscustomobject]@{ Ok = $true; Detail = "Docker server $server" }
        }
        return [pscustomobject]@{ Ok = $false; Detail = (($output | Out-String) -replace '\s+', ' ').Trim() }
    }

    return [pscustomobject]@{ Ok = $false; Detail = "docker server returned no version; linux engine pipe present=$pipe" }
}

function Wait-DockerServer {
    param([int]$Seconds = 45)

    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        $state = Test-DockerServer -TimeoutSeconds 8
        if ($state.Ok) { return $state }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)
    return $state
}

$dockerState = Test-DockerServer -TimeoutSeconds 8
if (-not $dockerState.Ok) {
    $task = schtasks /Query /TN LumberjacksDockerDesktop /FO LIST 2>$null
    if ($LASTEXITCODE -eq 0) {
        schtasks /Run /TN LumberjacksDockerDesktop | Out-Null
        $dockerState = Wait-DockerServer -Seconds 45
    }
}
if (-not $dockerState.Ok) {
    throw "Docker Desktop Linux engine is not ready on i5: $($dockerState.Detail). Log into i5 once and confirm Docker Desktop is running, then rerun Start-I5Companion.ps1. Do not start the Companion with the base compose file only."
}
Write-Output ("Docker ready: {0}" -f $dockerState.Detail)

$composeArgs = @(
    'compose',
    '-p', 'lumberjacks-companion',
    '--env-file', $envFile,
    '-f', '.\tools\companion\docker-compose.yml',
    '-f', '.\tools\companion\docker-compose.valheim.yml',
    'up', '-d', '--build'
)
& docker @composeArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$ready = $false
for ($i = 0; $i -lt 30; $i++) {
    try {
        Invoke-RestMethod http://127.0.0.1:8080/health | Out-Null
        $ready = $true
        break
    } catch {
        Start-Sleep -Seconds 1
    }
}
if (-not $ready) {
    docker logs --tail 100 lumberjacks-companion-companion-1
    throw 'Companion did not answer on http://127.0.0.1:8080.'
}

$status = Invoke-RestMethod http://127.0.0.1:8080/api/v0/companion/status
$status | ConvertTo-Json -Depth 8
'@

$escaped = $remote.
    Replace('__REMOTE_ROOT__', $RemoteRoot.Replace("'", "''")).
    Replace('__VALHEIM_PATH__', $ValheimPath.Replace("'", "''"))

$remoteScriptDir = 'C:/deploy/baseline'
$remoteScript = "$remoteScriptDir/Start-I5Companion.remote.ps1"
$mkRemoteScriptDir = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes("New-Item -ItemType Directory -Force -Path '$remoteScriptDir' | Out-Null"))
ssh @sshArgs "powershell.exe -NoProfile -EncodedCommand $mkRemoteScriptDir"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$tmp = New-TemporaryFile
try {
    Set-Content -LiteralPath $tmp.FullName -Value $escaped -Encoding UTF8
    scp -q @sshOptions $tmp.FullName "${sshAlias}:$remoteScript"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    ssh @sshArgs "powershell.exe -NoProfile -ExecutionPolicy Bypass -File $remoteScript"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} finally {
    Remove-Item -LiteralPath $tmp.FullName -Force -ErrorAction SilentlyContinue
}
