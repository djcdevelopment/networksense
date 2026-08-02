<#
.SYNOPSIS
Start the Docker-backed Lumberjacks Companion on the i5 with the Valheim directory mounted.

.DESCRIPTION
Runs over the existing `i5` ssh alias. The script writes a project-local compose env file on the
i5, then starts the Companion with both docker-compose.yml and docker-compose.valheim.yml.

This is the durable i5 lane for the Companion updater. Starting with only the base compose file
creates a read-only dashboard: it cannot see /valheim, cannot find the ComfyNetworkSense config,
and cannot perform client-pull mod updates.

.PARAMETER RemoteRoot
The baseline staging checkout on the i5.

.PARAMETER ValheimPath
The Windows Valheim install path on the i5.

.PARAMETER GatewayUrl
Gateway origin used by Companion. Lab defaults to the OMEN Lab Gateway over the
tailnet; other profiles retain the public release Gateway default. An explicit
value overrides the profile default.

.EXAMPLE
.\tools\i5\Start-I5Companion.ps1
#>
[CmdletBinding()]
param(
    [string]$RemoteRoot = 'C:\deploy\baseline\i5-companion',
    [string]$ValheimPath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim',
    [ValidateSet('Explore','Admin','Dev','Lab','Production')]
    [string]$Profile = 'Explore',
    [ValidateRange(1024,65535)]
    [int]$McpPort = 8721,
    [string]$GatewayUrl = ''
)

$ErrorActionPreference = 'Stop'
$sshOptions = @('-o', 'BatchMode=yes', '-o', 'ConnectTimeout=8')
$sshAlias = 'i5'
$sshArgs = $sshOptions + $sshAlias

$selectedGatewayUrl = $GatewayUrl
if ([string]::IsNullOrWhiteSpace($selectedGatewayUrl)) {
    $selectedGatewayUrl = if ($Profile -eq 'Lab') {
        'http://100.124.12.37:4000'
    } else {
        'https://comfy-p7.duckdns.org'
    }
}
$parsedGatewayUrl = $null
if (-not [Uri]::TryCreate($selectedGatewayUrl, [UriKind]::Absolute, [ref]$parsedGatewayUrl) -or
    $parsedGatewayUrl.Scheme -notin @('http','https') -or
    -not [string]::IsNullOrWhiteSpace($parsedGatewayUrl.UserInfo) -or
    -not [string]::IsNullOrWhiteSpace($parsedGatewayUrl.Query) -or
    -not [string]::IsNullOrWhiteSpace($parsedGatewayUrl.Fragment)) {
    throw "GatewayUrl must be an absolute http(s) origin without credentials, query, or fragment: $selectedGatewayUrl"
}
$selectedGatewayUrl = $selectedGatewayUrl.TrimEnd('/')

$remote = @'
$ErrorActionPreference = 'Stop'
$remoteRoot = '__REMOTE_ROOT__'
$valheim = '__VALHEIM_PATH__'
$profile = '__PROFILE__'
$mcpPort = __MCP_PORT__
$gatewayUrl = '__GATEWAY_URL__'
if (-not (Test-Path -LiteralPath $remoteRoot)) { throw "Companion staging root not found: $remoteRoot" }
if (-not (Test-Path -LiteralPath $valheim)) { throw "Valheim path not found: $valheim" }

Set-Location $remoteRoot
$envFile = Join-Path $remoteRoot 'tools\companion\.env'
$latestBootstrapFile = Join-Path $remoteRoot 'tools\companion\latest-bootstrap.json'
$workbenchData = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Lumberjacks\Workbench'
New-Item -ItemType Directory -Force -Path $workbenchData | Out-Null
$runnerTokenPath = Join-Path $workbenchData 'runner-token'
if (-not (Test-Path -LiteralPath $runnerTokenPath -PathType Leaf)) {
    $runnerBytes = New-Object byte[] 32
    $runnerRng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $runnerRng.GetBytes($runnerBytes) } finally { $runnerRng.Dispose() }
    $runnerToken = [Convert]::ToBase64String($runnerBytes).Replace('+','-').Replace('/','_').TrimEnd('=')
    [IO.File]::WriteAllText($runnerTokenPath, $runnerToken + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}
