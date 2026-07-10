## Changelog

### 0.5.6

- Added a config-driven auto-start for the I1 netcode probe under a new `[Netcode]` section (`netcodeProbeAutoStartEnabled`, `netcodeProbeAutoStartDelaySeconds`, `netcodeProbeAutoStopSeconds`, `netcodeProbeMaxDetailRows`).
- Auto-start fires once `ZNet.GetPeerConnections() > 0` — it does **not** wait for a local player, so it runs headless on the dedicated server as well as on clients. Needed because the ZDO send/receive funnels only fire with a connected peer (singleplayer never invokes `SendZDOs`/`RPC_ZDOData`), and containerized lab clients have no console to type the command.
- Auto-stops after the configured window, writing the lifecycle counters row. Wired into `run-autonomous-valheim-lab.ps1` (server + client configs enable it; a post-run step verifies whichever produced `netcode-probe.jsonl`).

### 0.5.5

- Added `network_sense_lumberjacks_netcode_probe [start|stop|status] [max-detail-rows]`, the rung I1 (interception reachability) observe-only probe.
- Installs three auto-applied Harmony postfixes on `ZDOMan`: `RPC_ZDOData` (receive funnel), `SendZDOs` (the residual-inlining-risk send helper), and `CreateSyncList` (send-side fallback seam + per-ZDO send detail).
- Writes one line per observed ZDO (uid, owner, ownerRevision, dataRevision, position, dir) plus lifecycle counters to `netcode-probe.jsonl`. The receive side re-parses a copy of the wire bytes so the live packet's read cursor is never touched; the send side only reads ZDO objects Valheim already selected. No ZDOs, owners, revisions, or transforms are modified.
- Paired verifier `fieldlab/scripts/verify-netcode-probe.ps1` applies the I1 gate (nonzero sends AND receives with legible uid/owner) and discriminates whether `SendZDOs` was reachable directly or via the `CreateSyncList` fallback (inlining evidence).

### 0.5.4 *(backfilled 2026-07-09 — shipped in commit `adddebf` without a changelog entry)*

- Fixed UDP datagram loss in the priority-manifest listener (drained the socket per tick instead of one datagram per tick, so bursts no longer drop rows).

### 0.5.3 *(backfilled 2026-07-09 — shipped in commit `6c44983` without a changelog entry; 0.5.2 was never released)*

- Added `network_sense_lumberjacks_priority_manifest_listen [start|stop|status] [ws-url] [region-id]`, the real in-game consumer for the reliable Lumberjacks `priority_manifest` broadcast (UDP datagram lane + WS control).

### 0.5.1

- Added `network_sense_lumberjacks_priority_mirror [start|stop|status] [eventlog-url]`, which live-mirrors priority manifest rows to Lumberjacks EventLog.
- Added `network_sense_lumberjacks_priority_route_mirror [teleport-route.tsv] [radius] [scan-interval] [max-objects] [eventlog-url]`, which wraps the priority route with live per-stop EventLog mirror batches.
- Live mirror posts UUID-backed EventLog events and keeps Valheim replication untouched.

### 0.5.0

- Added `network_sense_lumberjacks_priority_probe [start|stop|status] [radius] [scan-interval] [max-objects]`, a local priority/load-order manifest probe for loaded Valheim objects.
- Added `network_sense_lumberjacks_priority_route [teleport-route.tsv] [radius] [scan-interval] [max-objects]`, which reuses the Era16 teleport route and writes per-stop priority rows to `priority-load.jsonl`.
- Added `[Lumberjacks]` config defaults for priority probe radius, scan interval, and max per-object rows per sample.
- The priority probe is observation-only: it classifies loaded local pieces into player-critical, portal, structural, interactive, storage/crafting, support, and far decorative tiers without writing ZDOs or replacing vanilla replication.

### 0.4.9

- Added optional shadow input-rate overrides to `network_sense_lumberjacks_shadow` and `network_sense_lumberjacks_shadow_route`, so route runs can match Lumberjacks' 20 Hz simulation tick with a final `20` argument.
- Added `axis_north`, `axis_east`, `axis_south`, and `axis_west` shadow-route movement profiles for direction/scale diagnostics.
- Expanded `lumberjacks-shadow.jsonl` rows with input heading, speed percent, input echo lag, Valheim delta/total vectors, authority delta/scaled vectors, and authority-to-Valheim distance ratio.
- Changed the fresh-config default `lumberjacksShadowInputHz` from 10 Hz to 20 Hz.

