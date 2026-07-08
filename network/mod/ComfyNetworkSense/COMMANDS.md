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
- `network_sense_lumberjacks_shadow [start|stop|status] [ws-url] [region-id]`: compare Lumberjacks authoritative self updates against Valheim local-player movement without applying corrections.
- `network_sense_lumberjacks_shadow_route [teleport-route.tsv] [movement_only|stationary] [ws-url] [region-id]`: run the teleport route and collect per-stop Lumberjacks shadow drift rows.
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

Shortcuts are unbound by default to avoid collisions with other local Valheim mods.

MCP gateway docs live under `network/mcp/`.
