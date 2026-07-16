## Changelog

### 0.5.31

- Start permanent primary redirection as soon as the server peer is ready instead of waiting for
  the diagnostic netcode probe's 25-second delay.
- Carry the proven FieldLab priority tiers into every authoritative envelope, preserve Valheim's
  `ServerSortSendZDOS` order, and let Gateway deliver critical/portal/structural state first while
  keeping every ZDO in the durable acknowledged queue.
- Raise the end-to-end poll/ack batch from 256 to 1,024 and serialize batch apply/ack/poll phases,
  removing the 256-envelope-per-second ceiling and duplicate redelivery churn without increasing
  the Unity main-thread apply budget.

### 0.5.30

- Give the permanent primary window a server-session boundary: while the dedicated server is
  empty, reset the durable delivery window before sequence numbers can be reused after a restart.
- Refuse to arm all-prefab primary suppression until that empty-server reset succeeds, preserving
  native delivery as the fail-safe path when Gateway maintenance is unavailable.
- Treat an existing ZDO at equal-or-newer owner/data revisions as a native-funnel reconciliation
  even when an obsolete queued identity has a different prefab; older local revisions still retry
  and reject rather than acknowledging un-applied state.

### 0.5.29

- Merge authoritative consumer state into the normal client JSONL stream so the local MCP
  development channel can inspect the live poll/apply/ack path without dashboard inference.
- Report the consumer phase, peer readiness, queue depths, sequence progress, request activity,
  timestamps, and sanitized errors; enrollment secrets are never written to telemetry.
- Bound direct Gateway connect, send, receive, and response sizes so a dead route cannot strand
  the background poller or acknowledgement loop indefinitely.

### 0.5.28

- Separate the shared authoritative queue/window id from each player's Steam enrollment id.
- Send the enrollment credential on authoritative HTTP and Lumberjacks WebSocket connections so
  the mod can use a direct authenticated Gateway route instead of requiring OMEN's IAP tunnel.
- Retain `lumberjacksEnrollmentManifestId` as a backwards-compatible queue id fallback.

### 0.5.27

- Keep the all-prefab redirect armed after the finite diagnostic probe stops when the effective
  cutover mode is `lumberjacks-primary`; mirrored/bounded gates retain automatic rollback.

### 0.5.26

- Publish concrete all-prefab coverage totals from the authoritative redirect so Gateway can
  promote a fully posted and applied window to `lumberjacks-primary`.
- Keep the persistent cold-start plugin copy synchronized during GCP deployments.

### 0.5.25

- Raise authoritative Gateway polling from 64 to 256 envelopes per second while retaining the
  64-apply Unity frame budget and 256-sequence acknowledgement batches.

### 0.5.24

- Reconcile an obsolete envelope as `superseded` when `RPC_ZDOData` leaves a matching ZDO at a
  strictly newer owner or data revision. Acknowledge that sequence without rolling state backward,
  and report it separately from exact applies and terminal rejects.

### 0.5.23

- Treat `RPC_ZDOData` readback mismatches as transient retries until a bounded terminal threshold;
  only terminal failures increment the authoritative rejection counter.
- Retry locally on Unity's main thread with exponential backoff and a 64-apply frame budget, while
  suppressing expected Gateway redelivery for sequences already queued or waiting to retry.
- Add sequence, attempt, UID, owner, revision, and prefab diagnostics without per-frame log spam.

### 0.5.19

- Add `network_sense_godfly [on|off]` console command: on-demand god mode + debug-fly toggle for a
  client joined to a dedicated server, where the vanilla `god`/`fly` commands are cheat-gated
  (`IsCheatsEnabled() == ZNet.IsServer()`, false on a client) and `fly` is `onlyServer`. Drives the
  `Player` API directly — the same path the route-walk safeguard already uses. Client-only (no-op
  headless). Additive; no existing behaviour changes.

### 0.5.16

- P6/I5 handshake responder: stage the `handshakeResponder*` config surface (server-side am4
  hook, Steam-only per I6, disabled by default; empty endpoint refuses to arm). Config only —
  the RPC interceptor (Harmony on `RPC_ServerHandshake`/`RPC_PeerInfo`, `ZPackage` clone-decode,
  raw `TcpClient` context poll per the server Mono trap) is built and wire-validated at the armed
  session, where it meets a real client's PeerInfo bytes. Architecture + contract:
  `fieldlab/NETCODE-HANDSHAKE-CONTRACT.md`, worklog I5 design block.

### 0.5.15

- Replace the inbound-injection poll parser with a narrow gateway-shape parser so the client no
  longer depends on Unity `JsonUtility` DTO population under BepInEx/Mono. The command still goes
  through the same synthetic authority, prefab allowlist, revision, position, and vanilla
  `RPC_ZDOData` validation before any ZDO is applied.

