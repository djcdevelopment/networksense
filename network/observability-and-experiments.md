# Observability And Experiments

## Why this note exists

The early version of this system should not start with aggressive automation.

It should start with visibility.

Two things matter immediately:

- players gain awareness and agency
- the server gains stable, comparable telemetry instead of guesswork

That means the first build should function almost like a live networking HUD and field recorder.

Not because the HUD is the product, but because it helps everyone build intuition about what the engine is doing between client, server, region, and ownership decisions.

## Early build goal

The first useful version should answer:

- what is the server seeing right now?
- what is the player experiencing right now?
- how different are those two pictures?
- what changes when players spread out, cluster, build, portal, or enter combat?
- what signals are stable enough to drive policy later?

Before trying to optimize aggressively, we want to learn which signals are actually predictive.

## Core design principle

Show both:

- `server view`
- `player live view`

and make the difference between them visible.

This is one of the biggest opportunities in the whole concept.

Most players only see "it felt laggy."

Most server operators only see "someone complained."

A shared HUD can expose the space in between:

- ping
- jitter
- update freshness
- authority placement
- distance bands
- observer count
- packet class pressure
- priority decisions
- correction events

## Telemetry categories

### 1. Transport and timing

Collect:

- client RTT to server
- rolling RTT average
- RTT variance or jitter
- packet send rate
- packet receive rate
- bytes sent per second
- bytes received per second
- recent stall windows
- heartbeat gaps

Why:

This is the base layer. Without timing quality, every higher-level interpretation is suspect.

### 2. Update freshness

Collect:

- time since last authoritative movement update
- time since last nearby entity update
- time since last remote structure refresh
- correction frequency
- average correction magnitude
- input-to-ack delay if measurable

Why:

Players often describe freshness problems before they describe transport problems.

This is where "rubber-band", "teleporting", "invisible but still hit me", and "late updates" become measurable.

### 3. Area-of-interest and distance

Collect:

- current region or cell
- nearby entity count
- nearby build-piece count if accessible
- current relevance radius
- counts by distance band
- counts by priority band
- observer count for the player's active area

Why:

This reveals when pressure is coming from population density, decorative density, or simple spatial crowding.

### 4. Ownership and authority

Collect:

- current owner or authority candidate for the active region
- owner confidence score
- time since last owner change
- ownership change reason
- local candidate ranking if available
- whether player is eligible, preferred, or excluded as owner

Why:

The authority story has to be visible if players are going to understand or trust it.

### 5. Priority and queue pressure

Collect:

- messages by priority class per second
- dropped or deferred low-priority updates
- queue depth by class if available
- send backlog duration
- largest recent burst
- current replication mode: `Low Impact`, `Auto`, `Combat`, `Staging`

Why:

This is where the scarcity model becomes concrete instead of philosophical.

### 6. Local performance

Collect:

- FPS
- average frame time
- frame time variance
- CPU frame budget estimate if measurable
- optional benchmark score
- whether the player appears CPU-bound

Why:

Since the game is CPU-limited and effectively single-thread sensitive, player-side headroom matters for ownership suitability and for good recommendations.

### 7. Event markers

Collect:

- portal use
- entering or leaving build-dense zones
- enemy aggro start
- combat start
- mode toggle
- owner election start
- owner election commit
- major correction spike

Why:

Raw time series are much more useful when matched with event markers.

## HUD design goals

The HUD should serve three audiences at once:

- ordinary players
- volunteer testers
- the server operator or mod developer

That means it needs layers.

## HUD layers

### Layer 1: player-readable status

Minimal and always understandable.

Show:

- current mode
- connection quality
- update freshness state
- current authority status
- whether the system thinks pressure is rising

Example:

`Mode: Auto | Ping: 54 ms stable | Area Load: Medium | Owner: Alice | Freshness: Good`

### Layer 2: diagnostic summary

For players who want more detail.

Show:

- RTT and jitter
- nearby entity and build counts
- owner confidence
- relevance radius
- current priority shedding
- recent correction count

### Layer 3: deep debug overlay

For local testing and iteration.

Show:

- per-class message rates
- bytes per second by class
- correction deltas
- event timeline markers
- owner score breakdown
- pulse versus live server comparisons

This layer can be noisy. That is acceptable if it is clearly separate from the normal player view.

## Pulse versus live

This distinction is important.

Not every signal needs to update continuously on screen.

Use both:

- `live`: rapidly changing values like ping, jitter, correction count, or current owner
- `pulse`: slower snapshots from the server every few seconds summarizing what it thinks is happening

Examples of pulse data:

- server view of active observers
- server view of area pressure
- server view of candidate owner scores
- server view of dropped or deferred low-priority traffic
- recent average correction severity

This helps expose one of the most valuable comparisons:

