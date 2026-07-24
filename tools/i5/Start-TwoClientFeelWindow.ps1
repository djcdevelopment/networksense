<#
.SYNOPSIS
Run a bounded, low-touch OMEN/i5 physical movement observation window.

.DESCRIPTION
Coordinates the existing read-only readiness check, concurrent two-client capture,
allow-listed apply/observe role command, and named motion pattern command. It does
not launch Valheim, inject keyboard input, edit configs, or copy files. The clients
must already be joined and fully loaded before this command is started.

The default sequence runs one OMEN-apply window. With -RoleReversal it immediately
runs a second window with i5 applying and OMEN observing, so the visual comparison
does not require another join or relog.

The only intended human work is to watch both screens during the bounded window and
record whether the result felt smooth, rough, or mixed and whether the visible
effect followed the apply role.
#>
[CmdletBinding()]
param(
    [ValidateSet('straight_north','straight_east','stutter_north','circle')]
    [string]$Pattern = 'straight_north',

    [ValidateRange(1, 60)]
    [int]$MotionDurationSeconds = 10,

    [ValidateRange(0, 300)]
    [int]$CaptureDurationSeconds = 0,

    [ValidateRange(1, 60)]
    [int]$IntervalSeconds = 1,

    [ValidateSet('omen','i5','none')]
    [string]$ApplyClient = 'omen',

    [switch]$RoleReversal,

    [string]$Label = 'feel-window',

    [string]$GatewayUrl = 'https://comfy-p7.duckdns.org',

    [string]$OutputDirectory = '',

    [switch]$SkipReadiness,

    [switch]$CollectBundles,

    [switch]$DryRun,

    [string]$OutputJson
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot '..\..\captures\physical-feel'
}

if ($CaptureDurationSeconds -eq 0) {
    $CaptureDurationSeconds = [Math]::Min(300, [Math]::Max(15, $MotionDurationSeconds + 15))
}
if ($RoleReversal -and $ApplyClient -eq 'none') {
    throw '-RoleReversal requires -ApplyClient omen or -ApplyClient i5.'
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$readinessScript = Join-Path $PSScriptRoot 'Test-Wave0Readiness.ps1'
$captureScript = Join-Path $PSScriptRoot 'Start-TwoClientCapture.ps1'
$motionScript = Join-Path $PSScriptRoot 'Start-TwoClientMotionTest.ps1'
$rolesScript = Join-Path $PSScriptRoot 'Set-TwoClientApplyRoles.ps1'

function Get-SafeName([string]$Value) {
    $safe = ($Value -replace '[^A-Za-z0-9._-]', '-').Trim('-')
    if ([string]::IsNullOrWhiteSpace($safe)) { return 'feel-window' }
    return $safe
}

function ConvertTo-ProcessArgument([string]$Value) {
    if ($Value -notmatch '[\s"]') { return $Value }
    return '"' + ($Value -replace '"', '\\"') + '"'
}

function Invoke-ChildScript {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string]$OutputDirectory,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )

    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $stdoutPath = Join-Path $OutputDirectory "$Name.stdout.log"
    $stderrPath = Join-Path $OutputDirectory "$Name.stderr.log"
    $argumentList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $ScriptPath) + $Arguments
    $processArguments = ($argumentList | ForEach-Object { ConvertTo-ProcessArgument ([string]$_) }) -join ' '
    $process = Start-Process -FilePath 'powershell.exe' -ArgumentList $processArguments -WorkingDirectory $WorkingDirectory `
        -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -WindowStyle Hidden -PassThru

    $timedOut = $false
    try {
        Wait-Process -Id $process.Id -Timeout $TimeoutSeconds -ErrorAction SilentlyContinue | Out-Null
        try { $stillRunning = Get-Process -Id $process.Id -ErrorAction Stop } catch { $stillRunning = $null }
        if ($stillRunning) {
            $timedOut = $true
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            Wait-Process -Id $process.Id -Timeout 5 -ErrorAction SilentlyContinue | Out-Null
        }
    } finally {
        try { $process.Refresh() } catch { }
    }

    $exitCode = if ($timedOut) { $null } else { $process.ExitCode }
    [ordered]@{
        ok = (-not $timedOut -and $exitCode -eq 0)
        timed_out = $timedOut
        exit_code = $exitCode
        stdout_path = $stdoutPath
        stderr_path = $stderrPath
    }
}

function Start-ChildScriptProcess {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string]$OutputDirectory,
        [Parameter(Mandatory = $true)][string]$Name
    )

    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $stdoutPath = Join-Path $OutputDirectory "$Name.stdout.log"
    $stderrPath = Join-Path $OutputDirectory "$Name.stderr.log"
    $argumentList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $ScriptPath) + $Arguments
    $processArguments = ($argumentList | ForEach-Object { ConvertTo-ProcessArgument ([string]$_) }) -join ' '
    $process = Start-Process -FilePath 'powershell.exe' -ArgumentList $processArguments -WorkingDirectory $WorkingDirectory `
        -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -WindowStyle Hidden -PassThru
    [pscustomobject]@{
        process = $process
        stdout_path = $stdoutPath
        stderr_path = $stderrPath
    }
}

