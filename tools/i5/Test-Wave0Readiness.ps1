<#
.SYNOPSIS
Read-only Wave 0 readiness receipt for the P7, OMEN, and optional i5 alpha lane.

.DESCRIPTION
Collects the current public P7 release, local OMEN Companion status, retained
Companion captures, and a single optional i5 SSH/Companion probe. It does not
start Valheim, install files, or move players. Its purpose is to answer:

- what is already aligned;
- what evidence exists without Derek;
- what still needs a real two-client test when Derek returns.
#>
[CmdletBinding()]
param(
    [string]$GatewayUrl = 'https://comfy-p7.duckdns.org',
    [string]$OmenCompanionUrl = 'http://127.0.0.1:8080',
    [string]$OutputJson,
    [switch]$SummaryOnly
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$SshArgs = @('-o', 'BatchMode=yes', '-o', 'ConnectTimeout=8', 'i5')
$httpTimeoutSeconds = 15

function Get-Json {
    param([string]$Url)

    Invoke-RestMethod -Uri $Url -Method Get -Headers @{ 'Cache-Control' = 'no-cache' } -TimeoutSec $httpTimeoutSeconds
}

function New-Check {
    param([string]$Name, [bool]$Ok, [string]$Detail, [string]$Level = '')

    [ordered]@{
        name = $Name
        ok = $Ok
        level = if ($Level) { $Level } elseif ($Ok) { 'ok' } else { 'bad' }
        detail = $Detail
    }
}

function Try-GetRemoteJson {
    param([string]$Path)

    $remoteScript = @"
`$ErrorActionPreference = 'Stop'
Invoke-RestMethod -Uri 'http://127.0.0.1:8080$Path' -TimeoutSec $httpTimeoutSeconds | ConvertTo-Json -Depth 20 -Compress
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($remoteScript))
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $raw = & ssh @SshArgs powershell.exe -NoProfile -EncodedCommand $encoded 2>$null
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($exitCode -ne 0) {
        return [ordered]@{ ok = $false; error = "i5 query failed: $Path (ssh exit $exitCode)" }
    }
    try {
        return [ordered]@{ ok = $true; value = (($raw | Where-Object { $_ -and $_ -notmatch '^#< CLIXML' }) -join [Environment]::NewLine | ConvertFrom-Json) }
    } catch {
        return [ordered]@{ ok = $false; error = "i5 returned unreadable JSON: $Path" }
    }
}

function Find-HeartbeatAgeCapture {
    param($Captures, [string]$ExpectedGateway)

    foreach ($capture in @($Captures)) {
        $local = $capture.final_local_motion
        if (-not $local) { continue }
        if ($ExpectedGateway -and $capture.capture_identity -and
            [string]$capture.capture_identity.gateway_version -ne $ExpectedGateway) { continue }
        if ($null -ne $local.server_ping_age_ms -and $null -ne $local.server_ping_age_jitter_ms) {
            return $capture
        }
    }
    return $null
}

function Get-PlayerTestList {
    param(
        [string]$ExpectedModRelease,
        [string]$ExpectedPackageRelease
    )

    @(
        [ordered]@{
            id = 'i5-online-install'
            who = 'agent'
            requires_derek = $false
            task = "When i5 is online, run Sync-I5Companion.ps1 if needed and install/check package $ExpectedPackageRelease through i5 Companion."
            expected = "i5 Companion status installed.release == $ExpectedPackageRelease, installed.mod_release == $ExpectedModRelease, and package hash matches P7."
        },
        [ordered]@{
            id = 'two-client-join'
            who = 'derek'
            requires_derek = $true
            task = 'Start OMEN and i5 Valheim clients and join with the two owned player accounts.'
            expected = 'P7 /api/v0/telemetry/valheim reports peer_count 2 and observed player names.'
        },
        [ordered]@{
            id = 'idle-two-client-capture'
            who = 'agent'
            requires_derek = $false
            task = "Run Start-TwoClientCapture.ps1 -DurationSeconds 30 -IntervalSeconds 1 -Label $ExpectedPackageRelease-idle."
            expected = "Both summaries have bad_sample_count 0, Gateway $ExpectedModRelease identity, and peer count above zero."
        },
        [ordered]@{
            id = 'apply-observe-course'
            who = 'agent-plus-derek-visual'
            requires_derek = $true
            task = 'Run Start-Wave0LiveGate.ps1 -DesiredApplyClient omen while both clients are joined.'
            expected = 'Live gate sets OMEN APPLY and i5 OBSERVE ONLY, verifies that split, then runs bounded motion; Derek records visual smoothness/teleporting.'
        },
        [ordered]@{
            id = 'role-reversal'
            who = 'agent-plus-derek-visual'
            requires_derek = $true
            task = 'Run Start-Wave0LiveGate.ps1 -DesiredApplyClient i5 and annotate the reversal receipt.'
            expected = 'Live gate verifies i5 APPLY and OMEN OBSERVE ONLY; the visual result follows the role, not the machine or account.'
        }
    )
}

$gatewayRoot = $GatewayUrl.TrimEnd('/')
$omenRoot = $OmenCompanionUrl.TrimEnd('/')

$manifest = Get-Json "$gatewayRoot/api/v0/valheim/modpack/manifest"
$deployment = Get-Json "$gatewayRoot/api/v0/telemetry/deployment"
$valheim = Get-Json "$gatewayRoot/api/v0/telemetry/valheim"
$motion = Get-Json "$gatewayRoot/live/valheim-motion"
$omenStatus = Get-Json "$omenRoot/api/v0/companion/status"
$omenCaptures = Get-Json "$omenRoot/api/v0/companion/transport-capture"

$expectedRelease = [string]$manifest.mod_release
$expectedPackageRelease = [string]$manifest.release
$expectedPackageHash = [string]$manifest.package.sha256
$heartbeatAgeCapture = Find-HeartbeatAgeCapture $omenCaptures.captures $expectedRelease

$i5StatusResult = Try-GetRemoteJson '/api/v0/companion/status'
$i5Release = $null
if ($i5StatusResult.ok) {
    $i5Release = Try-GetRemoteJson '/api/v0/companion/update/check'
}

$checks = @()
$manifestReady = -not [string]::IsNullOrWhiteSpace($expectedPackageRelease) -and
    -not [string]::IsNullOrWhiteSpace($expectedRelease) -and
    -not [string]::IsNullOrWhiteSpace($expectedPackageHash)
$checks += New-Check 'p7-public-package-pointer' $manifestReady ("package_release=$expectedPackageRelease admitted_mod_release=$expectedRelease sha256=$expectedPackageHash")
$checks += New-Check 'p7-gateway-deployment' ([string]$deployment.lumberjacks_version -eq $expectedRelease) ("gateway=$($deployment.lumberjacks_version)")
$checks += New-Check 'p7-valheim-ready' (-not [bool]$valheim.stale -and [string]$valheim.heartbeat.server_state -eq 'ready') ("stale=$($valheim.stale) server_state=$($valheim.heartbeat.server_state) peers=$($valheim.heartbeat.peer_count)")
$omenPackageOk = [string]$omenStatus.installed.release -eq $expectedPackageRelease -and
    [string]$omenStatus.installed.mod_release -eq $expectedRelease -and
    [string]$omenStatus.installed.package_sha256 -eq $expectedPackageHash
$checks += New-Check 'omen-installed-package' $omenPackageOk ("package=$($omenStatus.installed.release) mod=$($omenStatus.installed.mod_release) sha256=$($omenStatus.installed.package_sha256)")
$checks += New-Check 'omen-profile-and-config' ([bool]$omenStatus.profile.linked -and [bool]$omenStatus.valheim.config_found) ("profile_linked=$($omenStatus.profile.linked) config_found=$($omenStatus.valheim.config_found)")
if ($heartbeatAgeCapture) {
    $checks += New-Check 'heartbeat-age-capture-field' $true ("run=$($heartbeatAgeCapture.run_id)")
} else {
    $checks += New-Check 'heartbeat-age-capture-field' $false ("no retained $expectedRelease capture with server_ping_age_ms/server_ping_age_jitter_ms; the next real capture should create it") 'warn'
}
$checks += New-Check 'motion-endpoint-readable' ($null -ne $motion.received -and $null -ne $motion.relayed_websocket) ("received=$($motion.received) relayed_ws=$($motion.relayed_websocket) relayed_udp=$($motion.relayed_udp)")

if ($i5StatusResult.ok) {
    $i5 = $i5StatusResult.value
    $i5Ok = [string]$i5.installed.release -eq $expectedPackageRelease -and
        [string]$i5.installed.mod_release -eq $expectedRelease -and
        [string]$i5.installed.package_sha256 -eq $expectedPackageHash -and
        [bool]$i5.profile.linked -and [bool]$i5.valheim.config_found
    $checks += New-Check 'i5-installed-package' $i5Ok ("package=$($i5.installed.release) mod=$($i5.installed.mod_release) sha256=$($i5.installed.package_sha256)")
} else {
    $checks += New-Check 'i5-installed-package' $false ($i5StatusResult.error) 'wait'
}

$failed = @($checks | Where-Object { -not $_.ok -and $_.level -notin @('wait', 'warn') })
$waiting = @($checks | Where-Object { -not $_.ok -and $_.level -eq 'wait' })
$readyForDerek = $failed.Count -eq 0 -and $waiting.Count -eq 0

$result = [ordered]@{
    schema_version = 1
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    expected_release = $expectedRelease
    expected_mod_release = $expectedRelease
    expected_package_release = $expectedPackageRelease
    expected_package_sha256 = $expectedPackageHash
    verdict = if ($failed.Count -gt 0) { 'blocked_by_failed_preflight' }
        elseif ($waiting.Count -gt 0) { 'waiting_for_optional_i5_or_real_clients' }
        else { 'ready_for_two_client_gate' }
    ready_for_derek = $readyForDerek
    checks = $checks
    p7 = [ordered]@{
        manifest = $manifest
        deployment = $deployment
        valheim = [ordered]@{
            stale = $valheim.stale
            server_state = $valheim.heartbeat.server_state
            peer_count = $valheim.heartbeat.peer_count
            players = @($valheim.heartbeat.players)
            instance_id = $valheim.heartbeat.instance_id
            motion_state = $valheim.heartbeat.motion_state
        }
        motion = $motion
    }
    omen = [ordered]@{
        status = $omenStatus
        latest_capture = if ($omenCaptures.captures) { $omenCaptures.captures[0] } else { $null }
        heartbeat_age_capture = $heartbeatAgeCapture
    }
    i5 = if ($i5StatusResult.ok) {
        [ordered]@{ ok = $true; status = $i5StatusResult.value; update_check = $i5Release.value }
    } else {
        [ordered]@{ ok = $false; error = $i5StatusResult.error }
    }
    derek_return_tests = Get-PlayerTestList $expectedRelease $expectedPackageRelease
}

function Write-Summary {
    param($Receipt)

    Write-Host ("Wave 0 readiness: {0}" -f $Receipt.verdict)
    Write-Host ("Expected package/mod: {0} / {1}" -f $Receipt.expected_package_release, $Receipt.expected_mod_release)
    foreach ($check in $Receipt.checks) {
        $mark = if ($check.ok) { 'OK' } elseif ($check.level -eq 'wait') { 'WAIT' } elseif ($check.level -eq 'warn') { 'WARN' } else { 'FAIL' }
        Write-Host ("[{0}] {1} - {2}" -f $mark, $check.name, $check.detail)
    }
    Write-Host ''
    Write-Host 'Tests for Derek/real clients:'
    foreach ($test in $Receipt.derek_return_tests) {
        $human = if ($test.requires_derek) { 'Derek needed' } else { 'agent can run' }
        Write-Host ("- {0}: {1}; expected: {2}" -f $test.id, $human, $test.expected)
    }
}

Write-Summary $result
$json = $result | ConvertTo-Json -Depth 20
if ($OutputJson) {
    $path = [IO.Path]::GetFullPath($OutputJson)
    $dir = Split-Path -Parent $path
    if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    [IO.File]::WriteAllText($path, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    Write-Host ("Receipt JSON: {0}" -f $path)
}
if (-not $SummaryOnly) { $json }
