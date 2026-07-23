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

.EXAMPLE
.\tools\i5\Start-TwoClientCapture.ps1 -DurationSeconds 30 -IntervalSeconds 1 -Label sprint-stutter
#>
[CmdletBinding()]
param(
    [ValidateRange(5, 300)]
    [int]$DurationSeconds = 30,

    [ValidateRange(1, 60)]
    [int]$IntervalSeconds = 1,

    [string]$Label = 'two-client'
)

$ErrorActionPreference = 'Stop'
$SshArgs = @('-o', 'BatchMode=yes', '-o', 'ConnectTimeout=8', 'i5')
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$safeLabel = ($Label -replace '[^A-Za-z0-9._-]', '-').Trim('-')
if ([string]::IsNullOrWhiteSpace($safeLabel)) { $safeLabel = 'two-client' }

function New-RemoteCaptureScript {
    param([string]$Machine)

    @"
`$ErrorActionPreference = 'Stop'
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

$result = [ordered]@{
    schema_version = 1
    started_label = "$stamp-$safeLabel"
    duration_seconds = $DurationSeconds
    interval_seconds = $IntervalSeconds
    omen = if ($localError) { @{ ok = $false; error = $localError } } else { $local }
    i5 = if ($remoteError) { @{ ok = $false; error = $remoteError } } else { $remote }
}

$result | ConvertTo-Json -Depth 12