function Wait-ChildScriptProcess {
    param(
        [Parameter(Mandatory = $true)]$Started,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )

    $timedOut = $false
    Wait-Process -Id $Started.process.Id -Timeout $TimeoutSeconds -ErrorAction SilentlyContinue | Out-Null
    try { $stillRunning = Get-Process -Id $Started.process.Id -ErrorAction Stop } catch { $stillRunning = $null }
    if ($stillRunning) {
        $timedOut = $true
        Stop-Process -Id $Started.process.Id -Force -ErrorAction SilentlyContinue
        Wait-Process -Id $Started.process.Id -Timeout 5 -ErrorAction SilentlyContinue | Out-Null
    }
    try { $Started.process.Refresh() } catch { }
    $exitCode = if ($timedOut) { $null } else { $Started.process.ExitCode }
    [ordered]@{
        ok = (-not $timedOut -and $exitCode -eq 0)
        timed_out = $timedOut
        exit_code = $exitCode
        stdout_path = $Started.stdout_path
        stderr_path = $Started.stderr_path
    }
}

function Read-JsonFile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try {
        return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json)
    } catch {
        return $null
    }
}

function Invoke-StopMotion {
    param([string]$Id)

    $body = @{ action = 'stop'; id = $Id } | ConvertTo-Json -Compress
    $outcome = [ordered]@{ omen = $null; i5 = $null }
    try {
        $outcome.omen = Invoke-RestMethod -Uri 'http://127.0.0.1:8080/api/v0/companion/motion-test' `
            -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 15
    } catch {
        $outcome.omen = @{ ok = $false; error = $_.Exception.Message }
    }

    try {
        $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($body))
        $remote = @"
`$body = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$encoded'))
Invoke-RestMethod -Uri 'http://127.0.0.1:8080/api/v0/companion/motion-test' -Method Post -ContentType 'application/json' -Body `$body -TimeoutSec 15 | ConvertTo-Json -Compress
"@
        $remoteEncoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($remote))
        $raw = & ssh -o BatchMode=yes -o ConnectTimeout=8 i5 "powershell.exe -NoProfile -EncodedCommand $remoteEncoded" 2>$null
        if ($LASTEXITCODE -ne 0) { throw "ssh exit $LASTEXITCODE" }
        $outcome.i5 = (($raw | Where-Object { $_ -and $_ -notmatch '^#< CLIXML' }) -join "`n" | ConvertFrom-Json)
    } catch {
        $outcome.i5 = @{ ok = $false; error = $_.Exception.Message }
    }
    return $outcome
}

