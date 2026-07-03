# Telemetry And Scores

## Why this note exists

Before building policy automation, the system needs a stable language for measurement.

That means two things:

- a telemetry contract
- a scoring model

The telemetry contract defines what the client and server are allowed to claim.

The scoring model defines how those claims are turned into recommendations, warnings, mode suggestions, and ownership decisions.

## Design goals

The telemetry and scoring layer should be:

- cheap enough to leave on during testing
- understandable by players and operators
- stable enough for comparison across sessions
- explicit about confidence and uncertainty
- useful before it becomes fully automatic

## First rule

Scores should advise before they control.

Early versions should use scores for:

- HUD display
- recommendations
- experiment analysis
- owner candidate previews

Only later should the scores directly drive hard decisions.

## Sampling model

Use two sampling shapes:

- `live samples`: frequent client-side measurements for HUD responsiveness
- `pulse samples`: slower summarized snapshots for server truth and cross-session analysis

Suggested cadence:

- `live sample`: every 250 ms to 500 ms
- `pulse sample`: every 2 s to 5 s
- `event marker`: emitted only on meaningful transitions

## Primary entities

The system should reason about four kinds of subjects:

- `player`
- `region`
- `session`
- `owner_election`

Each telemetry record should clearly identify which subject it belongs to.

## Core identifiers

Common fields for most records:

- `timestamp_utc`
- `session_id`
- `sample_id`
- `player_id`
- `region_id`
- `cluster_id`
- `mode`
- `owner_id`
- `sample_source`
- `build_version`

## Record types

### `client_sample`

Frequent player-side measurement.

Useful fields:

- `rtt_ms`
- `jitter_ms`
- `fps`
- `frame_time_ms`
- `frame_time_p95_ms`
- `cpu_bound_estimate`
- `bytes_in_per_sec`
- `bytes_out_per_sec`
- `packets_in_per_sec`
- `packets_out_per_sec`
- `time_since_last_authoritative_update_ms`
- `time_since_last_nearby_entity_update_ms`
- `correction_count_recent`
- `correction_magnitude_avg`
- `nearby_players`
- `nearby_entities`
- `nearby_build_pieces`
- `danger_nearby`

### `server_sample`

Periodic server-side truth for a player or region.

Useful fields:

- `player_rtt_ms`
- `player_jitter_ms`
- `region_observer_count`
- `region_player_count`
- `region_entity_count`
- `region_build_density`
- `region_pressure_score`
- `messages_sent_per_sec`
- `bytes_sent_per_sec`
- `messages_by_priority`
- `bytes_by_priority`
- `deferred_low_priority_count`
- `dropped_low_priority_count`
- `owner_candidate_count`
- `owner_confidence`
- `authority_stability_sec`

### `event_marker`

Sparse event records for timeline reconstruction.

Useful event names:

- `portal_enter`
- `portal_exit`
- `combat_start`
- `combat_end`
- `danger_enter`
- `danger_exit`
- `region_merge_start`
- `region_merge_commit`
- `owner_election_start`
- `owner_election_commit`
- `owner_change`
- `mode_change`
- `correction_spike`
- `benchmark_start`
- `benchmark_end`

### `owner_election`

Snapshot of candidate ranking during staging or automatic selection.

Useful fields:

- `candidate_player_id`
- `owner_score_total`
- `network_quality_score`
- `frame_stability_score`
- `cpu_headroom_score`
- `proximity_score`
- `current_load_penalty`
- `eligibility`
- `selection_reason`
- `confidence`

### `benchmark_result`

Short local benchmark or safe-state measurement summary.

Useful fields:

- `benchmark_type`
- `duration_ms`
- `avg_fps`
- `p95_frame_time_ms`
- `cpu_bound_estimate`
- `base_density_context`
- `recommended_headroom_tier`

## Normalization

Raw values are useful for logs, but scores require normalization.

Use a simple `0.0` to `1.0` range for early versions.

Guideline:

- `1.0` means strongly favorable
- `0.5` means acceptable or mixed
- `0.0` means poor or unusable

Keep the normalization functions simple and inspectable.

Examples:

- lower RTT maps higher
- lower jitter maps higher
- lower frame time variance maps higher
- lower current load maps higher
- closer distance to active region maps higher

## Core component scores

### 1. Network quality score

Measures connection suitability for timing-sensitive play.

Inputs:

- RTT
- jitter
- heartbeat gaps
- resend or stall indicators if measurable

Conceptual formula:

`network_quality = weighted(rtt_score, jitter_score, stall_score)`

Early weighting:

- RTT: `0.50`
- jitter: `0.35`
- stall stability: `0.15`

### 2. Frame stability score

Measures whether the player can present and process updates smoothly.

Inputs:

- average frame time
- p95 frame time
- frame time variance
- recent stalls

Conceptual formula:

`frame_stability = weighted(avg_frame_score, p95_score, variance_score)`

### 3. CPU headroom score

Measures whether the client appears to have spare local capacity.

Inputs:

