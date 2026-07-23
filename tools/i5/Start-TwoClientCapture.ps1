<#
.SYNOPSIS
Start concurrent Companion transport captures on OMEN and i5.

.DESCRIPTION
Runs one local capture against OMEN's Companion at http://127.0.0.1:8080 and one remote capture
against the i5 Companion through the existing `i5` SSH alias. This is the operator command for
two-client movement tests: start it, move both characters during the window, then compare the two
summaries.

The command does not start Valheim and does not modify mod files. It only POSTs to each local
Companion's capture endpoint.

.PARAMETER DurationSeconds
Capture duration for both machines. Default: 30.

.PARAMETER IntervalSeconds
Sampling interval for both machines. Default: 1.

.PARAMETER Label
Capture label suffix. A timestamp and machine label are added automatically.

.PARAMETER BundleDirectory
Optional local directory where both evidence bundle zips should be collected. If omitted, bundles
remain in each Companion and can be downloaded from that machine's dashboard.

.EXAMPLE
.\tools\i5\Start-TwoClientCapture.ps1 -DurationSeconds 30 -IntervalSeconds 1 -Label sprint-stutter
#>
[CmdletBinding()]
param(
    [ValidateRange(5, 300)]
    [int]$DurationSeconds = 30,

    [ValidateRange(1, 60)]
    [int]$IntervalSeconds = 1,

    [string]$Label = 'two-client',

    [string]$BundleDirectory
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$SshArgs = @('-o', 'BatchMode=yes', '-o', 'ConnectTimeout=8', 'i5')
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$safeLabel = ($Label -replace '[^A-Za-z0-9._-]', '-').Trim('-')
if ([string]::IsNullOrWhiteSpace($safeLabel)) { $safeLabel = 'two-client' }

function New-RemoteCaptureScript {
    param([string]$Machine)

@"
`$ErrorActionPreference = 'Stop'
`$ProgressPreference = 'SilentlyContinue'
`$body = @{
    duration_seconds = $DurationSeconds
    interval_seconds = $IntervalSeconds
    label = '$safeLabel-$Machine'
} | ConvertTo-Json -Compress
Invoke-RestMethod -Method Post -ContentType 'application/json' -Body `$body 'http://127.0.0.1:8080/api/v0/companion/transport-capture' | ConvertTo-Json -Depth 12
"@
}

Write-Host "starting concurrent capture: duration=${DurationSeconds}s interval=${IntervalSeconds}s label=$stamp-$safeLabel"

$localJob = Start-Job -ScriptBlock {
    $body = @{
        duration_seconds = $using:DurationSeconds
        interval_seconds = $using:IntervalSeconds
        label = "$using:safeLabel-omen"
    } | ConvertTo-Json -Compress
    Invoke-RestMethod -Method Post -ContentType 'application/json' -Body $body 'http://127.0.0.1:8080/api/v0/companion/transport-capture'
}

$remoteScript = New-RemoteCaptureScript -Machine 'i5'
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($remoteScript))
$remoteJob = Start-Job -ScriptBlock {
    param($Encoded, $Ssh)
    ssh @Ssh "powershell.exe -NoProfile -EncodedCommand $Encoded"
    if ($LASTEXITCODE -ne 0) { throw "i5 capture command failed with exit code $LASTEXITCODE" }
} -ArgumentList $encoded, $SshArgs

Wait-Job -Job $localJob, $remoteJob | Out-Null

$localError = $null
$remoteError = $null
$local = $null
$remote = $null
try {
    $local = Receive-Job $localJob -ErrorAction Stop
    foreach ($property in 'PSComputerName', 'RunspaceId', 'PSShowComputerName') {
        $local.PSObject.Properties.Remove($property)
    }
} catch { $localError = $_.Exception.Message }
try {
    $remoteText = Receive-Job $remoteJob -ErrorAction Stop | Where-Object { $_ -and $_ -notmatch '^#< CLIXML' }
    $remote = ($remoteText | Out-String).Trim() | ConvertFrom-Json
} catch { $remoteError = $_.Exception.Message }
Remove-Job $localJob, $remoteJob -Force -ErrorAction SilentlyContinue

function Get-CounterDelta {
    param($Summary, [string]$Name)

    if (-not $Summary -or -not $Summary.counter_ranges) { return $null }
    $counter = $Summary.counter_ranges.$Name
    if (-not $counter) { return $null }
    return $counter.delta
}

function Get-CaptureBrief {
    param($Summary)

    if (-not $Summary) { return $null }
    [ordered]@{
        run_id = $Summary.run_id
        verdict = $Summary.verdict
        samples = $Summary.sample_count
        bad_samples = $Summary.bad_sample_count
        max_peers = $Summary.max_peers
        players = @($Summary.observed_players)
        motion_received_delta = $Summary.motion_received_delta
        motion_relayed_delta = Get-CounterDelta $Summary 'motion_relayed'
        pending_delta = Get-CounterDelta $Summary 'pending'
        acknowledged_delta = Get-CounterDelta $Summary 'acknowledged'
        applied_delta = Get-CounterDelta $Summary 'applied'
        gateway = $Summary.capture_identity.gateway_version
        mod = $Summary.capture_identity.valheim_mod_version
        cutover = $Summary.capture_identity.cutover_mode
        final = $Summary.final_current_read.text
    }
}

function Get-IntValue {
    param($Value)

    if ($null -eq $Value) { return 0 }
    return [int]$Value
}

