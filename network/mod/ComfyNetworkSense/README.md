# ComfyNetworkSense

Early telemetry and HUD scaffold for the network research fork.

This plugin is not trying to "fix Valheim netcode" yet.

It is the first instrument panel:

- local HUD for player awareness
- client live sampling
- server pulse sampling
- JSONL logs for later analysis
- early scores for owner fit, pressure, and low-impact recommendations

## Current scope

The first build does these things:

- shows an in-game IMGUI HUD
- samples local ping, jitter, FPS, frame timing, nearby density, and basic traffic rates
- broadcasts a periodic server pulse to connected clients
- logs `telemetry-client.jsonl`, `telemetry-server.jsonl`, `event-timeline.jsonl`, and `benchmark-results.jsonl`
- supports manual mode cycling: `Auto`, `Low Impact`, `Combat`, `Staging`
- supports a simple safe-state benchmark capture

This build does not yet do automatic authority selection, queue prioritization, or policy enforcement.

## Default shortcuts

- `F6`: cycle network mode
- `F7`: toggle HUD
- `Shift+F7`: cycle HUD detail
- `F8`: start or cancel benchmark

## Output

Logs are written under:

```text
BepInEx\config\comfy-network-sense\
```

## Build

From this project directory:

```powershell
dotnet build .\ComfyNetworkSense.csproj -c Release
```

If Valheim is installed in the default Steam path, the DLL is copied into:

```text
C:\Program Files (x86)\Steam\steamapps\common\Valheim\BepInEx\plugins
```