function Get-ApplySequence {
    $sequence = @()
    if ($ApplyClient -eq 'none') {
        $sequence += 'none'
    } else {
        $sequence += $ApplyClient
        if ($RoleReversal) {
            $sequence += if ($ApplyClient -eq 'omen') { 'i5' } else { 'omen' }
        }
    }
    return $sequence
}

$safeLabel = Get-SafeName $Label
$runStamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')
$runRoot = [IO.Path]::GetFullPath((Join-Path $OutputDirectory "$runStamp-$safeLabel"))

$sequence = @(Get-ApplySequence)
Write-Host "physical feel window: pattern=$Pattern motion=${MotionDurationSeconds}s capture=${CaptureDurationSeconds}s interval=${IntervalSeconds}s"
Write-Host ("apply sequence: {0}" -f ($sequence -join ' -> '))
Write-Host 'human role: watch both joined clients; do not drive or relog during the window.'

if ($DryRun) {
    Write-Host "dry run - no readiness query, Companion command, SSH call, or movement was sent"
    Write-Host "receipt directory: $runRoot"
    exit 0
}

New-Item -ItemType Directory -Force -Path $runRoot | Out-Null

$readinessResult = $null
$readinessPath = Join-Path $runRoot 'readiness.json'
if (-not $SkipReadiness) {
    Write-Host 'readiness: querying P7, OMEN, and i5 before movement'
    $readinessRun = Invoke-ChildScript -ScriptPath $readinessScript -WorkingDirectory $repoRoot `
        -OutputDirectory $runRoot -Name 'readiness' -TimeoutSeconds 45 `
        -Arguments @('-GatewayUrl', $GatewayUrl, '-SummaryOnly', '-OutputJson', $readinessPath)
    $readinessResult = Read-JsonFile $readinessPath
    if (-not $readinessRun.ok -or -not $readinessResult) {
        throw "readiness did not produce a usable receipt; see $($readinessRun.stderr_path)"
    }
    if ([string]$readinessResult.verdict -notin @('ready_for_real_clients','waiting_for_optional_i5_or_real_clients')) {
        $readinessResult | ConvertTo-Json -Depth 20
        throw "readiness gate stopped the feel window with verdict=$($readinessResult.verdict)"
    }
    Write-Host ("readiness: {0}" -f $readinessResult.verdict)
} else {
    Write-Host 'readiness: skipped by operator'
}

