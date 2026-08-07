#Requires -Version 5.1
<#
.SYNOPSIS
Build the small P7 alpha modpack zip from a local Valheim install.

.DESCRIPTION
The Companion installer applies only entries rooted under Valheim/ and preserves the
personalized ComfyNetworkSense config. This helper builds the package shape that
works for both first install and ordinary update: doorstop, BepInEx core, stable
support plugins/config, and the selected ComfyNetworkSense DLL.
#>
[CmdletBinding()]
param(
    [string] $ValheimRoot = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim',
    [string] $NetworkSenseDll = 'network\mod\ComfyNetworkSense\bin\Release\ComfyNetworkSense.dll',
    [string] $OutputDirectory = 'artifacts\modpacks',
    [Parameter(Mandatory)]
    [ValidatePattern('^m[0-9]+-[a-z0-9]+-[0-9]{8}-r[0-9]+$')]
    [string] $ReleaseId
)

$ErrorActionPreference = 'Stop'

function Resolve-LocalPath([string] $Path) {
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path (Get-Location) $Path))
}

function Add-PayloadFile([string] $RelativePath, [string] $SourceOverride = '') {
    $source = if ($SourceOverride) { Resolve-LocalPath $SourceOverride } else { Join-Path $ValheimRoot $RelativePath }
    if (!(Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "missing package payload file: $source"
    }

    $target = Join-Path $stageRoot (Join-Path 'Valheim' $RelativePath)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Copy-Item -LiteralPath $source -Destination $target -Force
}

$ValheimRoot = Resolve-LocalPath $ValheimRoot
$NetworkSenseDll = Resolve-LocalPath $NetworkSenseDll
$OutputDirectory = Resolve-LocalPath $OutputDirectory

if (!(Test-Path -LiteralPath $ValheimRoot -PathType Container)) { throw "ValheimRoot not found: $ValheimRoot" }
if (!(Test-Path -LiteralPath $NetworkSenseDll -PathType Leaf)) { throw "NetworkSenseDll not found: $NetworkSenseDll" }

$packageName = "Comfy-P7-Alpha-Mods-$ReleaseId.zip"
$packagePath = Join-Path $OutputDirectory $packageName
$stageRoot = Join-Path $OutputDirectory ("stage-" + $ReleaseId)

if (Test-Path -LiteralPath $stageRoot) { Remove-Item -LiteralPath $stageRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stageRoot, $OutputDirectory | Out-Null

$payload = @(
    'doorstop_config.ini',
    'winhttp.dll',
    'BepInEx/config/comfy-network-sense/quest-view.json',
    'BepInEx/core/0Harmony.dll',
    'BepInEx/core/0Harmony.xml',
    'BepInEx/core/0Harmony20.dll',
    'BepInEx/core/BepInEx.dll',
    'BepInEx/core/BepInEx.Harmony.dll',
    'BepInEx/core/BepInEx.Harmony.pdb',
    'BepInEx/core/BepInEx.Harmony.xml',
    'BepInEx/core/BepInEx.pdb',
    'BepInEx/core/BepInEx.Preloader.dll',
    'BepInEx/core/BepInEx.Preloader.pdb',
    'BepInEx/core/BepInEx.Preloader.xml',
    'BepInEx/core/BepInEx.xml',
    'BepInEx/core/HarmonyXInterop.dll',
    'BepInEx/core/HarmonyXInterop.pdb',
    'BepInEx/core/Mono.Cecil.dll',
    'BepInEx/core/Mono.Cecil.Mdb.dll',
    'BepInEx/core/Mono.Cecil.Pdb.dll',
    'BepInEx/core/Mono.Cecil.Rocks.dll',
    'BepInEx/core/MonoMod.RuntimeDetour.dll',
    'BepInEx/core/MonoMod.RuntimeDetour.xml',
    'BepInEx/core/MonoMod.Utils.dll',
    'BepInEx/core/MonoMod.Utils.xml',
    'BepInEx/plugins/ComfyCameraProof.dll',
    'BepInEx/plugins/ComfyControlSurface.dll',
    'BepInEx/plugins/ComfyNetworkSense.dll',
    'doorstop_libs/libdoorstop_x64.dylib',
    'doorstop_libs/libdoorstop_x64.so'
)

foreach ($relative in $payload) {
    if ($relative -eq 'BepInEx/plugins/ComfyNetworkSense.dll') {
        Add-PayloadFile $relative $NetworkSenseDll
    } else {
        Add-PayloadFile $relative
    }
}

# The /join first-install lane REQUIRES a config template entry: ModPackBuilder
# personalizes the [Lumberjacks] block inside the zip and 503s when the entry is
# absent. (The Companion UPDATE lane is unaffected — BuildConfigPreservingUpdatePack
# strips the entry so an update never overwrites a personalized config.) Stage a
# SANITIZED copy of the local config: the enrollment credential pair is blanked and
# the consumer lane disarmed, so the template carries no machine credential even if
# a future personalizer misses a key.
$configRelative = 'BepInEx/config/djcdevelopment.valheim.comfynetworksense.cfg'
$configSource = Join-Path $ValheimRoot ($configRelative -replace '/', '\')
if (!(Test-Path -LiteralPath $configSource -PathType Leaf)) {
    throw "missing config template source: $configSource"
}
$configText = [IO.File]::ReadAllText($configSource)
foreach ($blankKey in @('lumberjacksEnrollmentId', 'lumberjacksClientAccessKey')) {
    $configText = [regex]::Replace($configText,
        '(?m)^(' + [regex]::Escape($blankKey) + '\s*=\s*)\S*\s*$', '${1}')
}
$configText = [regex]::Replace($configText,
    '(?m)^(zdoAuthoritativeConsumerEnabled\s*=\s*)\S*\s*$', '${1}false')
$configTarget = Join-Path $stageRoot (Join-Path 'Valheim' ($configRelative -replace '/', '\'))
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $configTarget) | Out-Null
[IO.File]::WriteAllText($configTarget, $configText, [Text.UTF8Encoding]::new($false))

$readme = @"
Comfy P7 alpha modpack

Release: $ReleaseId
Payload root: Valheim/
Generated: $((Get-Date).ToUniversalTime().ToString('o'))

The Companion installer preserves the personalized ComfyNetworkSense access-key
config and applies only Valheim-relative payload files.
"@
[IO.File]::WriteAllText((Join-Path $stageRoot 'README.txt'), $readme, [Text.UTF8Encoding]::new($false))

if (Test-Path -LiteralPath $packagePath) { Remove-Item -LiteralPath $packagePath -Force }
Get-ChildItem -LiteralPath $stageRoot -Force | Compress-Archive -DestinationPath $packagePath -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item -LiteralPath $packagePath).Length

[pscustomobject]@{
    release = $ReleaseId
    package = $packagePath
    sha256 = $hash
    size_bytes = $size
}
