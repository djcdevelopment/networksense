<#
.SYNOPSIS
Deploy files or directories from OMEN to the i5 over the tailnet ssh lane,
with end-to-end SHA256 verification.

.DESCRIPTION
Copies each -Path item (file or directory, recursively) into -Dest on the i5.
Default destination is the staging root C:/deploy/baseline. After the copy,
every deployed file is re-hashed on BOTH ends and compared; any mismatch or
missing file fails the run with exit 1. Nothing about this script prompts --
it is safe for unattended agent use (BatchMode ssh, no password fallback).

Requires: the `i5` ssh alias in ~/.ssh/config and OMEN's key authorized on the
i5 (see tools/i5/README.md). OpenSSH >= 9.0 client (scp in SFTP mode -- remote
paths with spaces are passed literally).

.PARAMETER Path
One or more local files or directories to deploy. Directories are copied
recursively and land as <Dest>/<dirname>/...

.PARAMETER Dest
Remote destination directory (Windows path on the i5, forward or back slashes).
Default: C:/deploy/baseline (the staging root -- auto-created).

.PARAMETER ValheimPlugins
Shortcut: target the i5's live BepInEx plugins directory
(C:/Program Files (x86)/Steam/steamapps/common/Valheim/BepInEx/plugins).
Overrides -Dest.

.PARAMETER DryRun
Print the deploy plan (files and remote paths) without copying anything.

.PARAMETER ExcludeDirectoryName
Directory leaf names to skip when deploying directories recursively. Useful for build outputs such
as bin and obj; exact leaf-name match only.

.EXAMPLE
.\Deploy-ToI5.ps1 -Path .\ComfyNetworkSense.dll -ValheimPlugins

.EXAMPLE
.\Deploy-ToI5.ps1 -Path .\bundle\ -Dest C:/deploy/baseline/run-042
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string[]]$Path,

    [string]$Dest = 'C:/deploy/baseline',

    [switch]$ValheimPlugins,

    # Shortcut: target the i5's live BepInEx config directory. Same trust class as
    # -ValheimPlugins (the deploy lane already writes into the live install); used to
    # ship a personalized ComfyNetworkSense config for the at-rest cutover posture.
    [switch]$ValheimConfig,

    [switch]$DryRun,

    [string[]]$ExcludeDirectoryName = @()
)

$SshAlias = 'i5'
$SshOpts  = @('-o', 'BatchMode=yes', '-o', 'ConnectTimeout=8')

# Remote calls that embed a path list in a -EncodedCommand payload grow with the
# manifest, and the i5's sshd spawns the command through a shell capped at 8191
# characters. Batch those calls so no single command line approaches the cap.
$RemoteCommandBudget = 6000

function Split-IntoRemoteBatches {
    param(
        [string[]]$Items,
        [int]$OverheadChars = 0
    )

    # base64 over UTF-16LE costs ~8/3 command-line characters per script
    # character; 128 covers the "powershell.exe -NoProfile -EncodedCommand "
    # prefix and rounding.
    $maxScriptChars = [int]((($RemoteCommandBudget - 128) * 3) / 8) - $OverheadChars
    if ($maxScriptChars -lt 1) { $maxScriptChars = 1 }

    $batches = @()
    $current = @()
    $used = 0
    foreach ($item in $Items) {
        # quotes, indent, doubled apostrophes and the line separator
        $cost = $item.Length + 10
        if ($current.Count -gt 0 -and ($used + $cost) -gt $maxScriptChars) {
            $batches += , $current
            $current = @()
            $used = 0
        }
        $current += $item
        $used += $cost
    }
    if ($current.Count -gt 0) { $batches += , $current }
    return , $batches
}

