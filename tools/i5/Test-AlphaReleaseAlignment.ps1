<#
.SYNOPSIS
Read-only preflight for the two-client alpha lane.
#>
[CmdletBinding()]
param(
    [string]$GatewayUrl = 'https://comfy-p7.duckdns.org',
    [string]$OmenCompanionUrl = 'http://127.0.0.1:8080',
    [switch]$SummaryOnly,
    [string]$OutputJson = ''
)

. (Join-Path $PSScriptRoot '..\Assert-RepoIdentity.ps1') -DefineOnly
Assert-RepoIdentity | Out-Null

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$SshArgs = @('-o', 'BatchMode=yes', '-o', 'ConnectTimeout=8', 'i5')
$httpTimeoutSeconds = 15

function Get-Json([string]$Url) {
    Invoke-RestMethod -Uri $Url -Method Get -Headers @{ 'Cache-Control' = 'no-cache' } -TimeoutSec $httpTimeoutSeconds
}

function Get-RemoteJson([string]$Path) {
    $remoteScript = @"
`$ErrorActionPreference = 'Stop'
Invoke-RestMethod -Uri 'http://127.0.0.1:8080$Path' -TimeoutSec $httpTimeoutSeconds | ConvertTo-Json -Depth 20 -Compress
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

$result = [ordered]@{
    schema = 'networksense-alpha-release-alignment/v1'
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    verdict = if ($aligned) { 'blocked_release_alignment' } else { 'ready_for_real_clients' }
    gateway_deployment = [string]$gatewayDeployment.release_id
    companion_update_available = [bool]($omenRelease.update_available -or $i5Release.update_available)
    expected_release = $expectedRelease
    expected_package_sha256 = $expectedHash
    blocked_lanes = @($aligned | ForEach-Object { $_.lane })
    lanes = @($rows)
}

if (-not $SummaryOnly) {
    Write-Host 'alpha release alignment (read-only)'
    $rows | Format-Table -AutoSize
}
Write-Host ("verdict: {0}" -f $result.verdict)
$json = $result | ConvertTo-Json -Depth 8
if (-not [string]::IsNullOrWhiteSpace($OutputJson)) {
    $outputFullPath = [IO.Path]::GetFullPath($OutputJson)
    $outputParent = Split-Path -Parent $outputFullPath
    if ($outputParent) { New-Item -ItemType Directory -Force -Path $outputParent | Out-Null }
    [IO.File]::WriteAllText($outputFullPath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}
if (-not $SummaryOnly) { $json }
if ($aligned) { exit 1 }
exit 0
