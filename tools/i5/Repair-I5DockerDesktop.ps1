<#
.SYNOPSIS
Recover or diagnose the i5 Docker Desktop Linux engine and Companion container.

.DESCRIPTION
Runs over the existing `i5` SSH alias. The script is intentionally bounded:
it touches only Docker Desktop service/processes and, unless -NoCompanionStart
is provided, the staged Lumberjacks Companion compose project under -RemoteRoot.

This is the repair lane for the failure mode where the i5 SSH lane is alive but
Docker CLI calls hang, the Linux engine pipe is missing, or the Companion
container is stuck in Created/zombie state after rapid rebuilds.

.PARAMETER RemoteRoot
The baseline staging checkout on the i5.

.PARAMETER NoCompanionStart
Recover Docker Desktop only; do not recreate/start the Companion compose service.

.PARAMETER WaitSeconds
Maximum seconds to wait for the Docker Linux engine after each recovery action.

.EXAMPLE
.\tools\i5\Repair-I5DockerDesktop.ps1

.EXAMPLE
.\tools\i5\Repair-I5DockerDesktop.ps1 -NoCompanionStart
#>
[CmdletBinding()]
param(
    [string]$RemoteRoot = 'C:\deploy\baseline\i5-companion',
    [switch]$NoCompanionStart,
    [ValidateRange(30,600)]
    [int]$WaitSeconds = 240
)

$ErrorActionPreference = 'Stop'
$sshOptions = @('-o', 'BatchMode=yes', '-o', 'ConnectTimeout=8')
$sshAlias = 'i5'
$sshArgs = $sshOptions + $sshAlias

$remote = @'
$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
$remoteRoot = '__REMOTE_ROOT__'
$noCompanionStart = [bool]::Parse('__NO_COMPANION_START__')
$waitSeconds = [int]'__WAIT_SECONDS__'
$docker = 'C:\Program Files\Docker\Docker\resources\bin\docker.exe'
$dockerDesktop = 'C:\Program Files\Docker\Docker\Docker Desktop.exe'

function Invoke-DockerProbe {
    param([int]$TimeoutSeconds = 10)

    if (-not (Test-Path -LiteralPath $docker)) {
        return [ordered]@{ ok = $false; detail = 'docker.exe_not_found'; version = $null; os_type = $null; linux_pipe = $false }
    }

    $linuxPipe = Test-Path -LiteralPath '\\.\pipe\dockerDesktopLinuxEngine'
    $job = Start-Job -ScriptBlock {
        param($Docker)
        $version = & $Docker version --format '{{.Server.Version}}' 2>&1
        if ($LASTEXITCODE -ne 0) { return [pscustomobject]@{ ok = $false; output = (($version | Out-String) -replace '\s+', ' ').Trim() } }
        $os = & $Docker info --format '{{.OSType}}' 2>&1
        if ($LASTEXITCODE -ne 0) { return [pscustomobject]@{ ok = $false; output = (($os | Out-String) -replace '\s+', ' ').Trim() } }
        return [pscustomobject]@{ ok = ($os -eq 'linux'); version = [string]$version; os_type = [string]$os; output = "version=$version os=$os" }
    } -ArgumentList $docker

    if (-not (Wait-Job $job -Timeout $TimeoutSeconds)) {
        Stop-Job $job -ErrorAction SilentlyContinue
        Remove-Job $job -Force -ErrorAction SilentlyContinue
        return [ordered]@{ ok = $false; detail = "docker_probe_timeout_${TimeoutSeconds}s"; version = $null; os_type = $null; linux_pipe = $linuxPipe }
    }

    $result = Receive-Job $job -ErrorAction SilentlyContinue | Select-Object -Last 1
    Remove-Job $job -Force -ErrorAction SilentlyContinue
    if ($result -and $result.ok) {
        return [ordered]@{ ok = $true; detail = 'docker_linux_ready'; version = $result.version; os_type = $result.os_type; linux_pipe = $linuxPipe }
    }
    return [ordered]@{ ok = $false; detail = if ($result) { [string]$result.output } else { 'docker_probe_no_output' }; version = $null; os_type = $null; linux_pipe = $linuxPipe }
}

function Wait-DockerLinux {
    param([int]$Seconds)

    $deadline = (Get-Date).AddSeconds($Seconds)
    $last = $null
    do {
        $last = Invoke-DockerProbe -TimeoutSeconds 10
        if ($last.ok) { return $last }
        Start-Sleep -Seconds 5
    } while ((Get-Date) -lt $deadline)
    return $last
}

function Get-DockerProcessSummary {
    @(Get-Process | Where-Object { $_.ProcessName -like '*Docker*' -or $_.ProcessName -like 'com.docker*' } |
        Select-Object ProcessName,Id)
}

$events = @()
$initialService = Get-Service com.docker.service -ErrorAction SilentlyContinue
$initialProbe = Invoke-DockerProbe -TimeoutSeconds 10
$events += [ordered]@{
    action = 'initial_probe'
    service_status = if ($initialService) { [string]$initialService.Status } else { 'missing' }
    probe = $initialProbe
    processes = Get-DockerProcessSummary
}

$probe = $initialProbe
if (-not $probe.ok) {
    Restart-Service com.docker.service -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $dockerDesktop) {
        Start-Process -WindowStyle Hidden $dockerDesktop -ErrorAction SilentlyContinue
    }
    $probe = Wait-DockerLinux -Seconds $waitSeconds
    $events += [ordered]@{ action = 'soft_recovery'; probe = $probe; processes = Get-DockerProcessSummary }
}

