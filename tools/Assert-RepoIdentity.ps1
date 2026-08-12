#Requires -Version 5.1
[CmdletBinding()]
param(
    [string] $ExpectedRepository = 'djcdevelopment/networksense',
    [string] $RepositoryRoot = '',
    [switch] $DefineOnly
)

$script:NetworkSenseIdentityRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$script:NetworkSenseExpectedRepository = $ExpectedRepository

function Assert-RepoIdentity {
    [CmdletBinding()]
    param(
        [string] $Expected = $script:NetworkSenseExpectedRepository,
        [string] $Root = $script:NetworkSenseIdentityRoot
    )

    $ErrorActionPreference = 'Stop'
    if (-not (Test-Path -LiteralPath (Join-Path $Root '.git'))) {
        throw "REPO IDENTITY FAILURE: '$Root' is not a Git checkout. Refusing to act."
    }

    $origin = @(& git -C $Root remote get-url origin 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $origin) {
        throw "REPO IDENTITY FAILURE: no origin is resolvable from '$Root'. Refusing to act."
    }

    $originText = ($origin -join [Environment]::NewLine).Trim()
    if ($originText -notmatch [regex]::Escape($Expected)) {
        throw ("REPO IDENTITY FAILURE: origin is '{0}', expected '*{1}*'. Refusing to act." -f $originText, $Expected)
    }

    Write-Output ("repo identity verified: {0}" -f $Expected)
}

if (-not $DefineOnly) {
    $root = $RepositoryRoot
    if ([string]::IsNullOrWhiteSpace($root)) { $root = $script:NetworkSenseIdentityRoot }
    Assert-RepoIdentity -Expected $ExpectedRepository -Root $root
}