$bootstrapRelease = 'unknown'
if (Test-Path -LiteralPath $latestBootstrapFile) {
    try {
        $latestBootstrap = Get-Content -LiteralPath $latestBootstrapFile -Raw | ConvertFrom-Json
        if ($latestBootstrap.release) { $bootstrapRelease = $latestBootstrap.release }
    } catch {
        Write-Output ("Could not read latest bootstrap pointer: {0}" -f $_.Exception.Message)
    }
}
Set-Content -LiteralPath $envFile -Value @(
    'LUMBERJACKS_VALHEIM_HOST_PATH=' + $valheim
    'LUMBERJACKS_COMPANION_BOOTSTRAP_RELEASE=' + $bootstrapRelease
    'LUMBERJACKS_COMPANION_SOURCE_REVISION=bundle:' + $bootstrapRelease
    'LUMBERJACKS_COMPANION_SOURCE_BRANCH=release-bundle'
    'LUMBERJACKS_COMPANION_SOURCE_DIRTY=false'
    'LUMBERJACKS_COMPANION_IMAGE=lumberjacks-companion-companion:' + $bootstrapRelease
    'LUMBERJACKS_COMPANION_GATEWAY_URL=' + $gatewayUrl
    'LUMBERJACKS_WORKBENCH_PROFILE=' + $profile
    'COMFY_WORKBENCH_MCP_PORT=' + $mcpPort
    'LUMBERJACKS_WORKBENCH_REPO_ROOT=' + $remoteRoot
    'LUMBERJACKS_WORKBENCH_RUNNER_TOKEN_PATH=' + $runnerTokenPath
) -Encoding ascii

function Test-DockerServer {
    param([int]$TimeoutSeconds = 8)

    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        return [pscustomobject]@{ Ok = $false; Detail = 'docker CLI not found on i5' }
    }

    $pipe = Test-Path -LiteralPath '\\.\pipe\dockerDesktopLinuxEngine'
    $job = Start-Job -ScriptBlock {
        $version = docker version --format '{{.Server.Version}}' 2>&1
        if ($LASTEXITCODE -ne 0) { return $version }
        $os = docker info --format '{{.OSType}}' 2>&1
        if ($LASTEXITCODE -ne 0) { return $os }
        "version=$version os=$os"
    }
    if (-not (Wait-Job $job -Timeout $TimeoutSeconds)) {
        Stop-Job $job -ErrorAction SilentlyContinue
        Remove-Job $job -Force -ErrorAction SilentlyContinue
        return [pscustomobject]@{ Ok = $false; Detail = "docker server did not answer within $TimeoutSeconds seconds; linux engine pipe present=$pipe" }
    }

    $output = Receive-Job $job -ErrorAction SilentlyContinue
    Remove-Job $job -Force -ErrorAction SilentlyContinue
    if ($output) {
        $server = ($output | Select-Object -Last 1).ToString().Trim()
        if ($server -match 'version=(?<version>\S+)\s+os=linux') {
            return [pscustomobject]@{ Ok = $true; Detail = "Docker server $($Matches.version) linux engine ready" }
        }
        return [pscustomobject]@{ Ok = $false; Detail = (($output | Out-String) -replace '\s+', ' ').Trim() }
    }

    return [pscustomobject]@{ Ok = $false; Detail = "docker server returned no version; linux engine pipe present=$pipe" }
}

function Wait-DockerServer {
    param([int]$Seconds = 45)

    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        $state = Test-DockerServer -TimeoutSeconds 8
        if ($state.Ok) { return $state }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)
    return $state
}

