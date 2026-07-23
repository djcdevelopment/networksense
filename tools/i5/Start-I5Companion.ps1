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
$sshArgs = @('-o', 'BatchMode=yes', '-o', 'ConnectTimeout=8', 'i5')

$remote = @'
$ErrorActionPreference = 'Stop'
$remoteRoot = '__REMOTE_ROOT__'
$valheim = '__VALHEIM_PATH__'
if (-not (Test-Path -LiteralPath $remoteRoot)) { throw "Companion staging root not found: $remoteRoot" }
if (-not (Test-Path -LiteralPath $valheim)) { throw "Valheim path not found: $valheim" }

Set-Location $remoteRoot
$envFile = Join-Path $remoteRoot 'tools\companion\.env'
Set-Content -LiteralPath $envFile -Value ('LUMBERJACKS_VALHEIM_HOST_PATH=' + $valheim) -Encoding ascii

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
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($escaped))
ssh @sshArgs "powershell.exe -NoProfile -EncodedCommand $encoded"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
