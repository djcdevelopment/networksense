#Requires -Version 5.1
[CmdletBinding()]
param(
    [string[]] $ProjectPath = @()
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ($ProjectPath.Count -eq 0) {
    $ProjectPath = @(
        'network/mod/ComfyNetworkSense/ComfyNetworkSense.csproj',
        'network/mod/ComfyNetworkSense.Tests/ComfyNetworkSense.Tests.csproj'
    )
}

$rootPrefix = $repoRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$violations = New-Object Collections.Generic.List[string]
foreach ($project in $ProjectPath) {
    $fullProject = $project
    if (-not [IO.Path]::IsPathRooted($fullProject)) { $fullProject = Join-Path $repoRoot $project }
    if (-not (Test-Path -LiteralPath $fullProject -PathType Leaf)) {
        throw "G5 project input missing: $project"
    }

    [xml]$xml = [IO.File]::ReadAllText($fullProject)
    $projectDirectory = Split-Path -Parent $fullProject
    foreach ($node in @($xml.Project.ItemGroup.ProjectReference, $xml.Project.ItemGroup.Compile, $xml.Project.ItemGroup.None)) {
        if ($null -eq $node) { continue }
        foreach ($item in @($node)) {
            $include = [string]$item.Include
            if ([string]::IsNullOrWhiteSpace($include) -or $include.Contains('$(')) { continue }
            if ($include -notmatch '[\\/]') { continue }
            $candidate = [IO.Path]::GetFullPath((Join-Path $projectDirectory $include))
            if (-not $candidate.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                $violations.Add("$project reaches outside the repository: $include")
            }
        }
    }
}

$modProject = [IO.File]::ReadAllText((Join-Path $repoRoot 'network/mod/ComfyNetworkSense/ComfyNetworkSense.csproj'))
foreach ($package in 'Comfy.Quest.Contracts', 'Comfy.Transport.Contracts') {
    if ($modProject -notmatch ('PackageReference\s+Include="' + [regex]::Escape($package) + '"')) {
        $violations.Add("mod project does not consume required package: $package")
    }
}

if ($violations.Count -gt 0) {
    foreach ($violation in $violations) { Write-Error $violation }
    throw ("G5 package-only guard rejected {0} violation(s)." -f $violations.Count)
}

Write-Output ("G5 package-only references verified across {0} project(s)." -f $ProjectPath.Count)
