#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ArtifactDirectory,
    [string] $ExpectedTag = 'mod-v0.5.80-split-proof',
    [ValidateSet('public', 'interim')]
    [string] $ExpectedDependencyProfile = 'interim',
    [string] $ValheimDirectory = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $repoRoot 'tools\Assert-RepoIdentity.ps1') -DefineOnly
Assert-RepoIdentity | Out-Null

$verifier = Join-Path $PSScriptRoot 'Test-ModReleaseBundle.ps1'
& $verifier -ArtifactDirectory $ArtifactDirectory -ExpectedTag $ExpectedTag `
    -ExpectedDependencyProfile $ExpectedDependencyProfile -ValheimDirectory $ValheimDirectory | Out-Host

$fixturePath = Join-Path $repoRoot 'tests\fixtures\release\tamper-dll-byte.json.disabled'
$fixture = [IO.File]::ReadAllText($fixturePath) | ConvertFrom-Json
if ([string]$fixture.schema -ne 'comfy-mod-release-tamper-fixture/v1' -or
    [string]$fixture.asset -ne 'ComfyNetworkSense.dll' -or
    [string]$fixture.mutation -ne 'append-byte') {
    throw 'disabled release tamper fixture has an unexpected shape'
}

$sandboxParent = Join-Path $repoRoot 'artifacts\release-tamper-tests'
$sandbox = Join-Path $sandboxParent ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $sandbox | Out-Null
try {
    foreach ($name in @('ComfyNetworkSense.dll', 'release-manifest.json', 'boundary-receipt.json', 'SHA256SUMS')) {
        Copy-Item -LiteralPath (Join-Path $ArtifactDirectory $name) -Destination (Join-Path $sandbox $name)
    }
    $stream = [IO.File]::Open((Join-Path $sandbox 'ComfyNetworkSense.dll'), [IO.FileMode]::Append, [IO.FileAccess]::Write)
    try { $stream.WriteByte([byte]$fixture.byte) } finally { $stream.Dispose() }

    $rejected = $false
    try {
        & $verifier -ArtifactDirectory $sandbox -ExpectedTag $ExpectedTag `
            -ExpectedDependencyProfile $ExpectedDependencyProfile -ValheimDirectory $ValheimDirectory | Out-Null
    } catch {
        if ($_.Exception.Message -notmatch 'ComfyNetworkSense\.dll SHA-256 mismatch') { throw }
        $rejected = $true
        Write-Output 'disabled negative fixture: tampered DLL byte was rejected by the release boundary verifier.'
    }
    if (-not $rejected) { throw 'tampered DLL negative fixture unexpectedly passed' }
} finally {
    $sandboxFull = [IO.Path]::GetFullPath($sandbox)
    $parentFull = [IO.Path]::GetFullPath($sandboxParent).TrimEnd('\') + '\'
    if ($sandboxFull.StartsWith($parentFull, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $sandboxFull)) {
        Remove-Item -LiteralPath $sandboxFull -Recurse -Force
    }
}

Write-Output 'release bundle positive and disabled negative tamper fixtures passed.'