function Compare-Captures {
    param($Omen, $I5, [string]$OmenError, [string]$I5Error)

    if ($OmenError -or $I5Error) {
        return [ordered]@{
            level = 'bad'
            headline = 'One or both capture commands failed.'
            next_action = 'Fix the failed Companion lane before using this run as movement evidence.'
            evidence = (@($OmenError, $I5Error) | Where-Object { $_ }) -join ' | '
            omen = if ($OmenError) { @{ ok = $false; error = $OmenError } } else { Get-CaptureBrief $Omen }
            i5 = if ($I5Error) { @{ ok = $false; error = $I5Error } } else { Get-CaptureBrief $I5 }
        }
    }

    $omenMotion = Get-IntValue $Omen.motion_received_delta
    $i5Motion = Get-IntValue $I5.motion_received_delta
    $omenPeers = Get-IntValue $Omen.max_peers
    $i5Peers = Get-IntValue $I5.max_peers
    $badSamples = (Get-IntValue $Omen.bad_sample_count) + (Get-IntValue $I5.bad_sample_count)

    if ($badSamples -gt 0) {
        $level = 'bad'
        $headline = 'Capture had incomplete telemetry.'
        $next = 'Do not use this as a transport verdict. Re-run after both Companion dashboards show readable Gateway, Valheim, cutover, and motion telemetry.'
    } elseif ($omenMotion -gt 0 -or $i5Motion -gt 0) {
        $level = 'ok'
        $headline = 'Lumberjacks motion frames advanced during the two-client window.'
        $next = 'Use the samples/bundles to correlate perceived movement against motion counter deltas and player names.'
    } elseif ($omenPeers -gt 0 -or $i5Peers -gt 0) {
        $level = 'wait'
        $headline = 'Peers were present, but Lumberjacks motion counters did not advance.'
        $next = 'Treat visible movement as native Valheim for this run; inspect mod-side motion publish path before tuning interpolation.'
    } else {
        $level = 'wait'
        $headline = 'No active peer window was captured.'
        $next = 'Start this command after both clients are joining or immediately before moving both characters.'
    }

    $players = @(@($Omen.observed_players) + @($I5.observed_players)) |
        Where-Object { $_ } |
        Sort-Object -Unique
    if ($null -eq $players) { $players = @() }

    return [ordered]@{
        level = $level
        headline = $headline
        next_action = $next
        evidence = "omen peers=$omenPeers motion_delta=$omenMotion; i5 peers=$i5Peers motion_delta=$i5Motion; bad_samples=$badSamples"
        observed_players = @($players)
        omen = Get-CaptureBrief $Omen
        i5 = Get-CaptureBrief $I5
    }
}

$comparison = Compare-Captures $local $remote $localError $remoteError
$bundles = @()

if ($BundleDirectory) {
    $bundleRoot = [IO.Path]::GetFullPath($BundleDirectory)
    New-Item -ItemType Directory -Force -Path $bundleRoot | Out-Null

    if ($local -and $local.run_id) {
        $omenBundle = Join-Path $bundleRoot ("omen-" + $local.run_id + ".zip")
        Invoke-WebRequest -Uri ("http://127.0.0.1:8080/api/v0/companion/transport-capture/{0}/bundle.zip" -f [uri]::EscapeDataString($local.run_id)) -OutFile $omenBundle
        $bundles += [ordered]@{
            machine = 'omen'
            path = $omenBundle
            bytes = (Get-Item -LiteralPath $omenBundle).Length
        }
    }

    if ($remote -and $remote.run_id) {
        $remoteTemp = "C:\deploy\baseline\capture-bundles\i5-$($remote.run_id).zip"
        $remoteFetch = @"
`$ErrorActionPreference = 'Stop'
`$ProgressPreference = 'SilentlyContinue'
New-Item -ItemType Directory -Force -Path 'C:\deploy\baseline\capture-bundles' | Out-Null
Invoke-WebRequest -Uri 'http://127.0.0.1:8080/api/v0/companion/transport-capture/$($remote.run_id)/bundle.zip' -OutFile '$remoteTemp'
Get-Item -LiteralPath '$remoteTemp' | Select-Object -ExpandProperty Length
"@
        $encodedFetch = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($remoteFetch))
        $remoteSize = ssh @SshArgs "powershell.exe -NoProfile -EncodedCommand $encodedFetch"
        if ($LASTEXITCODE -ne 0) { throw "i5 bundle fetch failed for $($remote.run_id)" }

        $i5Bundle = Join-Path $bundleRoot ("i5-" + $remote.run_id + ".zip")
        scp -q -o BatchMode=yes -o ConnectTimeout=8 "i5:$remoteTemp" $i5Bundle
        if ($LASTEXITCODE -ne 0) { throw "i5 bundle copy failed for $($remote.run_id)" }

        $bundles += [ordered]@{
            machine = 'i5'
            path = $i5Bundle
            bytes = (Get-Item -LiteralPath $i5Bundle).Length
            remote_bytes = [int64]($remoteSize | Select-Object -Last 1)
        }
    }
}

$result = [ordered]@{
    schema_version = 1
    started_label = "$stamp-$safeLabel"
    duration_seconds = $DurationSeconds
    interval_seconds = $IntervalSeconds
    comparison = $comparison
    bundles = $bundles
    omen = if ($localError) { @{ ok = $false; error = $localError } } else { $local }
    i5 = if ($remoteError) { @{ ok = $false; error = $remoteError } } else { $remote }
}

$result | ConvertTo-Json -Depth 12
