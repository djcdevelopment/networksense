<##
.SYNOPSIS
Start a bounded named movement pattern on OMEN and i5 without keyboard automation.

.DESCRIPTION
Uses each machine's local Companion HTTP surface. The Companions write a one-shot command
file for the installed mod; the mod executes it on the Unity main thread and appends a receipt.
This script does not invoke arbitrary Valheim console commands or inject keystrokes.
##>
[CmdletBinding()]
param(
    [ValidateSet('straight_north','straight_east','stutter_north','circle')]
    [string]$Pattern = 'straight_north',
    [ValidateRange(1,60)]
    [int]$DurationSeconds = 10,
    [string]$Id = 'two-client-motion',
    [string]$OutputJson = ''
)

$ErrorActionPreference = 'Stop'
$localUrl = 'http://127.0.0.1:8080/api/v0/companion/motion-test'
$payload = @{ action = 'start'; pattern = $Pattern; duration_seconds = $DurationSeconds; id = $Id } |
    ConvertTo-Json -Compress

function Invoke-I5Companion([string]$Body) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($Body)
    $encoded = [Convert]::ToBase64String($bytes)
    $remote = "`$body = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$encoded')); Invoke-RestMethod -Uri 'http://127.0.0.1:8080/api/v0/companion/motion-test' -Method Post -ContentType 'application/json' -Body `$body | ConvertTo-Json -Compress"
    $result = & ssh -o BatchMode=yes -o ConnectTimeout=8 i5 "powershell.exe -NoProfile -Command `"$remote`""
    if ($LASTEXITCODE -ne 0) { throw 'i5 Companion motion command failed' }
    return ($result -join "`n" | ConvertFrom-Json)
}

$local = Invoke-RestMethod -Uri $localUrl -Method Post -ContentType 'application/json' -Body $payload
$remote = Invoke-I5Companion $payload
$result = [ordered]@{
    schema_version = 1
    pattern = $Pattern
    duration_seconds = $DurationSeconds
    id = $Id
    omen = $local
    i5 = $remote
}
$json = $result | ConvertTo-Json -Depth 8
if ($OutputJson) { $json | Set-Content -LiteralPath $OutputJson -Encoding UTF8 }
$json
