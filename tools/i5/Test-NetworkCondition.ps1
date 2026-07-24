<#
.SYNOPSIS
Read-only analysis of retained client-local NetworkSense samples on OMEN and i5.

.DESCRIPTION
This does not start Valheim or change config. It reads the last bounded JSONL window from
each client and reports sample age, heartbeat-age variation, frame timing, and the current
classification. It is intended to distinguish a persistent path condition from a stale or
single-sample spike before changing transport code.
#>
[CmdletBinding()]
param(
    [ValidateRange(10, 500)] [int]$SampleCount = 120,

    [ValidateRange(30, 86400)] [int]$StaleAfterSeconds = 300
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$LocalPath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim\BepInEx\config\comfy-network-sense\telemetry-client.jsonl'
$SshArgs = @('-o', 'BatchMode=yes', '-o', 'ConnectTimeout=8', 'i5')

function Get-LocalSamples {
    if (-not (Test-Path -LiteralPath $LocalPath)) { return @() }
    @(Get-Content -LiteralPath $LocalPath -Tail $SampleCount | ForEach-Object {
        try { $_ | ConvertFrom-Json } catch { }
    })
}

function Get-RemoteSamples {
    $remote = @'
$p = '__PATH__'
if (Test-Path -LiteralPath $p) {
    Get-Content -LiteralPath $p -Tail __COUNT__
} else {
    Write-Output '__MISSING__'
}
'@
    $remote = $remote.Replace('__PATH__', $LocalPath.Replace("'", "''")).Replace('__COUNT__', [string]$SampleCount)
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($remote))
    $raw = & ssh @SshArgs powershell.exe -NoProfile -EncodedCommand $encoded 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'i5 telemetry query failed' }
    @($raw | Where-Object { $_ -and $_ -notmatch '^#< CLIXML' -and $_ -ne '__MISSING__' } | ForEach-Object {
        try { $_ | ConvertFrom-Json } catch { }
    })
}

function Get-Percentile([double[]]$Values, [double]$Percentile) {
    if (-not $Values -or $Values.Count -eq 0) { return $null }
    $sorted = @($Values | Sort-Object)
    $index = [Math]::Max(0, [Math]::Ceiling($sorted.Count * $Percentile) - 1)
    return [Math]::Round([double]$sorted[$index], 2)
}

function Summarize([string]$Name, [object[]]$Samples) {
    $valid = @($Samples | Where-Object { $_.timestamp_utc -and ($null -ne $_.server_ping_age_ms -or $null -ne $_.rtt_ms) -and ($null -ne $_.server_ping_age_jitter_ms -or $null -ne $_.jitter_ms) })
    if ($valid.Count -eq 0) {
        return [ordered]@{ client = $Name; samples = 0; condition = 'no_data' }
    }
    $pingAge = @($valid | ForEach-Object { if ($null -ne $_.server_ping_age_ms) { [double]$_.server_ping_age_ms } else { [double]$_.rtt_ms } })
    $variation = @($valid | ForEach-Object { if ($null -ne $_.server_ping_age_jitter_ms) { [double]$_.server_ping_age_jitter_ms } else { [double]$_.jitter_ms } })
    $frames = @($valid | Where-Object { $null -ne $_.frame_time_p95_ms } | ForEach-Object { [double]$_.frame_time_p95_ms })
    $last = $valid | Select-Object -Last 1
    $age = ([DateTimeOffset]::UtcNow - [DateTimeOffset]::Parse([string]$last.timestamp_utc)).TotalSeconds
    $condition = if ($age -gt $StaleAfterSeconds) { 'stale_samples' }
        elseif (($pingAge | Measure-Object -Maximum).Maximum -ge 500 -or ($variation | Measure-Object -Maximum).Maximum -ge 250) { 'severe_variance' }
        elseif (($pingAge | Measure-Object -Maximum).Maximum -ge 200 -or ($variation | Measure-Object -Maximum).Maximum -ge 100) { 'elevated' }
        else { 'stable' }
    [ordered]@{
        client = $Name
        player = $last.player_name
        samples = $valid.Count
        latest_utc = $last.timestamp_utc
        latest_age_seconds = [Math]::Round($age, 1)
        stale_after_seconds = $StaleAfterSeconds
        server_ping_age_min_ms = [Math]::Round(($pingAge | Measure-Object -Minimum).Minimum, 2)
        server_ping_age_avg_ms = [Math]::Round(($pingAge | Measure-Object -Average).Average, 2)
        server_ping_age_p95_ms = Get-Percentile $pingAge 0.95
        server_ping_age_max_ms = [Math]::Round(($pingAge | Measure-Object -Maximum).Maximum, 2)
        server_ping_age_variation_avg_ms = [Math]::Round(($variation | Measure-Object -Average).Average, 2)
        server_ping_age_variation_p95_ms = Get-Percentile $variation 0.95
        server_ping_age_variation_max_ms = [Math]::Round(($variation | Measure-Object -Maximum).Maximum, 2)
        frame_time_p95_max_ms = if ($frames.Count) { [Math]::Round(($frames | Measure-Object -Maximum).Maximum, 2) } else { $null }
        condition = $condition
        provenance = 'ComfyNetworkSense ClientTelemetrySampler -> ZNet.GetServerPing() -> ZRpc.GetTimeSinceLastPing(); heartbeat age in seconds, emitted as server_ping_age_ms.'
    }
}

$result = [ordered]@{
    generated_utc = [DateTime]::UtcNow.ToString('o')
    sample_limit = $SampleCount
    omen = Summarize 'omen' (Get-LocalSamples)
    i5 = Summarize 'i5' (Get-RemoteSamples)
}
$result | ConvertTo-Json -Depth 8
