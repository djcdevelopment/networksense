#Requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$entrypoints = @(
    'tools/am4/Deploy-NetworkSense.ps1',
    'tools/i5/Deploy-ToI5.ps1',
    'tools/i5/Install-I5LatestModpack.ps1',
    'tools/i5/Repair-I5DockerDesktop.ps1',
    'tools/i5/Set-TwoClientApplyRoles.ps1',
    'tools/i5/Start-TwoClientCapture.ps1',
    'tools/i5/Start-TwoClientFeelWindow.ps1',
    'tools/i5/Start-TwoClientMotionTest.ps1',
    'tools/i5/Test-AlphaReleaseAlignment.ps1',
    'tools/i5/Test-DeployToI5Fixtures.ps1',
    'tools/modpack/New-AlphaModpack.ps1',
    'network/tools/New-ModReleaseCut.ps1',
    'network/tools/Test-ModReleaseBundle.ps1',
    'network/tools/Test-ModReleaseTamperFixtures.ps1'
)

$failures = New-Object Collections.Generic.List[string]
foreach ($entrypoint in $entrypoints) {
    $fullPath = Join-Path $repoRoot $entrypoint
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        $failures.Add("missing guarded entrypoint: $entrypoint")
        continue
    }
    $text = [IO.File]::ReadAllText($fullPath)
    if ($text -notmatch '(?m)^Assert-RepoIdentity\s*\|\s*Out-Null\s*$') {
        $failures.Add("identity call missing: $entrypoint")
    }
}

$extractor = Join-Path $repoRoot 'tools/synthetic-baseline-extractor/Program.cs'
if ([IO.File]::ReadAllText($extractor) -notmatch 'EnsureRepoIdentity\(\);') {
    $failures.Add('identity call missing: tools/synthetic-baseline-extractor/Program.cs')
}

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) { Write-Error $failure }
    throw 'G2 state-changing entrypoint coverage failed.'
}

Write-Output ("G2 identity coverage verified for {0} host entrypoints and the extractor." -f $entrypoints.Count)
