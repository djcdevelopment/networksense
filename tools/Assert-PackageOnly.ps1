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

$publicPropsPath = Join-Path $repoRoot 'eng/dependencies.public.props'
$interimPropsPath = Join-Path $repoRoot 'eng/dependencies.interim.props'
$publicConfigPath = Join-Path $repoRoot 'nuget.config'
$interimConfigPath = Join-Path $repoRoot 'nuget.interim.config'
foreach ($required in @($publicPropsPath, $interimPropsPath, $publicConfigPath, $interimConfigPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        $violations.Add("dependency profile input missing: $required")
    }
}

if ($violations.Count -eq 0) {
    [xml]$publicProps = [IO.File]::ReadAllText($publicPropsPath)
    [xml]$interimProps = [IO.File]::ReadAllText($interimPropsPath)
    foreach ($property in 'ComfyQuestContractsVersion', 'ComfyTransportContractsVersion') {
        if ([string]$publicProps.Project.PropertyGroup.$property -ne '[0.1.0]') {
            $violations.Add("public dependency pin is not exact [0.1.0]: $property")
        }
        if ([string]$interimProps.Project.PropertyGroup.$property -ne '0.1.0-local') {
            $violations.Add("interim dependency pin is not 0.1.0-local: $property")
        }
    }

    $publicConfig = [IO.File]::ReadAllText($publicConfigPath)
    $interimConfig = [IO.File]::ReadAllText($interimConfigPath)
    if ($publicConfig -match '(?i)packages-local') {
        $violations.Add('public NuGet config includes the interim local feed')
    }
    if ($interimConfig -notmatch '(?i)packages-local') {
        $violations.Add('interim NuGet config does not include packages-local')
    }
    if ($modProject -notmatch 'Version="\$\(ComfyQuestContractsVersion\)"' -or
        $modProject -notmatch 'Version="\$\(ComfyTransportContractsVersion\)"') {
        $violations.Add('mod PackageReferences do not flow through the declared dependency profiles')
    }
}

if ($violations.Count -gt 0) {
    foreach ($violation in $violations) { Write-Error $violation }
    throw ("G5 package-only guard rejected {0} violation(s)." -f $violations.Count)
}

Write-Output ("G5 package-only references verified across {0} project(s)." -f $ProjectPath.Count)
