## Changelog

### 0.4.0

- Added a matrix check-in poller for swarm clients: when enabled it continuously checks out benchmark cells from a gateway (`POST /valheim/matrix/checkout`), teleports the local player to each cell, measures load-time, runs a NetworkSense benchmark for the cell's `benchmark_seconds` honoring its `event_profile`, and reports the result (`POST /valheim/matrix/report`). Backs off `matrixPollIntervalSeconds` on idle/done/errors and loops.
- `movement_only` cells walk the player in a small pattern during the benchmark; heavier profiles (`build_social`, `combat_build`, `event_surge`) are recorded and run stationary for now (stubbed with a TODO).
- Off by default; enabled per-client via the `COMFY_MATRIX_CHECKIN` environment variable (or the `Matrix` config section). Gateway URL is configurable via `matrixGatewayUrl` / `COMFY_GATEWAY_URL` for container deployments; client id defaults to the machine hostname.

### 0.3.0

- Server-side telemetry is now self-sufficient: the dedicated server writes an always-on `telemetry-server.jsonl` heartbeat row every pulse interval regardless of connected clients — at-rest baseline with 0 peers, aggregate bytes/sec + messages/sec + connected players + world ZDO count under load. Previously server pulses were only written per connected peer, so a client-less server produced no file.
- The client sampler no longer runs on a dedicated server (no local player), so `telemetry-client.jsonl` on the server is no longer filled with meaningless solo/no-player rows.

### 0.2.0

- Added AutoJoin: drives the FejdStartup character-select screen, picks a character, and connects, so lab/swarm clients collect connected-multiplayer telemetry instead of sitting at the menu with rtt/jitter/bytes = 0.
- Character is chosen by name, env/config index, or a hostname-derived index so a swarm sharing one config spreads across distinct characters; creates a character if none exist.
- Off by default; enabled per-client via the `COMFY_AUTOJOIN` environment variable (or the `AutoJoin` config section).

### 0.1.0

- Initial telemetry scaffold.
- Added client HUD, server pulses, JSONL logging, mode cycling, and benchmark capture.