### 0.4.8

- Hardened `network_sense_lumberjacks_shadow_route` across Valheim teleport/load transitions by waiting for a stable local player and cleaning up the shadow sidecar on route abort.
- Added `[PortalFix] portalConnectionCacheEnabled`, a host/server-only cached portal connection loop based on the local ComfyMods `BetterServerPortals` approach, to avoid full-scan portal matching stalls on massive saves.
- Added `[SpawnerFix] spawnerConnectionCacheEnabled`, a cached spawner connection pass based on the local ComfyMods `Atlas` O(n) approach for large spawned-ZDO worlds.

### 0.4.7

- Added `network_sense_lumberjacks_shadow_route [teleport-route.tsv] [movement_only|stationary] [ws-url] [region-id]`, which reuses the existing teleport route file and records one Lumberjacks shadow authority window per route stop.
- Route shadow rows are tagged with `run_label`, `route_stop_id`, and `route_phase` so FieldLab packets can build per-density drift summaries.

### 0.4.6

- Added `network_sense_lumberjacks_shadow [start|stop|status] [ws-url] [region-id]`, a shadow movement authority probe.
- The shadow probe samples local Valheim player movement, converts observed motion into Lumberjacks `player_input`, receives authoritative self `entity_update` rows, and logs drift metrics to `lumberjacks-shadow.jsonl`.
- The probe is measurement-only: it does not apply Valheim transform corrections, does not add `ZNetView`, and does not write ZDOs.

### 0.4.5

- Added `network_sense_lumberjacks_projection [start|stop|status] [ws-url] [region-id]`, a local-only visual projection spike for the Lumberjacks Gateway bridge.
- Projection keeps a JSON WebSocket open, joins a Lumberjacks region, optionally sends lightweight `player_input`, parses `world_snapshot` / `entity_update`, and renders proxy markers as plain Unity primitives anchored in front of the local Valheim player.
- Projection markers deliberately do not use `ZNetView`, do not write ZDOs, and are cleared on stop/shutdown. Status rows are written to `lumberjacks-projection.jsonl`.

### 0.4.4

- Added `network_sense_lumberjacks_probe [ws-url] [region-id] [input-count]`, a narrow feasibility probe that connects from the live Valheim plugin process to the Lumberjacks Gateway, joins a region, sends JSON `player_input` messages, and writes `lumberjacks-bridge-probes.jsonl`.
- Added `[Lumberjacks]` config defaults for the Gateway URL, probe region, and input count. This proves side-channel protocol reachability only; it does not replace Valheim ZDO replication, physics authority, or Steam/PlayFab sockets.

### 0.4.3

- Disabled recurring `ZDOMan.NrOfObjects()` polling by default on server heartbeat rows. On Era16-scale saves this showed up as heartbeat gaps and likely recurring main-thread stalls.
- Added `serverHeartbeatWorldZdoCountEnabled` / `serverHeartbeatWorldZdoCountIntervalSeconds` so world ZDO counts are explicit opt-in and cached when needed.
- Disabled severe-hitch world ZDO counting by default via `worldZdoCountOnSevereHitchEnabled` so the perf probe does not amplify the hitch it is measuring.

### 0.4.2

- Added a low-overhead perf probe for the Era16 hitch investigation. It writes `perf-hitches.jsonl`, `perf-sections.jsonl`, `perf-engine-log.jsonl`, and manual `perf-markers.jsonl` rows under `BepInEx/config/comfy-network-sense`.
- Added configurable hitch thresholds, section warning thresholds, engine log sampling, and a scene-scan isolation switch in the BepInEx config.
- Tagged teleport route and matrix check-in phases so hitch rows identify whether the client was resolving ground, teleporting, settling, benchmarking, waiting, exporting, or reporting.
- Added telemetry-writer counters and `network_sense_perf_status` / `network_sense_perf_mark <label>` console commands for live run sanity checks.

### 0.4.1

- Moved JSONL appends off the Unity main thread so telemetry writes cannot stall dense-world load or benchmark frames.
- Replaced per-sample global `FindObjectsByType` entity/build scans with throttled local physics-radius scans, reducing main-thread pressure during teleport rehearsals and connected server pulses.
- Avoided per-frame snapshot allocation while a teleport route waits for benchmark completion.

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
