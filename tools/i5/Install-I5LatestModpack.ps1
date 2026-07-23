<#
.SYNOPSIS
Install the current P7 client-pull modpack through the i5 Companion API.

.DESCRIPTION
Runs over the verified i5 SSH lane and asks the Companion container on the i5
to install the current /api/v0/valheim/modpack/manifest package. This preserves
the installed ComfyNetworkSense config and credentials; it does not use the
Steam browser flow and never prompts for a password.

Valheim must be closed on the i5. If it is running, Companion returns
valheim_is_running and this script fails without killing the game.
#>
[CmdletBinding()]
param(
    [string]$SshAlias = 'i5'
)

$ErrorActionPreference = 'Stop'
$sshArgs = @('-o', 'BatchMode=yes', '-o', 'ConnectTimeout=8', $SshAlias)

$remote = @'
$ErrorActionPreference = 'Stop'
$body = @{ game_closed_confirmed = $true } | ConvertTo-Json -Compress
$result = Invoke-RestMethod -Uri 'http://127.0.0.1:8080/api/v0/companion/update/install' -Method Post -ContentType 'application/json' -Body $body
$result | ConvertTo-Json -Depth 20 -Compress
if (-not $result.ok) { exit 1 }
'@

$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($remote))
$raw = ssh @sshArgs "powershell.exe -NoProfile -EncodedCommand $encoded" 2>$null
if ($LASTEXITCODE -ne 0) {
    $text = (($raw | Where-Object { $_ -and $_ -notmatch '^#< CLIXML' }) -join [Environment]::NewLine)
    if ($text) { Write-Error $text }
    throw 'i5 Companion install failed'
}

$json = (($raw | Where-Object { $_ -and $_ -notmatch '^#< CLIXML' }) -join [Environment]::NewLine)
if (-not $json) { throw 'i5 Companion install returned no JSON' }
$json | ConvertFrom-Json | ConvertTo-Json -Depth 20