### 0.5.14

- P5/I4 inbound injection runway: client-only, disabled by default, synthetic-authority and
  prefab-allowlist scoped. Polls bounded Lumberjacks commands off-thread, validates them on the
  Unity main thread, rebuilds vanilla `ZDOData` packages, applies through `RPC_ZDOData`, records
  apply/render/readback lifecycle to `injection-apply.jsonl`, and auto-stops.
- Expose the Unity JSON DTOs as public serializable top-level types with the gateway's exact
  snake_case field names. An in-game parser self-check now refuses at arm time on incompatibility.
- Add the bounded Lumberjacks queue/ack/status contract, malformed-input rejection, MCP gate
  reads, and the `run-injection-window.ps1` deploy/arm/stage/gate/disarm driver.

### 0.5.12

- **Fix: redirect POST delivery on the dedicated server.** `ZdoRedirectRunner.PostBatch` used `WebRequest.Create("http://…")`, which throws `NotSupportedException("The URI prefix is not recognized.")` in Valheim's stripped **server** Mono runtime (the WebRequest prefix table is empty there) — so window i3-w3 suppressed 88 tree ZDOs correctly (`ack_failures=0`) but posted **0** (all 88 dropped after 9 failed batches / 176 requeues). Replaced with a raw `TcpClient` HTTP/1.1 POST (`SendHttpPostViaSocket`) that bypasses the prefix table: runs on the poster's background thread, bounded 5s connect/send/receive, throws on non-2xx so the existing retry + `last_error` path is unchanged. Only the delivery call changed; suppression/ack/rollback semantics are identical. NB: the sibling `WebRequest.Create` call sites (priority mirror, telemetry, apply-profile) share the same latent defect but run **client-side**, where the prefix table is populated — left for a follow-up.

### 0.5.11

