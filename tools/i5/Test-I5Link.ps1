<#
.SYNOPSIS
Preflight for the i5 deploy lane: tailnet presence, ssh reachability, key auth,
remote layout. Run this (or rely on Deploy-ToI5's inline preflight) before any
deploy.

.DESCRIPTION
Exit 0 = lane up. Exit 1 = lane down. The i5 is a roaming laptop marked
expect="optional" in the fleet inventory -- OFFLINE IS A NORMAL STATE, not an
error to retry in a loop. Report and stop.

.EXAMPLE
.\Test-I5Link.ps1
#>
[CmdletBinding()]
param()

$SshAlias = 'i5'
$Fqdn     = 'i5-laptop.tail8e749c.ts.net'
$script:AnyFail = $false

function Write-Step {
    param([string]$Name, [bool]$Ok, [string]$Detail)
    $tag = 'FAIL'
    if ($Ok) { $tag = 'PASS' }
    $suffix = ''
    if ($Detail) { $suffix = " - $Detail" }
    Write-Host ("[{0}] {1}{2}" -f $tag, $Name, $suffix)
    if (-not $Ok) { $script:AnyFail = $true }
}

# 1. Is the i5 on the tailnet right now?
$tsOk = $false
$tsDetail = ''
try {
    $tsLine = tailscale status 2>$null | Select-String 'i5-laptop' | Select-Object -First 1
    if ($tsLine) {
        $tsDetail = ($tsLine.Line -replace '\s+', ' ').Trim()
        $tsOk = ($tsDetail -notmatch 'offline')
    } else {
        $tsDetail = 'i5-laptop not present in tailscale status'
    }
} catch {
    $tsDetail = "tailscale CLI unavailable: $($_.Exception.Message)"
}
Write-Step 'tailnet peer online' $tsOk $tsDetail

# 2. Does ssh answer?
$tcpOk = Test-NetConnection $Fqdn -Port 22 -InformationLevel Quiet -WarningAction SilentlyContinue
Write-Step 'ssh port 22 reachable' $tcpOk $Fqdn

# 3. Does OMEN's key authenticate? (BatchMode: never falls back to a password prompt)
$who = ssh -o BatchMode=yes -o ConnectTimeout=8 $SshAlias "whoami" 2>$null
$authOk = ($LASTEXITCODE -eq 0)
$authDetail = 'no key auth - see tools/i5/README.md re-trust runbook'
if ($authOk) { $authDetail = "remote user: $who" }
Write-Step "ssh key auth via alias '$SshAlias'" $authOk $authDetail

# 4. Remote layout facts -- informational only, never fails the lane verdict
#    (staging is auto-created on first deploy; plugins dir only matters for
#    -ValheimPlugins deploys).
if ($authOk) {
    $remote = @'
$staging = Test-Path -LiteralPath 'C:\deploy\baseline'
$plugins = Test-Path -LiteralPath 'C:\Program Files (x86)\Steam\steamapps\common\Valheim\BepInEx\plugins'
$free = [math]::Round((Get-PSDrive C).Free / 1GB, 1)
Write-Output ("{0}|{1}|{2}" -f $staging, $plugins, $free)
'@
    $b64 = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($remote))
    $facts = ssh -o BatchMode=yes -o ConnectTimeout=8 $SshAlias "powershell.exe -NoProfile -EncodedCommand $b64" 2>$null
    if ($LASTEXITCODE -eq 0 -and $facts) {
        $parts = ($facts | Select-Object -Last 1).Split('|')
        Write-Host ("[INFO] staging root C:\deploy\baseline present: {0} (auto-created on first deploy)" -f $parts[0])
        Write-Host ("[INFO] Valheim BepInEx plugins dir present: {0}" -f $parts[1])
        Write-Host ("[INFO] free disk on C:: {0} GB" -f $parts[2])
    } else {
        Write-Step 'remote layout probe' $false 'remote powershell probe failed'
    }

    $dockerProbe = @'
$pipe = Test-Path -LiteralPath '\\.\pipe\dockerDesktopLinuxEngine'
$cli = Get-Command docker -ErrorAction SilentlyContinue
$server = $null
$versionError = $null
if ($cli) {
    $job = Start-Job -ScriptBlock { docker version --format '{{.Server.Version}}' 2>&1 }
    if (Wait-Job $job -Timeout 12) {
        $output = Receive-Job $job -ErrorAction SilentlyContinue
        if ($output) {
            $server = ($output | Select-Object -Last 1).ToString().Trim()
            if ($server -match 'error|failed|Cannot connect|pipe') {
                $versionError = (($output | Out-String) -replace '\s+', ' ').Trim()
                $server = $null
            }
        }
    } else {
        Stop-Job $job -ErrorAction SilentlyContinue
        $versionError = 'docker version timed out after 12 seconds'
    }
    Remove-Job $job -Force -ErrorAction SilentlyContinue
}
[pscustomobject]@{
    docker_cli = [bool]$cli
    linux_engine_pipe = $pipe
    server_version = $server
    version_error = $versionError
} | ConvertTo-Json -Compress
'@
    $b64 = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($dockerProbe))
    $dockerFacts = ssh -o BatchMode=yes -o ConnectTimeout=8 $SshAlias "powershell.exe -NoProfile -EncodedCommand $b64" 2>$null
    if ($LASTEXITCODE -eq 0 -and $dockerFacts) {
        $docker = $dockerFacts | Select-Object -Last 1 | ConvertFrom-Json
        if ($docker.docker_cli -and $docker.linux_engine_pipe -and $docker.server_version) {
            Write-Host ("[INFO] Docker Desktop Linux engine ready: {0}" -f $docker.server_version)
        } else {
            $reason = 'Docker Desktop Linux engine unavailable'
            if (-not $docker.docker_cli) { $reason = 'docker CLI not found' }
            elseif ($docker.version_error) { $reason = $docker.version_error }
            Write-Host ("[WARN] Companion Docker runtime not ready: {0}" -f $reason)
            Write-Host "[WARN] Deploy lane can still copy files; Start-I5Companion.ps1 will fail until Docker Desktop is ready."
        }
    } else {
        Write-Host '[WARN] Companion Docker runtime probe failed; deploy lane verdict is still based on tailnet/ssh.'
    }
}

if ($script:AnyFail) {
    Write-Host 'i5 lane: DOWN (offline is normal for this roaming laptop - report and stop, do not loop)'
    exit 1
}
Write-Host 'i5 lane: UP'
exit 0
