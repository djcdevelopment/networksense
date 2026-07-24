<#
.SYNOPSIS
Fixture-smoke the i5 deploy script's directory exclude behavior.

.DESCRIPTION
Creates a tiny local directory with keep files and excluded bin/obj files, deploys
it to the i5 staging root, then verifies that keep files exist remotely and the
excluded files do not. This catches the class of bug where the manifest was
filtered but scp still copied the full source directory.
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory = 'captures/i5-deploy-fixtures',
    [string]$RemoteRoot = 'C:/deploy/baseline/deploy-fixtures'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$outRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
}
New-Item -ItemType Directory -Force -Path $outRoot | Out-Null

$sourceRoot = Join-Path $outRoot 'fixture-src'
$caseRoot = Join-Path $sourceRoot 'deploy-exclude-case'
Remove-Item -LiteralPath $sourceRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path `
    $caseRoot, `
    (Join-Path $caseRoot 'nested'), `
    (Join-Path $caseRoot 'bin'), `
    (Join-Path $caseRoot 'obj'), `
    (Join-Path $caseRoot 'nested\obj') | Out-Null

Set-Content -LiteralPath (Join-Path $caseRoot 'keep.txt') -Value 'keep-root' -Encoding ascii
Set-Content -LiteralPath (Join-Path $caseRoot 'nested\keep-nested.txt') -Value 'keep-nested' -Encoding ascii
Set-Content -LiteralPath (Join-Path $caseRoot 'bin\drop-bin.txt') -Value 'drop-bin' -Encoding ascii
Set-Content -LiteralPath (Join-Path $caseRoot 'obj\drop-obj.txt') -Value 'drop-obj' -Encoding ascii
Set-Content -LiteralPath (Join-Path $caseRoot 'nested\obj\drop-nested-obj.txt') -Value 'drop-nested-obj' -Encoding ascii

$runId = 'run-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
$remoteRunRoot = ($RemoteRoot.TrimEnd('/', '\') + '/' + $runId)

& (Join-Path $repoRoot 'tools\i5\Deploy-ToI5.ps1') `
    -Path $caseRoot `
    -Dest $remoteRunRoot `
    -ExcludeDirectoryName bin,obj | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Deploy-ToI5 fixture deploy failed' }

$expectedPresent = @(
    "$remoteRunRoot/deploy-exclude-case/keep.txt",
    "$remoteRunRoot/deploy-exclude-case/nested/keep-nested.txt"
)
$expectedAbsent = @(
    "$remoteRunRoot/deploy-exclude-case/bin/drop-bin.txt",
    "$remoteRunRoot/deploy-exclude-case/obj/drop-obj.txt",
    "$remoteRunRoot/deploy-exclude-case/nested/obj/drop-nested-obj.txt"
)

$remoteCheck = @'
$present = @(
__PRESENT__
)
$absent = @(
__ABSENT__
)
$result = [ordered]@{
    present = @()
    absent = @()
}
foreach ($p in $present) {
    $result.present += [ordered]@{ path = $p; exists = [bool](Test-Path -LiteralPath $p -PathType Leaf) }
}
foreach ($p in $absent) {
    $result.absent += [ordered]@{ path = $p; exists = [bool](Test-Path -LiteralPath $p -PathType Leaf) }
}
$result | ConvertTo-Json -Depth 6
'@
$quotedPresent = @($expectedPresent | ForEach-Object { "    '$($_.Replace("'", "''"))'" }) -join ",`r`n"
$quotedAbsent = @($expectedAbsent | ForEach-Object { "    '$($_.Replace("'", "''"))'" }) -join ",`r`n"
$remoteScript = $remoteCheck.Replace('__PRESENT__', $quotedPresent).Replace('__ABSENT__', $quotedAbsent)
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($remoteScript))
$remoteJson = ssh -o BatchMode=yes -o ConnectTimeout=8 i5 "powershell.exe -NoProfile -EncodedCommand $encoded"
if ($LASTEXITCODE -ne 0) { throw 'remote fixture verification failed' }
$remote = $remoteJson | ConvertFrom-Json

$missingExpected = @($remote.present | Where-Object { -not [bool]$_.exists })
$copiedExcluded = @($remote.absent | Where-Object { [bool]$_.exists })
$verdict = if ($missingExpected.Count -eq 0 -and $copiedExcluded.Count -eq 0) {
    'i5_deploy_exclude_fixture_passed'
} else {
    'i5_deploy_exclude_fixture_failed'
}

$summary = [ordered]@{
    schema_version = 1
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    verdict = $verdict
    local_fixture = $caseRoot
    remote_root = $remoteRunRoot
    expected_present = $remote.present
    expected_absent = $remote.absent
}
$summaryPath = Join-Path $outRoot 'summary.json'
[IO.File]::WriteAllText($summaryPath, (($summary | ConvertTo-Json -Depth 8) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))

Write-Host ("i5 deploy exclude fixtures: {0}" -f $summary.verdict)
Write-Host ("Summary JSON: {0}" -f $summaryPath)
if ($verdict -ne 'i5_deploy_exclude_fixture_passed') { exit 1 }