- P4/I3 outbound **REDIRECT** — the second behaviour-changing rung (server-side/am4 only, rollback-gated). New `ZdoRedirectRunner` + a `Priority.High` Harmony postfix on `ZDOMan.CreateSyncList` that removes allowlisted-prefab ZDOs from the freshly built `toSync` list *before* `SendZDOs` serializes it, and posts the wire-equivalent payload — `{seq, uid, owner, owner_rev, data_rev, prefab, pos, body_b64 = base64(zdo.Serialize bytes)}` in batched JSON — to the Lumberjacks gateway (`POST /valheim/zdo-redirect/receipts`). Every suppression is also logged locally to `redirect-send.jsonl` (the mod-side count of record for the gate: gateway distinct-seq == local seq, zero gaps).
- **Suppress-with-ack:** each suppressed ZDO gets the native per-peer bookkeeping replicated (`peer.m_zdos[uid] = PeerZDOInfo(DataRevision, OwnerRevision, now)` + `m_forceSend` removal, mirroring `ZDOMan:767/780`, via cached reflection — `ZDOPeer` is a private nested class) so vanilla re-offers a ZDO only when its revision actually changes; suppressed-count therefore equals exactly what native would have sent. If the reflection handles don't resolve, `Start()` **refuses** — fail-safe is vanilla behaviour.
- **Writes no persisted ZDO state** (decompile-verified: the send path touches only runtime peer bookkeeping; `ZDO.Serialize` only reads; the save is the separate `PrepareSave`/`SaveAsync` clone path) — inherits the pin's save-safety class.
- **Rollback rehearsal built into the window:** `zdoRedirectActiveSeconds` (default 90) < the probe window ⇒ suppression auto-disarms mid-capture and the still-running probe (whose `CreateSyncList` postfix runs *after* this one and thus sees the post-filter list) records native sends of the tagged prefab resuming — P4 step 11 with zero keystrokes.
- Gated by new `[Netcode]` flags: `zdoRedirectEnabled` (default `false`; rollback), `zdoRedirectPrefabs` (**required** allowlist — empty refuses to arm, deliberately opposite to the pin's "blank = any"), `zdoRedirectEndpoint` (Lumberjacks base URL, OMEN tailnet), `zdoRedirectWindowId` (blank = auto per run), `zdoRedirectActiveSeconds`. Coupled to the netcode-probe window; server-only guard; off = vanilla behaviour, a volatile read of idle cost.

### 0.5.10

- P3/I2 ownership-seizure **PIN** — the first *behaviour-changing* rung (server-side/am4 only, rollback-gated). New `OwnershipPinRunner` + Harmony patches that **skip** the vanilla ownership transfer on a small auto-captured set of ZDOs, so a pinned ZDO's owner does not transfer on zone entry. Enforced by two prefixes on `ZDO.SetOwner(long)` / `ZDO.SetOwnerInternal(long)` that return `false` (skip original) — **scoped** to exactly the two churn funnels via depth flags set around `ZDOMan.ReleaseNearbyZDOS` (release@640 + reseize@645) and `ZDOMan.RPC_ZDOData` (the remote-apply at :830/:844). Create/load/convert `SetOwner(Internal)` paths are never touched.
- Covers **both** caveat-#2 paths. Blocking `SetOwnerInternal` directly is strictly stronger than the "keep `OwnerRevision` highest" strategy, which has a hole at `RPC_ZDOData:842-844`: when the incoming *DataRevision* is newer the remote owner is applied **without** the `OwnerRevision` gate. This runner closes that hole (correction now recorded in `NETCODE-OWNERSHIP-MAP.md`).
- **Never invents ownership**: it only *skips* vanilla owner changes on captured ZDOs — the persisted owner is always a value vanilla itself set, so the save is a strict subset of vanilla writes (motivates the save-integrity hard gate but cannot fabricate state). Test-scoped by `ownershipPinAutoCaptureMax` (default 25) so the pin touches a negligible slice of the ~9M-ZDO world; everything uncaptured transfers normally — the built-in **negative control**.
- Gated by new `[Netcode]` flags: `ownershipPinEnabled` (default `false`; rollback), `ownershipPinAutoCaptureMax` (25), `ownershipPinPrefabs` (optional prefab allowlist, matched by stable hash). Coupled to the netcode-probe window (arms/disarms in lockstep with the probe + auto-rehearsal walk), started **after** the observe seam so a pin-blocked change reads as a no-op to the observer rather than a spurious transition. Off = vanilla behaviour, a volatile read of idle cost. Pin holds are logged to `ownership-pin.jsonl`; read via the new MCP tool `valheim_ownership_pin_status` (`valheim_tail_ownership_pin` for raw rows).

### 0.5.9

- P3/I2 ownership-seizure grounding, **observe only**: new server-side Harmony postfixes on `ZDO.SetOwner(long)` and `ZDO.SetOwnerInternal(long)` (`OwnershipObserveRunner` + `OwnershipObservePatches`) that log every ZDO ownership change — `{uid, old_owner, new_owner, owner_revision, data_revision, is_owner_now, sector, via, is_server}` — to `ownership-churn.jsonl`. The `via` tag separates the two funnels: `SetOwner` = the authoritative, revision-**bumping** path; `SetOwnerInternal` = the revision-**silent** remote-apply path (`RPC_ZDOData`, NETCODE-OWNERSHIP-MAP.md caveat #2 — a `SetOwner` guard alone would not cover it). Changes **no** gameplay: both hooks read state in postfix after Valheim applied it; no owners, revisions, or transforms are written. A prefix stashes the pre-change owner into `__state` since by postfix time `GetOwner()` is already the new owner.
- Gated by new `[Netcode] ownershipObserveEnabled` (default `false`; rollback flag). Coupled to the netcode-probe run window (starts/stops in lockstep) so one auto-rehearsal walk captures both, sharing the detail-row cap. Idle cost is a volatile read + null-check when the flag is off, mirroring the netcode probe.
- Purpose: measure the P3 ownership funnel against real churn *before* any behaviour-changing pin code, and empirically confirm the `ReleaseNearbyZDOS`→`SetOwner` funnel actually reaches a Harmony patch. Read through the new MCP tools `valheim_tail_ownership_churn` / `valheim_ownership_churn_summary`.

### 0.5.8

- Route/rehearsal walks now enable god mode + debug-fly on the local player before the first teleport, so a fall after any teleport can't kill the character mid-walk (a death would abort the route and force a human back into the loop). Gated by `[Automation] routeGodFlySafeguard` (default on); no-op headless.
- Implemented via the `Player.SetGodMode` / `Player.ToggleDebugFly` API directly rather than the `god`/`fly` console commands: those are cheat-gated (`Terminal.IsCheatsEnabled()` returns `ZNet.IsServer()`, false on a client joined to a dedicated server) and `fly` is `onlyServer`, so the console strings are rejected client-side. Direct calls also avoid `devcommands`' `RemoteCommand` side effect on the authoritative server during baseline capture.

### 0.5.7 *(backfilled — shipped in commit `c64ca1d` "Land P1 headless code" without a changelog entry or manifest bump)*

- Added `[Automation] coupleAutoRehearsalToNetcodeProbe`: when the netcode probe auto-starts on a client, also trigger the automatic route rehearsal so captured ZDO traffic exists without a human hand-walking the route (P1 step 7, probe-walk coupling). Plus the P1 netcode MCP tools, verifier max-age/source-host hardening, and the one-command probe-cycle orchestrator.

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