`here is what the server thought`

versus

`here is what you felt in real time`

## Suggested first data schema

The early version does not need a heavy database.

A simple append-only event log plus periodic snapshots is enough.

Useful record types:

- `client_sample`
- `server_sample`
- `event_marker`
- `owner_election`
- `mode_change`
- `correction_event`
- `benchmark_result`

Suggested common fields:

- `timestamp_utc`
- `session_id`
- `player_id`
- `region_id`
- `mode`
- `owner_id`
- `sample_source`

Then attach source-specific metrics to each record.

## What to log locally

For fast iteration, every client should be able to write a local session log.

Useful local artifacts:

- compact JSONL telemetry log
- event timeline file
- optional CSV export for quick spreadsheet analysis
- screenshot or overlay capture marker support

This lets you review a session without requiring full central infrastructure.

## What the server should log

The server should write the same session ID so client and server records can be lined up later.

Useful server-side outputs:

- periodic region pressure snapshots
- owner ranking snapshots
- mode transitions
- queue pressure or message shedding counts
- player join, leave, cluster, and merge events

## First experiments

You said you will have at least one volunteer and want fast local iteration.

That means the first experiments should be small, repeatable, and intentionally staged.

### Experiment 1: baseline solo field work

Goal:

Understand what "quiet play" looks like without extra automation.

Procedure:

1. player farms or builds alone in an open area
2. capture local FPS, frame time variance, RTT, jitter, nearby counts, and update freshness
3. repeat near a dense base
4. compare

Questions:

- how much does decorative density affect client and server pressure?
- which signals shift even before the player feels lag?

### Experiment 2: low impact mode trial

Goal:

Measure the cost and feel of `Low Impact`.

Procedure:

1. repeat the solo activity with `Auto`
2. repeat with `Low Impact`
3. compare update frequency, freshness, local FPS, and subjective feel

Questions:

- what can be reduced without harming trust?
- does the player actually notice the trade?
- does a small perk materially improve willingness to opt in?

### Experiment 3: convergence test with one volunteer

Goal:

Watch what happens when two players move from separate areas into a shared hotspot.

Procedure:

1. start far apart
2. record telemetry independently
3. move toward the same area
4. pause at a staging point
5. then enter combat or dense build space

Questions:

- which metrics rise first?
- can the system predict a bad merge before symptoms appear?
- how early should a negotiation warning appear?

### Experiment 4: pre-combat staging

Goal:

Test whether a visible `Prepare for Combat` flow is understandable and useful.

Procedure:

1. gather in a safe staging area
2. show owner candidates and tradeoffs
3. choose owner
4. enter combat
5. review whether the prediction matched the experience

Questions:

- do players understand the tradeoff language?
- does the chosen owner produce noticeably different feel?
- which telemetry best predicts a good choice?

### Experiment 5: owner swap sensitivity

Goal:

Learn how disruptive authority change is.

Procedure:

1. force controlled owner swaps during a nonlethal scenario
2. vary swap timing and cooldown
3. measure correction spikes and subjective disruption

Questions:

- how often can ownership change before trust breaks?
- what cooldown is necessary to avoid flapping?

### Experiment 6: benchmark usefulness

Goal:

Test whether a local solo benchmark predicts anything valuable.

Procedure:

1. run benchmark in calm conditions
2. classify client into rough headroom tiers
3. compare later against combat-area or dense-base behavior

Questions:

- does the benchmark correlate with ownership suitability?
- which benchmark metrics are noise?

## Subjective feedback matters

Do not only collect numbers.

For each experiment, collect short player notes:

- did it feel worse, better, or different?
- did the HUD help explain what happened?
- did the player trust the recommendation?
- did the player feel in control?

This is important because the concept succeeds partly through player awareness, not just raw throughput.

## Early success criteria

The first phase is successful if:

- players can see and understand network state changes
- the server and client logs can be aligned cleanly
- clustering and density patterns become visibly measurable
- `Low Impact` feels legible and acceptable
- owner selection signals look stable enough to reason about

The first phase is not yet about proving the final policy is correct.

It is about building an instrument panel that makes the engine teachable.

## Recommended implementation order

1. local HUD with client-side live stats
2. server pulse snapshots sent to client
3. local JSONL session logs on both sides
4. event markers for combat, portal, mode, cluster, and owner changes
5. simple owner score display
6. simple `Low Impact` toggle and visible mode badge
7. controlled experiments with one volunteer
8. only then start policy automation

## Open questions

- Which three signals are the most useful to show by default without overwhelming players?
- What is the best visual language for freshness and correction severity?
- How often should pulse data update before it becomes distracting?
- What telemetry can be collected cheaply enough to leave on all the time?
- Which metrics are stable enough to support automatic recommendations later?