- CPU-bound estimate
- benchmark tier if available
- heavy-area frame degradation

Conceptual formula:

`cpu_headroom = weighted(cpu_bound_score, benchmark_score, degradation_score)`

This score should be conservative.

### 4. Proximity score

Measures how appropriate the player is for owning a specific active region.

Inputs:

- distance to region center
- distance to current event hotspot
- movement direction toward or away from hotspot

Conceptual formula:

`proximity = weighted(distance_score, trajectory_score)`

### 5. Current load penalty

Measures whether the player is already burdened by density or pressure.

Inputs:

- nearby build density
- nearby entity count
- correction frequency
- local throughput pressure
- whether the player opted into `Low Impact`

Conceptual formula:

`current_load_penalty = weighted(density_penalty, correction_penalty, throughput_penalty, low_impact_penalty)`

This is a penalty, so higher means worse.

### 6. Region pressure score

Measures how stressed a region appears to be.

Inputs:

- player count
- observer count
- entity count
- build density
- message rate
- low-priority shedding

Conceptual formula:

`region_pressure = weighted(population, density, traffic, shedding)`

This is useful for warnings, staging prompts, and event placement guidance.

## Composite decision scores

### Owner score

Used for previews and later authority selection.

Conceptual formula:

`owner_score = network_quality + frame_stability + cpu_headroom + proximity - current_load_penalty`

Suggested early weights:

- network quality: `0.35`
- frame stability: `0.20`
- CPU headroom: `0.20`
- proximity: `0.20`
- current load penalty: `0.25`

The exact numbers will change. The important part is transparency.

### Low Impact recommendation score

Used to suggest whether a player should opt into lower-impact solo behavior.

Inputs:

- local danger state
- isolation level
- region pressure
- player role activity
- current owner eligibility

Conceptual logic:

- recommend `Low Impact` when danger is low, isolation is high, and the player is not a strong ownership candidate
- suppress the recommendation when combat, clustering, or event staging begins

### Combat readiness score

Used to indicate whether the player is a good candidate for latency-sensitive activity or ownership.

Inputs:

- network quality
- frame stability
- CPU headroom
- correction severity

Conceptual formula:

`combat_readiness = weighted(network_quality, frame_stability, cpu_headroom, inverse_correction_penalty)`

### Merge risk score

Used to warn when two or more groups are likely to create pressure soon.

Inputs:

- cluster distance
- cluster trajectories
- predicted combined observer count
- predicted combined density context
- current region pressure

Conceptual formula:

`merge_risk = weighted(trajectory_convergence, predicted_population, predicted_density, current_pressure)`

## Confidence

Every recommendation should carry confidence.

Confidence should rise when:

- enough recent samples exist
- client and server views broadly agree
- the signal has been stable over several pulses

Confidence should fall when:

- samples are sparse
- readings are contradictory
- the player is moving rapidly between contexts
- the region state just changed

Suggested confidence labels:

- `Low`
- `Medium`
- `High`

## Hysteresis and cooldowns

Scores should not cause rapid flapping.

Apply:

- mode recommendation cooldowns
- owner selection cooldowns
- minimum dwell times for displayed recommendations
- stability thresholds before state change

Examples:

- do not re-recommend `Low Impact` more than once every 30 seconds unless conditions change dramatically
- do not change owner candidates visibly on every tiny score swing

## Pulse presentation rules

The HUD should not show raw internal math by default.

Player-facing presentation should translate scores into plain language:

- `Connection: Stable`
- `Area Load: Rising`
- `Owner Fit: Strong`
- `Low Impact: Recommended`
- `Merge Risk: High`

The debug layer can still expose raw values and score breakdowns.

## Anti-goals

The scoring layer should not:

- pretend to be perfectly objective
- make hard promises based on weak telemetry
- punish players for weaker hardware
- hide tradeoffs behind fake precision
- overfit to one test session

## Cheap first implementation

The first mod build does not need perfect instrumentation.

Start with:

- RTT
- jitter
- FPS
- frame time
- nearby player count
- nearby entity count
- nearby build count if accessible
- correction frequency
- mode
- owner candidate display

Then add:

- bytes per second
- priority counts
- region pressure pulses
- benchmark summaries

## First validation questions

Use the first experiment loop to answer:

- Which metrics stay stable enough to compare across sessions?
- Which metrics correlate with player-reported bad feel?
- Which metrics best predict a good owner candidate?
- Does `Low Impact` recommendation logic line up with common sense?
- How often do client and server views disagree?

## Recommended file outputs

Keep the first implementation simple.

Suggested outputs:

- `telemetry-client.jsonl`
- `telemetry-server.jsonl`
- `event-timeline.jsonl`
- `owner-elections.jsonl`
- `benchmark-results.jsonl`

## Open questions

- What should count as a correction event if engine internals do not expose one directly?
- How should build density be estimated cheaply enough for live use?
- Which score weights are stable across different activities?
- How much telemetry is cheap enough to keep visible in ordinary play?
- Which recommendations should remain advisory permanently rather than becoming automatic?
