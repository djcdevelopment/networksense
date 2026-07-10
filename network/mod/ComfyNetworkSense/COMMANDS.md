# ComfyNetworkSense Commands

In-game Valheim console commands:

- `network_sense_status`: show current state.
- `network_sense_panel`: toggle the NetworkSense debug drawer.
- `network_sense_panel debug|signals|raven`: toggle a specific panel tab. `ward` is kept as an alias for `debug`.
- `network_sense_debug`: open the debug drawer.
- `network_sense_raven`: open the Raven drawer.
- `network_sense_hud`: toggle HUD.
- `network_sense_detail`: cycle HUD detail.
- `network_sense_mode`: cycle mode.
- `network_sense_mode solo|combat|group|town`: set mode.
- `network_sense_benchmark`: start or cancel benchmark.
- `network_sense_tp x z [label]`: teleport the local player to a baseline coordinate and record a marker.
- `network_sense_tp x y z [label]`: teleport with an explicit Y coordinate and record a marker.
- `network_sense_route_run [teleport-route.tsv]`: run a tab- or comma-delimited teleport route from `BepInEx/config/comfy-network-sense`.
- `network_sense_rehearsal [teleport-route.tsv] [profile]`: reload config, check MCP, mark the rehearsal, run the route, and export the session.
- `network_sense_lumberjacks_probe [ws-url] [region-id] [input-count]`: prove the live Valheim plugin can speak the Lumberjacks Gateway side-channel protocol.
- `network_sense_lumberjacks_projection [start|stop|status] [ws-url] [region-id]`: render local-only proxy markers from Lumberjacks world rows.
- `network_sense_lumberjacks_shadow [start|stop|status] [ws-url] [region-id] [input-hz]`: compare Lumberjacks authoritative self updates against Valheim local-player movement without applying corrections.
- `network_sense_lumberjacks_shadow_route [teleport-route.tsv] [movement_only|stationary|axis_north|axis_east|axis_south|axis_west] [ws-url] [region-id] [input-hz]`: run the teleport route and collect per-stop Lumberjacks shadow drift rows. Use `20` for `input-hz` to match Lumberjacks' simulation tick.
- `network_sense_lumberjacks_priority_probe [start|stop|status] [radius] [scan-interval] [max-objects]`: classify loaded local Valheim objects into a Lumberjacks-ready priority manifest and write `priority-load.jsonl`.
- `network_sense_lumberjacks_priority_route [teleport-route.tsv] [radius] [scan-interval] [max-objects]`: teleport through the route and collect per-stop priority/load-order manifest rows.
- `network_sense_lumberjacks_priority_mirror [start|stop|status] [eventlog-url]`: live-mirror priority manifest rows to Lumberjacks EventLog as sample events plus per-sample object batches.
- `network_sense_lumberjacks_priority_route_mirror [teleport-route.tsv] [radius] [scan-interval] [max-objects] [eventlog-url]`: run the priority route and live-mirror the same per-stop batches to Lumberjacks EventLog.
- `network_sense_lumberjacks_netcode_probe [start|stop|status] [max-detail-rows]`: rung I1 observe-only probe of the live ZDO send/receive funnels (`RPC_ZDOData` / `SendZDOs` / `CreateSyncList`). Writes one row per observed ZDO (uid, owner, revisions, position, dir) to `netcode-probe.jsonl`. Changes nothing. The funnels only fire with a connected peer (not in singleplayer). For headless/lab runs, the `[Netcode]` config keys (`netcodeProbeAutoStartEnabled`, `netcodeProbeAutoStartDelaySeconds`, `netcodeProbeAutoStopSeconds`, `netcodeProbeMaxDetailRows`) auto-start it once a peer connects — no console needed.
- `network_sense_lumberjacks_priority_manifest_listen [start|stop|status] [ws-url] [region-id]`: listen for the reliable Lumberjacks `priority_manifest` broadcast (UDP datagram lane + WS control). *(since 0.5.3; doc backfilled 2026-07-09)*
- `network_sense_perf_status`: show the current perf-capture state. *(since 0.4.2; doc backfilled 2026-07-09)*
- `network_sense_perf_mark <label>`: record a ComfyNetworkSense perf marker. *(since 0.4.2; doc backfilled 2026-07-09)*
- `network_sense_reload_config`: reload BepInEx config.
- `network_sense_export_session`: write a compact dev session export JSON.
- `network_sense_mcp_status`: check whether the local Comfy MCP gateway is reachable.
- `network_sense_mcp_note <text>`: record a timestamped dev note in the event log.
- `network_sense_mcp_mark <label>`: record a timestamped test marker in the event log.

Automation config keys under `[Automation]`:

- `autoRehearsalEnabled`: when `true`, run a private-lab route rehearsal after the local player is available.
- `autoRehearsalRouteFile`: route file under `BepInEx/config/comfy-network-sense`, default `teleport-route.tsv`.
- `autoRehearsalProfile`: resource profile label recorded in markers.
- `autoRehearsalDelaySeconds`: delay after player availability before the route starts.
- `autoRehearsalRunOncePerSession`: prevents repeated runs in the same client session.

Portal fix config keys under `[PortalFix]`:

- `portalConnectionCacheEnabled`: when `true`, a host/server replaces Valheim's portal connection scan with a cached tag lookup.
- `portalConnectionCacheIntervalSeconds`: seconds between cached portal passes, default `5`.
- `portalConnectionCacheLogIntervalSeconds`: seconds between portal-cache summary log rows, default `60`.

Spawner fix config keys under `[SpawnerFix]`:

- `spawnerConnectionCacheEnabled`: when `true`, replaces Valheim's spawner connection pass with a cached hash lookup.

Priority probe config keys under `[Lumberjacks]`:

- `lumberjacksPriorityProbeRadiusMeters`: scan radius, default `96`.
- `lumberjacksPriorityProbeIntervalSeconds`: seconds between scans, default `5`.
- `lumberjacksPriorityProbeMaxObjectsPerSample`: maximum per-object rows per sample, default `96`; summary rows still include full scanned counts.

Shortcuts are unbound by default to avoid collisions with other local Valheim mods.

MCP gateway docs live under `network/mcp/`.