function ConvertTo-QuotedPathList {
    param([string[]]$Items)
    return (@($Items | ForEach-Object { "    '$($_.Replace("'", "''"))'" }) -join ",`r`n")
}

# Native ssh/scp stderr noise (e.g. the OpenSSH post-quantum KEX warning, first
# seen 2026-07-31) must stay non-terminating even when a caller invokes this script
# in-process under -ErrorAction Stop. Delivery is decided by the explicit SHA256
# verification and throws below, never by stderr chatter.
$ErrorActionPreference = 'Continue'

if ($ValheimPlugins) {
    $Dest = 'C:/Program Files (x86)/Steam/steamapps/common/Valheim/BepInEx/plugins'
}
if ($ValheimConfig) {
    $Dest = 'C:/Program Files (x86)/Steam/steamapps/common/Valheim/BepInEx/config'
}
$Dest = ($Dest -replace '\\', '/').TrimEnd('/')

# --- Resolve sources into a manifest: local file -> exact remote path --------
$items = @()
foreach ($p in $Path) {
    $items += Get-Item -LiteralPath $p -ErrorAction Stop
}
$leafNames = $items | ForEach-Object { $_.Name }
$dupes = $leafNames | Group-Object | Where-Object { $_.Count -gt 1 }
if ($dupes) {
    throw "duplicate top-level names would overwrite each other remotely: $(($dupes | ForEach-Object Name) -join ', ')"
}

$manifest = @()
foreach ($item in $items) {
    if ($item.PSIsContainer) {
        $excludeSet = @{}
        foreach ($name in $ExcludeDirectoryName) {
            if (-not [string]::IsNullOrWhiteSpace($name)) { $excludeSet[$name] = $true }
        }
        $files = Get-ChildItem -LiteralPath $item.FullName -Recurse -File | Where-Object {
            $relativeDirectory = $_.DirectoryName.Substring($item.FullName.Length).TrimStart('\', '/')
            if ([string]::IsNullOrWhiteSpace($relativeDirectory)) { return $true }
            foreach ($segment in ($relativeDirectory -split '[\\/]')) {
                if ($excludeSet.ContainsKey($segment)) { return $false }
            }
            return $true
        }
        if (-not $files) { throw "directory has no files: $($item.FullName)" }
        foreach ($f in $files) {
            $rel = $f.FullName.Substring($item.FullName.Length).TrimStart('\', '/') -replace '\\', '/'
            $manifest += [pscustomobject]@{
                Local  = $f.FullName
                Remote = "$Dest/$($item.Name)/$rel"
            }
        }
    } else {
        $manifest += [pscustomobject]@{
            Local  = $item.FullName
            Remote = "$Dest/$($item.Name)"
        }
    }
}

Write-Host ("deploy -> {0}:{1}   [{2} file(s)]" -f $SshAlias, $Dest, $manifest.Count)
foreach ($m in $manifest) { Write-Host ("  {0}" -f $m.Remote) }
if ($DryRun) {
    Write-Host 'dry run - nothing copied'
    exit 0
}

# --- Preflight: key auth answers (fail fast, never hang on a password) -------
$null = ssh @SshOpts $SshAlias "whoami" 2>$null
if ($LASTEXITCODE -ne 0) {
    throw "i5 ssh lane not available - run tools/i5/Test-I5Link.ps1 (offline is normal for this roaming laptop)"
}

# --- Ensure the remote destination directory exists --------------------------
$mkTemplate = @'
New-Item -ItemType Directory -Force -Path '__DEST__' | Out-Null
'@
$b64 = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($mkTemplate.Replace('__DEST__', $Dest)))
$null = ssh @SshOpts $SshAlias "powershell.exe -NoProfile -EncodedCommand $b64" 2>$null
if ($LASTEXITCODE -ne 0) { throw "could not create remote directory: $Dest" }

# --- Copy: exact manifest files only ----------------------------------------
# Do not scp whole directories here. The manifest may intentionally exclude
# build output directories such as bin/obj, and copying the top-level directory
# would silently ship excluded files while verification ignores them.
$remoteParents = @(
    $manifest |
        ForEach-Object { Split-Path -Parent $_.Remote } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Sort-Object -Unique
)
if ($remoteParents.Count -gt 0) {
    $mkdirTemplate = @'
$paths = @(
__PATHS__
)
foreach ($p in $paths) {
    New-Item -ItemType Directory -Force -Path $p | Out-Null
}
'@
    $mkdirBatches = Split-IntoRemoteBatches -Items $remoteParents -OverheadChars $mkdirTemplate.Length
    Write-Verbose ("mkdir: {0} path(s) in {1} remote call(s)" -f $remoteParents.Count, @($mkdirBatches).Count)
    foreach ($batch in $mkdirBatches) {
        $mkdirScript = $mkdirTemplate.Replace('__PATHS__', (ConvertTo-QuotedPathList -Items $batch))
        $b64 = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($mkdirScript))
        $null = ssh @SshOpts $SshAlias "powershell.exe -NoProfile -EncodedCommand $b64" 2>$null
        if ($LASTEXITCODE -ne 0) { throw 'could not create remote manifest parent directories' }
    }
}

foreach ($m in $manifest) {
    $scpArgs = @('-q') + $SshOpts
    & scp @scpArgs $m.Local "${SshAlias}:$($m.Remote)"
    if ($LASTEXITCODE -ne 0) { throw "scp failed for $($m.Local) -> $($m.Remote)" }
}

# --- Verify: recompute SHA256 on both ends, compare per file -----------------
$verifyTemplate = @'
$paths = @(
__PATHS__
)
foreach ($p in $paths) {
    if (Test-Path -LiteralPath $p) { $h = (Get-FileHash -Algorithm SHA256 -LiteralPath $p).Hash }
    else { $h = 'MISSING' }
    Write-Output ($p + '|' + $h)
}
'@
$remotePaths = @($manifest | ForEach-Object Remote)
$verifyBatches = Split-IntoRemoteBatches -Items $remotePaths -OverheadChars $verifyTemplate.Length
Write-Verbose ("verify: {0} path(s) in {1} remote call(s)" -f $remotePaths.Count, @($verifyBatches).Count)
$remoteLines = @()
foreach ($batch in $verifyBatches) {
    $verifyScript = $verifyTemplate.Replace('__PATHS__', (ConvertTo-QuotedPathList -Items $batch))
    $b64 = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($verifyScript))
    $batchLines = ssh @SshOpts $SshAlias "powershell.exe -NoProfile -EncodedCommand $b64" 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'remote hash verification call failed' }
    $remoteLines += @($batchLines)
}

$remoteHash = @{}
foreach ($line in @($remoteLines)) {
    $parts = $line -split '\|', 2
    if ($parts.Count -eq 2) { $remoteHash[$parts[0]] = $parts[1] }
}

$failed = 0
foreach ($m in $manifest) {
    $localH = (Get-FileHash -Algorithm SHA256 -LiteralPath $m.Local).Hash
    $remoteH = $remoteHash[$m.Remote]
    if (-not $remoteH) { $remoteH = 'NO-REPORT' }
    if ($localH -eq $remoteH) {
        Write-Host ("  OK    {0}  sha256:{1}" -f $m.Remote, $localH.Substring(0, 12).ToLower())
    } else {
        Write-Host ("  FAIL  {0}  local={1} remote={2}" -f $m.Remote, $localH, $remoteH)
        $failed++
    }
}

if ($failed -gt 0) {
    Write-Host ("deploy FAILED verification: {0}/{1} file(s) mismatched" -f $failed, $manifest.Count)
    exit 1
}
Write-Host ("deploy verified: {0}/{0} file(s) match on the i5" -f $manifest.Count)
exit 0