function Start-DockerDesktopTask {
    $taskName = 'LumberjacksDockerDesktop'
    $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if (-not $task) { return $false }

    $settings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries `
        -StartWhenAvailable `
        -ExecutionTimeLimit ([TimeSpan]::Zero) `
        -MultipleInstances IgnoreNew
    Set-ScheduledTask -TaskName $taskName -Settings $settings | Out-Null
    Start-ScheduledTask -TaskName $taskName
    return $true
}

$dockerState = Test-DockerServer -TimeoutSeconds 8
if (-not $dockerState.Ok) {
    if (Start-DockerDesktopTask) {
        $dockerState = Wait-DockerServer -Seconds 45
    }
}
if (-not $dockerState.Ok) {
    throw "Docker Desktop Linux engine is not ready on i5: $($dockerState.Detail). Log into i5 once and confirm Docker Desktop is running, then rerun Start-I5Companion.ps1. Do not start the Companion with the base compose file only."
}
Write-Output ("Docker ready: {0}" -f $dockerState.Detail)
Write-Output ("Workbench Gateway: {0} ({1} profile)" -f $gatewayUrl, $profile)

$composeArgs = @(
    'compose',
    '-p', 'lumberjacks-companion',
    '--env-file', $envFile,
    '-f', '.\tools\companion\docker-compose.yml',
    '-f', '.\tools\companion\docker-compose.valheim.yml',

    'up', '-d', '--build', '--force-recreate'
)
if ($profile -in @('Dev', 'Lab')) { $composeArgs = $composeArgs[0..6] + @('--profile', $profile.ToLowerInvariant()) + $composeArgs[7..($composeArgs.Count - 1)] }
$downArgs = @('compose', '-p', 'lumberjacks-companion', '--env-file', $envFile, '-f', '.\tools\companion\docker-compose.yml', '-f', '.\tools\companion\docker-compose.valheim.yml', '--profile', 'dev', '--profile', 'lab', 'down')
& docker @downArgs
if ($LASTEXITCODE -ne 0) { throw 'i5 Companion profile convergence failed' }
if ($profile -in @('Dev', 'Lab')) {
    $listeners = @(Get-NetTCPConnection -LocalPort $mcpPort -State Listen -ErrorAction SilentlyContinue)
    if ($listeners.Count -gt 0) {
        $owners = @($listeners | ForEach-Object { "PID $($_.OwningProcess)" } | Sort-Object -Unique)
        throw "Dev MCP loopback port $mcpPort is already listening ($($owners -join ', ')). Stop the existing Dev MCP or rerun with -McpPort <free-port>."
    }
}
& docker @composeArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$ready = $false
for ($i = 0; $i -lt 30; $i++) {
    try {
        Invoke-RestMethod -TimeoutSec 5 http://127.0.0.1:8080/health | Out-Null
        $ready = $true
        break
    } catch {
        Start-Sleep -Seconds 1
    }
}
if (-not $ready) {
    docker logs --tail 100 lumberjacks-companion-companion-1
    throw 'Companion did not answer on http://127.0.0.1:8080.'
}

$status = Invoke-RestMethod -TimeoutSec 15 http://127.0.0.1:8080/api/v0/companion/status
$status | ConvertTo-Json -Depth 8
$runner = Join-Path $remoteRoot 'tools\companion\Start-WorkbenchHostRunner.ps1'
if (Test-Path -LiteralPath $runner) {
    Start-Process -FilePath 'powershell.exe' -WindowStyle Hidden -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $runner,
        '-CompanionUrl', 'http://127.0.0.1:8080',
        '-ContainerName', 'lumberjacks-companion-companion-1',
        '-ProjectName', 'lumberjacks-companion',
        '-Profile', $profile,
        '-ComposeFile', ('"{0}"' -f (Join-Path $remoteRoot 'tools\companion\docker-compose.yml')),
        '-RepoRoot', ('"{0}"' -f $remoteRoot),
        '-ValheimPath', ('"{0}"' -f $valheim),
        '-ReplaceExisting'
    ) | Out-Null
    Write-Output ('Workbench host runner started for profile {0}' -f $profile)
}
'@

$escaped = $remote.
    Replace('__REMOTE_ROOT__', $RemoteRoot.Replace("'", "''")).
    Replace('__VALHEIM_PATH__', $ValheimPath.Replace("'", "''")).
    Replace('__PROFILE__', $Profile).
    Replace('__MCP_PORT__', [string]$McpPort).
    Replace('__GATEWAY_URL__', $selectedGatewayUrl.Replace("'", "''"))

$remoteScriptDir = 'C:/deploy/baseline'
$remoteScript = "$remoteScriptDir/Start-I5Companion.remote.ps1"
$mkRemoteScriptDir = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes("New-Item -ItemType Directory -Force -Path '$remoteScriptDir' | Out-Null"))
ssh @sshArgs "powershell.exe -NoProfile -EncodedCommand $mkRemoteScriptDir"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$tmp = New-TemporaryFile
try {
    Set-Content -LiteralPath $tmp.FullName -Value $escaped -Encoding UTF8
    scp -q @sshOptions $tmp.FullName "${sshAlias}:$remoteScript"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    ssh @sshArgs "powershell.exe -NoProfile -ExecutionPolicy Bypass -File $remoteScript"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} finally {
    Remove-Item -LiteralPath $tmp.FullName -Force -ErrorAction SilentlyContinue
}