$windows = @()
foreach ($windowApplyClient in $sequence) {
    $windowIndex = $windows.Count + 1
    $windowName = "window-$windowIndex-$windowApplyClient"
    $windowRoot = Join-Path $runRoot $windowName
    New-Item -ItemType Directory -Force -Path $windowRoot | Out-Null
    $capturePath = Join-Path $windowRoot 'capture.json'
    $rolesPath = Join-Path $windowRoot 'roles.json'
    $motionPath = Join-Path $windowRoot 'motion.json'
    $motionId = "$safeLabel-$windowIndex-$windowApplyClient"

    Write-Host "[$windowName] capture first, then role=$windowApplyClient, then pattern=$Pattern"
    $captureArguments = @(
        '-DurationSeconds', $CaptureDurationSeconds,
        '-IntervalSeconds', $IntervalSeconds,
        '-Label', "$safeLabel-$windowIndex-$windowApplyClient",
        '-SummaryOnly',
        '-OutputJson', $capturePath
    )
    if ($CollectBundles) {
        $bundlePath = Join-Path $windowRoot 'bundles'
        $captureArguments += @('-BundleDirectory', $bundlePath)
    }
    $captureProcess = $null
    $captureRun = $null
    $motionStarted = $false
    $stopResult = $null
    $windowRecord = $null
    try {
        $captureProcess = Start-ChildScriptProcess -ScriptPath $captureScript -WorkingDirectory $repoRoot `
            -OutputDirectory $windowRoot -Name 'capture' -Arguments $captureArguments
        Start-Sleep -Seconds 1

        if ($windowApplyClient -ne 'none') {
            $rolesRun = Invoke-ChildScript -ScriptPath $rolesScript -WorkingDirectory $repoRoot `
                -OutputDirectory $windowRoot -Name 'roles' -TimeoutSeconds 45 `
                -Arguments @('-ApplyClient', $windowApplyClient, '-Id', $motionId, '-OutputJson', $rolesPath)
        } else {
            $rolesRun = [ordered]@{ ok = $true; skipped = $true }
        }

        if (-not $rolesRun.ok) {
            throw "apply-role command failed; see $($rolesRun.stderr_path)"
        }

        $motionStarted = $true
        $motionRun = Invoke-ChildScript -ScriptPath $motionScript -WorkingDirectory $repoRoot `
            -OutputDirectory $windowRoot -Name 'motion' -TimeoutSeconds ([Math]::Max(45, $MotionDurationSeconds + 30)) `
            -Arguments @('-Pattern', $Pattern, '-DurationSeconds', $MotionDurationSeconds, '-Id', $motionId, '-OutputJson', $motionPath)
        $captureRun = Wait-ChildScriptProcess -Started $captureProcess -TimeoutSeconds ([Math]::Max(75, $CaptureDurationSeconds + 75))
        $capture = Read-JsonFile $capturePath
        $roles = Read-JsonFile $rolesPath
        $motion = Read-JsonFile $motionPath
        $windowRecord = [ordered]@{
            schema_version = 1
            window = $windowName
            apply_client = $windowApplyClient
            observe_client = if ($windowApplyClient -eq 'omen') { 'i5' } elseif ($windowApplyClient -eq 'i5') { 'omen' } else { 'both' }
            motion_id = $motionId
            capture = $capture
            roles = $roles
            motion = $motion
            process = [ordered]@{
                capture = $captureRun
                motion = $motionRun
                roles = $rolesRun
            }
            evidence_paths = [ordered]@{
                capture_json = $capturePath
                roles_json = $rolesPath
                motion_json = $motionPath
                capture_stdout = $captureRun.stdout_path
                capture_stderr = $captureRun.stderr_path
            }
        }
        $windows += $windowRecord
    } finally {
        if ($motionStarted) { $stopResult = Invoke-StopMotion $motionId }
        if ($captureProcess) {
            try { $captureStillRunning = Get-Process -Id $captureProcess.process.Id -ErrorAction Stop } catch { $captureStillRunning = $null }
            if ($captureStillRunning) {
                Stop-Process -Id $captureProcess.process.Id -Force -ErrorAction SilentlyContinue
            }
        }
        if ($windowRecord) {
            $windowRecord.cleanup_stop = $stopResult
        }
    }
}

$final = [ordered]@{
    schema_version = 1
    event_type = 'physical_feel_window'
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    pattern = $Pattern
    motion_duration_seconds = $MotionDurationSeconds
    capture_duration_seconds = $CaptureDurationSeconds
    interval_seconds = $IntervalSeconds
    apply_sequence = $sequence
    readiness_path = $readinessPath
    run_directory = $runRoot
    windows = $windows
    human_observation = [ordered]@{
        instruction = 'Watch both screens; do not drive or relog during each named window.'
        smoothness = @('smooth','rough','mixed')
        visible_effect_followed_apply_role = @('yes','no','unclear')
        notes = 'Record the first correction, glide, teleport, stop response, or other felt boundary while the raw trace is retained.'
    }
}
$finalPath = if ($OutputJson) { [IO.Path]::GetFullPath($OutputJson) } else { Join-Path $runRoot 'feel-window.json' }
$finalParent = Split-Path -Parent $finalPath
if ($finalParent) { New-Item -ItemType Directory -Force -Path $finalParent | Out-Null }
$final | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $finalPath -Encoding UTF8
Write-Host "receipt: $finalPath"
exit 0
