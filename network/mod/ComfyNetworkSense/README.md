# ComfyNetworkSense

Valheim network-awareness mod and local MCP-assisted development loop.

This project is an instrument panel, not a netcode replacement. It captures local
client signals, server pulse snapshots, benchmark/test events, and optional
Raven/MCP summaries so we can understand multiplayer conditions before changing
gameplay policy.

## Table Of Contents

- [What Is Built](#what-is-built)
- [Architecture Overview](#architecture-overview)
- [Setup And Build](#setup-and-build)
- [MCP Gateway Setup](#mcp-gateway-setup)
- [Testing](#testing)
- [Using It In Game](#using-it-in-game)
- [Outputs And Files](#outputs-and-files)
- [Debugging](#debugging)
- [How To Extend](#how-to-extend)
- [Current Boundaries](#current-boundaries)

## What Is Built

- BepInEx Valheim plugin: `ComfyNetworkSense.dll`.
- Upper-left HUD with `Minimal`, `Compact`, and `Diagnostic` presets.
- Modern IMGUI debug drawer with `Debug`, `Signals`, and `Raven` tabs.
- Mode selector visible in the panel: `Solo`, `Combat`, `Group`, `Town`.
- Client telemetry sampler for ping, jitter, FPS, frame timing, traffic rates,
  nearby players/entities/build pieces, danger, and zone.
- Server pulse broadcaster for host/server-side pressure and ownership context.
- Score calculator for connection, owner fit, pressure, confidence, and internal
  low-impact recommendation signals.
- JSONL telemetry/event/benchmark logs under BepInEx config.
- Session export JSON files for host/client comparison.
- Local Comfy MCP gateway integration for Raven recaps, next-test suggestions,
  config suggestions, whitelisted config profiles, notes, and file/log tooling.

## Architecture Overview

### In-Game Plugin

The plugin entrypoint is `ComfyNetworkSense.cs`. It binds config, registers
Valheim console commands, applies panel input patches, owns the telemetry
coordinator, and forwards Unity `Update`/`OnGUI` calls.

Important areas:

- `Config/PluginConfig.cs`: BepInEx config bindings.
- `Core/Services/TelemetryCoordinator.cs`: central runtime orchestration.
- `Core/Services/ClientTelemetrySampler.cs`: local player/client sampling.
- `Core/Services/ServerPulseBroadcaster.cs`: server pulse RPC capture/broadcast.
- `Core/Scoring/ScoreCalculator.cs`: derived score labels and recommendations.
- `Core/Services/HudRenderer.cs`: compact HUD rendering.
- `Core/Services/NetworkSensePanel.cs`: debug drawer UI.
- `Patches/PanelInputPatches.cs`: cursor release and input suppression while the
  panel is open.

### Data Flow

1. Unity frames call `TelemetryCoordinator.Update`.
2. The client sampler records frame timing and periodically captures a sample.
3. If running as host/server, the server pulse broadcaster writes and broadcasts
   server-side snapshots.
4. Scores are calculated from the latest client sample and server pulse.
5. HUD and panel render from a `NetworkSenseSnapshot`.
6. Logs are written as JSONL for later MCP/agent analysis.
7. Raven actions call local HTTP helper endpoints exposed by the MCP gateway.

### MCP Gateway

The gateway is separate from the mod and lives at:

```text
C:\work\comfy\network\mcp
```

It is intentionally a dev-only side channel. The mod uses simple localhost HTTP
helpers; agents can use the richer MCP tool surface.

Main docs:

- `network/mcp/README.md`
- `network/mcp/TOOLS.md`
- `network/mcp/AGENTS.md`
- `network/mcp/contracts/commands.json`

## Setup And Build

Prerequisites:

- Valheim installed with BepInEx.
- .NET SDK capable of building `net48`.
- Project root at `C:\work\comfy`.

Build from the mod project:

```powershell
cd C:\work\comfy\network\mod\ComfyNetworkSense
dotnet build .\ComfyNetworkSense.csproj -c Release
```

The project copies the built DLL to the default Valheim plugin folder when it
exists:

```text
C:\Program Files (x86)\Steam\steamapps\common\Valheim\BepInEx\plugins\ComfyNetworkSense.dll
```

If Valheim is installed elsewhere, pass `ValheimDir`:

```powershell
dotnet build .\ComfyNetworkSense.csproj -c Release -p:ValheimDir="D:\SteamLibrary\steamapps\common\Valheim"
```

Restart Valheim after each DLL rebuild.

## MCP Gateway Setup

Start the local gateway:

```powershell
C:\work\comfy\network\mcp\etc\start-comfy-gateway.cmd
```

Default endpoints:

```text
MCP:     http://127.0.0.1:8720/mcp
Health:  http://127.0.0.1:8720/healthz
Report:  http://127.0.0.1:8720/valheim/report
```

The in-game mod uses:

```text
X-Comfy-Key: valheim-mod-local
```

Manual health check:

```powershell
Invoke-WebRequest http://127.0.0.1:8720/healthz -Headers @{ "X-Comfy-Key" = "valheim-mod-local" }
```

## Testing

Build the plugin:

```powershell
cd C:\work\comfy\network\mod\ComfyNetworkSense
dotnet build .\ComfyNetworkSense.csproj -c Release
```

Run MCP tests:

```powershell
cd C:\work\comfy
$env:PYTHONPATH = "C:\work\comfy\network\mcp"
C:\work\commandcenter\fleet-worker-node\.venv-omen\Scripts\python.exe -m unittest discover -s network\mcp\tests
```

Expected MCP test result:

```text
Ran 4 tests
OK
```

In-game smoke test:

```text
network_sense_status
network_sense_debug
network_sense_hud
network_sense_panel signals
network_sense_benchmark
network_sense_raven
network_sense_mcp_status
```

## Using It In Game

Open the Valheim console and use:

```text
network_sense_debug
```

The panel has three tabs:

- `Debug`: live read, range/reception bars, session info, export folder.
- `Signals`: raw client/server signals, score values, benchmark start/stop.
- `Raven`: MCP/Raven recaps, config profiles, multiplayer checklist, markers,
  notes, recent events, and MCP health check.

Bottom controls are always visible:

- `Solo`, `Combat`, `Group`, `Town`: set the current context mode.
- `HUD ON/OFF`: toggle upper-left HUD.
- `Detail 1/3`: cycle HUD detail.
- `Export`: write a compact session export JSON.

Console commands:

```text
network_sense_hud
network_sense_detail
network_sense_mode
network_sense_mode solo|combat|group|town
network_sense_benchmark
network_sense_status
network_sense_panel debug|signals|raven
network_sense_debug
network_sense_raven
network_sense_export_session
network_sense_reload_config
network_sense_mcp_status
network_sense_mcp_note <text>
network_sense_mcp_mark <label>
```

Legacy mode aliases still work for convenience:

```text
auto -> solo
low, lowimpact, staging -> town
groupcombat, group-combat, group_combat -> group
```

Shortcuts are unbound by default to avoid collisions with ComfyControlSurface,
camera proof tools, and other local Valheim mods.

## Outputs And Files

Runtime logs are written under:

```text
BepInEx\config\comfy-network-sense\
```

Main files:

- `telemetry-client.jsonl`: client sample stream.
- `telemetry-server.jsonl`: server pulse stream.
- `event-timeline.jsonl`: commands, markers, notes, Raven responses, exports.
- `benchmark-results.jsonl`: completed benchmark summaries.
- `exports\network-sense-session-*.json`: compact session export bundles.

Config file:

```text
BepInEx\config\djcdevelopment.valheim.comfynetworksense.cfg
```

Useful HUD config keys:

- `hudPreset`: `Minimal`, `Compact`, or `Diagnostic`.
- `hudOpacity`: HUD background opacity.
- `hudMaxWidth`: wide HUD width before scale.
- `hudScale`: HUD scale factor.
- `hudMarginPixels`: upper-left HUD margin.

## Debugging

### Mod Does Not Load

- Restart Valheim after build.
- Confirm the DLL exists in `BepInEx\plugins`.
- Check `BepInEx\LogOutput.log` for `ComfyNetworkSense`.
- Rebuild from the project directory and confirm there are no compile errors.

### UI Does Not Show

- Run `network_sense_debug`.
- If the console command is unknown, the rebuilt DLL is not loaded.
- If the panel opens but input leaks to gameplay, inspect
  `Patches/PanelInputPatches.cs` and BepInEx logs for Harmony patch errors.

### HUD Is Too Large Or Too Noisy

- Use `network_sense_detail` to cycle detail.
- Set `hudPreset = Minimal` or `Compact`.
- Reduce `hudMaxWidth`, `hudScale`, or `hudOpacity`.
- Run `network_sense_reload_config` after config edits.

### Raven/MCP Is Offline

- Start `C:\work\comfy\network\mcp\etc\start-comfy-gateway.cmd`.
- Run `network_sense_mcp_status`.
- Check `http://127.0.0.1:8720/healthz`.
- Confirm the gateway is on port `8720`, not an older process on another port.
- Check `network/mcp/README.md` for auth and endpoint details.

### Multiplayer Test Checklist

1. Host starts world with the mod installed.
2. Client joins with the mod installed.
3. Both run `network_sense_debug`.
4. Host records marker: `Friend joined`.
5. Enter shared base/town and record marker: `Entered base`.
6. Run a benchmark from the `Signals` tab.
7. Export sessions on both machines.
8. Use MCP tools to compare host/client bundles.

## How To Extend

### Add A New In-Game Metric

1. Add the raw field to `ClientTelemetrySample` or `ServerPulseSnapshot`.
2. Capture it in `ClientTelemetrySampler` or `ServerPulseBroadcaster`.
3. Include it in `ToDictionary`/serialization.
4. Add derived scoring in `ScoreCalculator` if needed.
5. Expose it in HUD or panel only if it changes a player/test decision.

### Add A Panel Action

1. Add the action callback to `NetworkSensePanel.Draw`.
2. Wire it from `TelemetryCoordinator.DrawHud`.
3. Keep network/file work asynchronous if it can block.
4. Record an event with `WriteEvent` so MCP and agents can see it.

### Add A Raven/MCP Request

1. Add a gateway helper endpoint in `network/mcp/comfy_gateway`.
2. Add or update MCP tool docs/contracts.
3. Add a request kind in `TelemetryCoordinator.RequestEndpoint`.
4. Format the response into short bullets before rendering in-game.
5. Treat all model output as advisory text, never executable authority.

### Add A Config Profile

1. Add the profile to the gateway whitelist.
2. Make the profile small and reversible.
3. Add a button only after the profile has a concrete test purpose.
4. Require `network_sense_reload_config` or the panel reload button after apply.

### UI Rules

- HUD is the primary play surface.
- Panel is deliberate debugging, not always-on gameplay UI.
- Keep tabs low-scroll and action-specific.
- Prefer labels that map to real player contexts: solo, combat, group combat,
  town/base.
- Avoid adding raw numbers unless they support an immediate decision.

## Current Boundaries

This build does not implement automatic authority selection, queue
prioritization, gameplay policy enforcement, or production MCP connectivity.
It is a dev/test instrument for understanding conditions and shortening the
feedback loop while refining the Valheim network mod.
