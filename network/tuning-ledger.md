# Netcode Tuning Ledger

Every manual netcode tuning change gets a structured, citable entry here: knob →
hypothesis → before/after evidence → verdict. This turns folklore weights into
chains of evidence and feeds the M3 replay harness. Entries are **append-only**;
a `verdict` may be edited once, when its evidence lands.

> **Rule:** a netcode tuning change without a ledger entry doesn't ship (see
> [weekly-rhythm.md](../docs/weekly-rhythm.md)).

## Knob inventory

All runtime knobs are BepInEx `ConfigEntry`s in
[`PluginConfig.cs`](mod/ComfyNetworkSense/Config/PluginConfig.cs) (config file
`BepInEx/config/djcdevelopment.valheim.comfynetworksense.cfg`). Defaults below
are the in-repo values; grep the `file:line` to confirm.

### Area-of-interest / radii (meters)
| Knob | Section | Location | Default |
|---|---|---|---|
| `nearbyRadiusMeters` | Sampling | PluginConfig.cs:252 | 64.0 |
| `buildScanRadiusMeters` | Sampling | PluginConfig.cs:259 | 64.0 |
| `zdoInnerRadiusMeters` | Netcode | PluginConfig.cs:739 | 30.0 |
| `zdoOuterRadiusMeters` | Netcode | PluginConfig.cs:749 | 64.0 |
| `lumberjacksPriorityProbeRadiusMeters` | Lumberjacks | PluginConfig.cs:574 | 96.0 |

### Rate / cadence / send budget
| Knob | Section | Location | Default |
|---|---|---|---|
| `liveSampleIntervalSeconds` | Sampling | PluginConfig.cs:224 | 0.5 |
| `serverPulseIntervalSeconds` | Sampling | PluginConfig.cs:231 | 3.0 |
| `zdoThinHz` (mid-band emit rate) | Netcode | PluginConfig.cs:759 | 5.0 |
| `zdoSendCadenceOverrideIntervalSeconds` | Netcode | PluginConfig.cs:788 | 0.05 |
| `lumberjacksMotionSendHz` | Lumberjacks | PluginConfig.cs:469 | 20.0 |
| `lumberjacksTelemetryHeartbeatIntervalSeconds` | Lumberjacks | PluginConfig.cs:434 | 5.0 |
| `lumberjacksPriorityProbeIntervalSeconds` | Lumberjacks | PluginConfig.cs:581 | 5.0 |

### Priority / shedding
| Knob | Section | Location | Default |
|---|---|---|---|
| `zdoRedirectMaxPriorityRank` | Netcode | PluginConfig.cs:668 | 6 |
| `zdoRedirectActiveSeconds` (0 = no auto-disarm) | Netcode | PluginConfig.cs:654 | 0.0 |
| `zdoRedirectPrefabs` (empty refuses to arm) | Netcode | PluginConfig.cs:626 | "" |
| `zdoLandmarkReach` | Netcode | PluginConfig.cs:679 | "" |
| `lumberjacksPriorityProbeMaxObjectsPerSample` | Lumberjacks | PluginConfig.cs:588 | 96 |

### Band-shaping / policy switches
| Knob | Section | Location | Default |
|---|---|---|---|
| `zdoBandShapingEnabled` | Netcode | PluginConfig.cs:728 | false |
| `zdoPlayerFastLaneEnabled` | Netcode | PluginConfig.cs:769 | true |
| `zdoSendCadenceOverrideEnabled` | Netcode | PluginConfig.cs:778 | false |
| `zdoCoPresenceShadowEnabled` (ADR 0013 Phase 0) | Netcode | PluginConfig.cs:796 | false |
| `zdoCoPresenceFanoutEnabled` (ADR 0013 Phase 2) | Netcode | PluginConfig.cs:809 | false |

### Scoring weights — NOT tunable at runtime (finding)
The owner / priority / pressure scoring weights are **hardcoded constants** in
[`ScoreCalculator.cs`](mod/ComfyNetworkSense/Core/Scoring/ScoreCalculator.cs),
not config. Changing a weight is a **code change + mod rebuild**, not a config
edit — so weight tuning is a `pre-ledger` code commit tracked in git, not a row
here. Current weights:

