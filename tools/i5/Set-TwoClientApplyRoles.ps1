<#
.SYNOPSIS
Set OMEN/i5 Lumberjacks motion apply roles through the local Companion command lane.

.DESCRIPTION
Posts the allow-listed `set_apply` motion-test action to OMEN's local Companion
and the i5 Companion over the existing tailnet SSH lane. This does not run a
general console command and does not edit configuration; the running mod consumes
the command on Unity's main thread and records a receipt.
#>
[CmdletBinding()]
param(
    [ValidateSet('omen','i5')]
    [string]$ApplyClient = 'omen',

    [string]$Id = 'two-client-apply-roles',

    [string]$OutputJson = ''
)

$ErrorActionPreference = 'Stop'
$localUrl = 'http://127.0.0.1:8080/api/v0/companion/motion-test'

function New-Payload([bool]$Enabled, [string]$Client) {
    @{
        action = 'set_apply'
        motion_apply_enabled = $Enabled
        id = "$Id-$Client"
    } | ConvertTo-Json -Compress
}

function Invoke-LocalCompanion([string]$Body) {
    Invoke-RestMethod -Uri $localUrl -Method Post -ContentType 'application/json' -Body $Body
}

function Invoke-I5Companion([string]$Body) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($Body)
    $encoded = [Convert]::ToBase64String($bytes)
    $remote = @"
`$body = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$encoded'))
Invoke-RestMethod -Uri 'http://127.0.0.1:8080/api/v0/companion/motion-test' -Method Post -ContentType 'application/json' -Body `$body | ConvertTo-Json -Compress
"@
    $remoteEncoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($remote))
    $result = & ssh -o BatchMode=yes -o ConnectTimeout=8 i5 "powershell.exe -NoProfile -EncodedCommand $remoteEncoded"
    if ($LASTEXITCODE -ne 0) { throw 'i5 Companion apply-role command failed' }
    return ($result -join "`n" | ConvertFrom-Json)
}

$omenEnabled = $ApplyClient -eq 'omen'
$i5Enabled = $ApplyClient -eq 'i5'
$omenPayload = New-Payload $omenEnabled 'omen'
$i5Payload = New-Payload $i5Enabled 'i5'

$omen = Invoke-LocalCompanion $omenPayload
$i5 = Invoke-I5Companion $i5Payload

$result = [ordered]@{
    schema_version = 1
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    apply_client = $ApplyClient
    observe_client = if ($ApplyClient -eq 'omen') { 'i5' } else { 'omen' }
    omen_apply_enabled = $omenEnabled
    i5_apply_enabled = $i5Enabled
    omen = $omen
    i5 = $i5
}

$json = $result | ConvertTo-Json -Depth 8
if ($OutputJson) {
    $outPath = if ([IO.Path]::IsPathRooted($OutputJson)) {
        [IO.Path]::GetFullPath($OutputJson)
    } else {
        [IO.Path]::GetFullPath((Join-Path (Get-Location) $OutputJson))
    }
    $dir = Split-Path -Parent $outPath
    if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    [IO.File]::WriteAllText($outPath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}
$json
