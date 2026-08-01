<#
.SYNOPSIS
Install Sysinternals ProcDump on the i5 test client.

.DESCRIPTION
Runs ON the i5 (deploy it there first). The winget manifest for the Sysinternals
suite currently 404s, so this pulls the standalone ProcDump archive from
Microsoft's own download host and unpacks it under the deploy staging root.

ProcDump earns its place here for one specific reason: its CPU trigger is
discriminating. A stall that is CPU-bound will fire `-c`, and one that is
blocked in a syscall will not - so the trigger firing or not firing is itself
evidence, before any dump is even opened.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File C:\deploy\baseline\Install-I5ProcDump.ps1
#>
[CmdletBinding()]
param(
    [string] $Root = 'C:\deploy\baseline\procdump',
    [string] $Uri = 'https://download.sysinternals.com/files/Procdump.zip'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$exe = Join-Path $Root 'procdump64.exe'
if (Test-Path -LiteralPath $exe) {
    [ordered] @{
        schema_version = 1
        receipt_type   = 'procdump_install'
        result         = 'already_present'
        procdump       = $exe
        version        = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
    } | ConvertTo-Json -Depth 4
    return
}

New-Item -ItemType Directory -Force -Path $Root | Out-Null
$zip = Join-Path $Root 'Procdump.zip'

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Invoke-WebRequest -Uri $Uri -OutFile $zip -UseBasicParsing -TimeoutSec 120

Expand-Archive -LiteralPath $zip -DestinationPath $Root -Force
Remove-Item -LiteralPath $zip -Force

if (-not (Test-Path -LiteralPath $exe)) {
    throw "ProcDump unpacked but $exe is missing; contents: " +
        ((Get-ChildItem -LiteralPath $Root).Name -join ', ')
}

[ordered] @{
    schema_version = 1
    receipt_type   = 'procdump_install'
    result         = 'installed'
    source         = $Uri
    procdump       = $exe
    version        = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
    sha256         = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    contents       = @((Get-ChildItem -LiteralPath $Root -Filter '*.exe').Name)
} | ConvertTo-Json -Depth 4