if (-not $probe.ok) {
    Get-Process | Where-Object { $_.ProcessName -in @('Docker Desktop','com.docker.backend','com.docker.build','docker-agent') } |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Restart-Service com.docker.service -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $dockerDesktop) {
        Start-Process -WindowStyle Hidden $dockerDesktop -ErrorAction SilentlyContinue
    }
    $probe = Wait-DockerLinux -Seconds $waitSeconds
    $events += [ordered]@{ action = 'hard_recovery'; probe = $probe; processes = Get-DockerProcessSummary }
}

$companion = [ordered]@{ attempted = $false; ok = $false; detail = 'not_requested'; status = $null; init = $null }
if ($probe.ok -and -not $noCompanionStart) {
    $companion.attempted = $true
    $compose = Join-Path $remoteRoot 'tools\companion\docker-compose.yml'
    $composeValheim = Join-Path $remoteRoot 'tools\companion\docker-compose.valheim.yml'
    $envFile = Join-Path $remoteRoot 'tools\companion\.env'
    if (-not (Test-Path -LiteralPath $remoteRoot)) {
        $companion.detail = "remote_root_missing: $remoteRoot"
    } elseif (-not (Test-Path -LiteralPath $compose) -or -not (Test-Path -LiteralPath $composeValheim) -or -not (Test-Path -LiteralPath $envFile)) {
        $companion.detail = 'companion_compose_inputs_missing'
    } else {
        Set-Location $remoteRoot
        & $docker compose -p lumberjacks-companion --env-file $envFile -f '.\tools\companion\docker-compose.yml' -f '.\tools\companion\docker-compose.valheim.yml' up -d --build --force-recreate 2>&1 | Out-String | Out-Null
        if ($LASTEXITCODE -ne 0) {
            $companion.detail = "docker_compose_failed_exit_$LASTEXITCODE"
        } else {
            $healthy = $false
            for ($i = 0; $i -lt 45; $i++) {
                try {
                    Invoke-RestMethod -TimeoutSec 5 http://127.0.0.1:8080/health | Out-Null
                    $healthy = $true
                    break
                } catch {
                    Start-Sleep -Seconds 1
                }
            }
            if ($healthy) {
                $status = Invoke-RestMethod -TimeoutSec 10 http://127.0.0.1:8080/api/v0/companion/status
                $packet = Invoke-RestMethod -TimeoutSec 10 http://127.0.0.1:8080/api/v0/companion/wave0/packet
                $init = & $docker inspect lumberjacks-companion-companion-1 --format '{{json .HostConfig.Init}}' 2>$null
                $companion.ok = $true
                $companion.detail = 'companion_ready'
                $companion.init = [string]$init
                $companion.status = [ordered]@{
                    valheim_found = [bool]$status.valheim.found
                    config_found = [bool]$status.valheim.config_found
                    profile_linked = [bool]$status.profile.linked
                    installed_release = [string]$status.installed.release
                    wave0_verdict = [string]$packet.verdict
                    has_wave0_commands = [bool]$packet.commands.live_omen_applies
                    has_defect_fallback = [bool]$packet.commands.retain_named_defect
                    raw_enrollment_in_status = @($status.profile.PSObject.Properties.Name) -contains 'enrollment_id'
                }
            } else {
                $companion.detail = 'companion_health_timeout'
            }
        }
    }
}

$verdict =
    if (-not $probe.ok) { 'docker_linux_engine_unavailable' }
    elseif ($noCompanionStart) { 'docker_linux_engine_ready' }
    elseif ($companion.ok) { 'docker_and_companion_ready' }
    else { 'docker_ready_companion_not_ready' }

[ordered]@{
    schema_version = 1
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    verdict = $verdict
    docker = $probe
    companion = $companion
    events = $events
} | ConvertTo-Json -Depth 10
'@

$escaped = $remote.
    Replace('__REMOTE_ROOT__', $RemoteRoot.Replace("'", "''")).
    Replace('__NO_COMPANION_START__', $NoCompanionStart.IsPresent.ToString()).
    Replace('__WAIT_SECONDS__', $WaitSeconds.ToString([Globalization.CultureInfo]::InvariantCulture))

$remoteScriptDir = 'C:/deploy/baseline'
$remoteScript = "$remoteScriptDir/Repair-I5DockerDesktop.remote.ps1"
$mkRemoteScriptDir = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes("New-Item -ItemType Directory -Force -Path '$remoteScriptDir' | Out-Null"))
ssh @sshArgs "powershell.exe -NoProfile -EncodedCommand $mkRemoteScriptDir"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$tmp = New-TemporaryFile
try {
    Set-Content -LiteralPath $tmp.FullName -Value $escaped -Encoding UTF8
    scp -q @sshOptions $tmp.FullName "${sshAlias}:$remoteScript"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $output = ssh @sshArgs "powershell.exe -NoProfile -ExecutionPolicy Bypass -File $remoteScript"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $json = (@($output) | Where-Object { $_ -and ($_ -notmatch '^#< CLIXML') }) -join [Environment]::NewLine
    $receipt = $json | ConvertFrom-Json
    $receipt | ConvertTo-Json -Depth 10
    if ($receipt.verdict -notin @('docker_linux_engine_ready', 'docker_and_companion_ready')) { exit 1 }
} finally {
    Remove-Item -LiteralPath $tmp.FullName -Force -ErrorAction SilentlyContinue
}
