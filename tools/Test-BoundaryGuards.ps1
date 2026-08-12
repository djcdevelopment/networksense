#Requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$identity = Join-Path $PSScriptRoot 'Assert-RepoIdentity.ps1'
$reachIn = Join-Path $PSScriptRoot 'Assert-NoReachIn.ps1'
$identityCoverage = Join-Path $PSScriptRoot 'Assert-StateChangingGuards.ps1'
$packageOnly = Join-Path $PSScriptRoot 'Assert-PackageOnly.ps1'
$badFixture = Join-Path $repoRoot 'tests\fixtures\boundary\bad-reach-in.ps1.disabled'
$badPackageFixture = Join-Path $repoRoot 'tests\fixtures\boundary\bad-package-reach-in.csproj.disabled'

& $identity | Out-Host
& $reachIn | Out-Host
& $identityCoverage | Out-Host
& $packageOnly | Out-Host

$identityFailed = $false
try {
    & $identity -ExpectedRepository 'djcdevelopment/not-networksense' | Out-Null
} catch {
    $identityFailed = $true
    Write-Output 'G2 negative fixture: wrong expected origin was rejected.'
}
if (-not $identityFailed) { throw 'G2 identity negative fixture unexpectedly passed.' }

$reachInFailed = $false
try {
    & $reachIn -Path $badFixture | Out-Null
} catch {
    $reachInFailed = $true
    Write-Output 'G1 negative fixture: forbidden source reach-in was rejected.'
}
if (-not $reachInFailed) { throw 'G1 no-reach-in negative fixture unexpectedly passed.' }

$packageFailed = $false
try {
    & $packageOnly -ProjectPath $badPackageFixture | Out-Null
} catch {
    $packageFailed = $true
    Write-Output 'G5 negative fixture: sibling project reference was rejected.'
}
if (-not $packageFailed) { throw 'G5 package-only negative fixture unexpectedly passed.' }

Write-Output 'boundary guard self-tests passed.'