| Composite | Weights | Location |
|---|---|---|
| Network quality | rtt .50 / jitter .35 / heartbeat-gap .15 | ScoreCalculator.cs:11-15 |
| Frame stability | frame .35 / p95 .45 / frame-jitter .20 | ScoreCalculator.cs:20-24 |
| Owner score | network .35 / frame .20 / cpu .20 / proximity .20 / −load .25 | ScoreCalculator.cs:47-51 |
| Combat readiness | network .40 / frame .30 / cpu .20 / corrections .10 | ScoreCalculator.cs:55-58 |
| Region pressure | players .35 / entities .25 / pieces .40 | ScoreCalculator.cs:93-95 |

### Ownership cooldown / hysteresis — no knob (finding)
There is **no config knob** for ownership cooldown or hysteresis dwell. Authority
stability is *measured* (`AuthorityStabilitySec`, computed in
`ServerPulseBroadcaster.cs`), not tuned. The one cooldown value —
`QuestTriggerEvaluator` `cooldownSeconds` (60s) — is a constructor argument, not a
`ConfigEntry`, so it isn't player-tunable. If hysteresis becomes tunable, add the
knob here.

## Entry template

Add a new row per change; edit a `verdict` once, when its evidence lands.

```
id        | e.g. TL-001
date      | UTC yyyy-mm-dd
knob      | the config key (or ScoreCalculator constant)
change    | old → new
hypothesis| what you expect to move, and why (prefix `retro:` if reconstructed)
before-log| recorded session before the change (path, or `pre-ledger`)
after-log | recorded session after the change (path, or `pending`)
verdict   | confirmed / refuted / inconclusive
notes     | anything else
```

> **Evidence gap.** The mod writes session logs to
> `BepInEx/config/comfy-network-sense/*.jsonl` (outside the repo); there is no
> committed session-log corpus yet. Until one lands (M3-1), `before-log` /
> `after-log` cite a `fieldlab/evidence/` bundle where one exists, else
> `pre-ledger`.

## Backfilled entries (from git history)

Reconstructed from `git log` on the config paths; hypotheses are `retro:`
(inferred from the commit and the knob's own description), not contemporaneous.

| id | date | knob | change | hypothesis | evidence | verdict |
|---|---|---|---|---|---|---|
| TL-b1 | pre-ledger | `zdoBandShapingEnabled` + inner=30 / outer=64 / thin=5Hz | off → banded AoI (near-full / mid-thin / far-drop) | retro: the full-rate redirect band is the whole cost; area ∝ r², so a ~30m inner band drops most objects a 50m band carried | commit `ecb2116`; `fieldlab/evidence/aoi-density-pressure-matrix-20260704/` | inconclusive (ships OFF) |
| TL-b2 | pre-ledger | `zdoPlayerFastLaneEnabled`=true | player ZDOs bypass distance thinning | retro: protect remote avatar motion from the static-world AoI policy | commit `0fff162` | pre-ledger |
| TL-b3 | pre-ledger | `zdoCoPresenceShadowEnabled` (Phase 0) | add zero-delivery co-presence shadow measurement | retro: measure the fan-out a correct model would produce before changing delivery (ADR 0013) | commit `c8e6478` | pre-ledger |
| TL-b4 | pre-ledger | `zdoCoPresenceFanoutEnabled` (Phase 2) | single-recipient → in-band fan-out delivery | retro: co-located players must see the same shared-area ZDOs (ADR 0013) | commit `c8db19d` | pre-ledger |
| TL-b5 | pre-ledger | `zdoLandmarkReach` | landmark reach as a granted distance property | retro: some objects (landmarks) must be visible beyond the band | commit `1dd6c18` | pre-ledger |

These are `pre-ledger`: they predate this ledger and have no committed before/after
session pair. They establish the template and the provenance chain — new changes
get a real `TL-NNN` id, a contemporaneous hypothesis, and cited evidence.
