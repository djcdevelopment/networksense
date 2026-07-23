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

    [switch]$DryRun,

    [string[]]$ExcludeDirectoryName = @()
)

$SshAlias = 'i5'
$SshOpts  = @('-o', 'BatchMode=yes', '-o', 'ConnectTimeout=8')

if ($ValheimPlugins) {
    $Dest = 'C:/Program Files (x86)/Steam/steamapps/common/Valheim/BepInEx/plugins'
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

# --- Copy: one scp per top-level item (SFTP mode handles spaces literally) ---
foreach ($item in $items) {
    $scpArgs = @('-q') + $SshOpts
    if ($item.PSIsContainer) { $scpArgs += '-r' }
    & scp @scpArgs $item.FullName "${SshAlias}:$Dest/"
    if ($LASTEXITCODE -ne 0) { throw "scp failed for $($item.FullName)" }
}

# --- Verify: recompute SHA256 on both ends, compare per file -----------------
$verifyTemplate = @'
$paths = '__PATHS__' -split ';'
foreach ($p in $paths) {
    if (Test-Path -LiteralPath $p) { $h = (Get-FileHash -Algorithm SHA256 -LiteralPath $p).Hash }
    else { $h = 'MISSING' }
    Write-Output ($p + '|' + $h)
}
'@
$verifyScript = $verifyTemplate.Replace('__PATHS__', (($manifest | ForEach-Object Remote) -join ';'))
$b64 = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($verifyScript))
$remoteLines = ssh @SshOpts $SshAlias "powershell.exe -NoProfile -EncodedCommand $b64" 2>$null
if ($LASTEXITCODE -ne 0) { throw 'remote hash verification call failed' }

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
