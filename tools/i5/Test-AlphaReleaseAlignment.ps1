<#
.SYNOPSIS
Read-only preflight for the two-client alpha lane.
#>
[CmdletBinding()]
param(
    [string]$GatewayUrl = 'https://comfy-p7.duckdns.org',
    [string]$OmenCompanionUrl = 'http://127.0.0.1:8080'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$SshArgs = @('-o', 'BatchMode=yes', '-o', 'ConnectTimeout=8', 'i5')

function Get-Json([string]$Url) {
    Invoke-RestMethod -Uri $Url -Method Get -Headers @{ 'Cache-Control' = 'no-cache' }
}

function Get-RemoteJson([string]$Path) {
    $remoteScript = @"
`$ErrorActionPreference = 'Stop'
Invoke-RestMethod -Uri 'http://127.0.0.1:8080$Path' | ConvertTo-Json -Depth 20 -Compress
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($remoteScript))
    $raw = & ssh @SshArgs powershell.exe -NoProfile -EncodedCommand $encoded
    if ($LASTEXITCODE -ne 0) { throw "i5 Companion query failed: $Path" }
    ($raw -join [Environment]::NewLine) | ConvertFrom-Json
}

$gateway = Get-Json (($GatewayUrl.TrimEnd('/')) + '/api/v0/valheim/modpack/manifest')
$gatewayDeployment = Get-Json (($GatewayUrl.TrimEnd('/')) + '/api/v0/telemetry/deployment')
$omenStatus = Get-Json (($OmenCompanionUrl.TrimEnd('/')) + '/api/v0/companion/status')
$omenRelease = Get-Json (($OmenCompanionUrl.TrimEnd('/')) + '/api/v0/companion/update/check')
$i5Status = Get-RemoteJson '/api/v0/companion/status'
$i5Release = Get-RemoteJson '/api/v0/companion/update/check'

$rows = @(
    [pscustomobject]@{ lane = 'gateway'; release = $gateway.mod_release; package_sha256 = $gateway.package.sha256; ready = $true },
    [pscustomobject]@{ lane = 'omen'; release = $omenStatus.installed.mod_release; package_sha256 = $omenStatus.installed.package_sha256; ready = [bool]$omenStatus.profile.linked -and [bool]$omenStatus.valheim.config_found },
    [pscustomobject]@{ lane = 'i5'; release = $i5Status.installed.mod_release; package_sha256 = $i5Status.installed.package_sha256; ready = [bool]$i5Status.profile.linked -and [bool]$i5Status.valheim.config_found }
)
$expectedRelease = [string]$gateway.mod_release
$expectedHash = [string]$gateway.package.sha256
$aligned = $rows | Where-Object { $_.release -ne $expectedRelease -or $_.package_sha256 -ne $expectedHash -or -not $_.ready }

Write-Host 'alpha release alignment (read-only)'
$rows | Format-Table -AutoSize
Write-Host ("gateway deployment: {0}" -f $gatewayDeployment.release_id)
Write-Host ("companion self-update available: {0}" -f [bool]($omenRelease.update_available -or $i5Release.update_available))
if ($aligned) {
    Write-Host ("verdict: BLOCKED ({0} lane(s) differ or are not ready)" -f @($aligned).Count)
    exit 1
}
Write-Host 'verdict: READY (Gateway, OMEN, and i5 release/package identities agree)'
exit 0
