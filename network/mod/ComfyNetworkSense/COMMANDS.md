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
- `network_sense_reload_config`: reload BepInEx config.
- `network_sense_export_session`: write a compact dev session export JSON.
- `network_sense_mcp_status`: check whether the local Comfy MCP gateway is reachable.
- `network_sense_mcp_note <text>`: record a timestamped dev note in the event log.
- `network_sense_mcp_mark <label>`: record a timestamped test marker in the event log.

Shortcuts are unbound by default to avoid collisions with other local Valheim mods.

MCP gateway docs live under `network/mcp/`.
