#Requires -Version 5.1
[CmdletBinding()]
param(
    [string[]] $Path = @()
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$explicitPaths = $Path.Count -gt 0

if ($explicitPaths) {
    $files = @($Path)
} else {
    $allowedExtensions = @('.cs', '.csproj', '.props', '.targets', '.ps1', '.psm1', '.py', '.yml', '.yaml', '.json', '.xml', '.config', '.cmd', '.sh')
    $files = @(& git -C $repoRoot ls-files | Where-Object {
        $extension = [IO.Path]::GetExtension($_).ToLowerInvariant()
        $allowedExtensions -contains $extension -and
        $_ -ne 'tools/Assert-NoReachIn.ps1' -and
        $_ -notlike '*.disabled'
    })
}

$patterns = @(
    [pscustomobject]@{ Name = 'absolute work-root path'; Regex = '(?i)C:[\\/]+work[\\/]' },
    [pscustomobject]@{ Name = 'former monorepo parent traversal'; Regex = '(?i)(?:\.\.[\\/]){3,}(?:network|Lumberjacks)(?:[\\/])' },
    [pscustomobject]@{ Name = 'baseline source reach-in URL'; Regex = '(?i)github\.com/djcdevelopment/baseline/(?:blob|tree)/' }
)

$violations = New-Object Collections.Generic.List[string]
foreach ($file in $files) {
    $fullPath = $file
    if (-not [IO.Path]::IsPathRooted($fullPath)) { $fullPath = Join-Path $repoRoot $file }
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "boundary scan input does not exist: $file"
    }

    $lineNumber = 0
    foreach ($line in [IO.File]::ReadAllLines($fullPath)) {
        $lineNumber++
        foreach ($pattern in $patterns) {
            if ($line -match $pattern.Regex) {
                $violations.Add(("{0}:{1}: {2}" -f $file, $lineNumber, $pattern.Name))
            }
        }
    }
}

if ($violations.Count -gt 0) {
    foreach ($violation in $violations) { Write-Error $violation }
    throw ("G1 no-reach-in guard rejected {0} violation(s)." -f $violations.Count)
}

Write-Output ("G1 no-reach-in verified across {0} file(s)." -f $files.Count)
