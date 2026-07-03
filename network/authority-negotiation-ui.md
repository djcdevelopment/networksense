# Authority Negotiation UI

## Why this matters

A community game does not need to treat networking as a hidden machine concern.

If player behavior is one of the main causes of replication pressure, then the interface should help players coordinate around that pressure instead of forcing the server to absorb every bad clustering pattern blindly.

The goal is to let the UI inform both:

- the player, so they understand the likely cost of what they are about to do
- the server, so it can shift policy before the expensive activity begins

This is especially useful for games where:

- groups cluster for raids, events, or boss fights
- players often spread back out into low-intensity solo activity
- dense builds distort performance in specific locations
- ownership or authority behavior strongly affects perceived lag

## Core idea

Treat certain player actions as explicit requests for a different network policy.

Examples:

- "prepare for combat"
- "start event"
- "gather at gate"
- "return to low traffic"

These are not only UI states. They are negotiation signals.

The UI can gather intent, the server can evaluate the current network topology, and the group can choose a tradeoff before combat starts.

## Main principle

Do not hide authority decisions when they materially affect the quality of play.

If authority ownership changes which player sees the cleanest combat timing, then that choice should be visible and, in some cases, socially negotiable.

## Proposed modes

### Auto

The system handles ownership and replication policy automatically.

This should be the default for ordinary play.

### Low traffic

Used when players are farming, building, decorating, or otherwise doing low-risk activity.

Characteristics:

- lower update rates for noncritical state
- reduced distant freshness
- more aggressive relevancy pruning
- cosmetic or ambient data deprioritized

### Combat

Used when a group is intentionally preparing for latency-sensitive play.

Characteristics:

- higher priority for nearby actors
- more aggressive protection of combat-critical updates
- tighter authority placement around the combat region
- reduced tolerance for stale movement or hit validation

### Event staging

An optional pre-combat mode where the group gathers in a safe space and previews the likely authority outcomes before the event begins.

This is where the system becomes socially useful rather than merely technical.

## Event staging flow

### 1. Gather

Players stand in a safe staging location near the event entrance, arena, or portal.

### 2. Signal intent

Players use a UI action such as `Prepare for Combat`.

This tells the server:

- these players intend to act as a group
- latency quality now matters more than idle efficiency
- ownership and replication policy should be evaluated

### 3. Measure

The server evaluates candidate authority owners using current telemetry.

Useful inputs:

- RTT to server
- jitter
- packet loss or resend pressure
- recent frame stability
- client hardware tier if available
- proximity to the target region
- current replication burden
- whether the player is already loaded by a dense build

### 4. Present tradeoffs

The UI shows the group what changes depending on who owns the active region or instance.

Example presentation:

- `Alice owns`: best melee timing, support players get slightly slower remote updates
- `Ben owns`: more even update quality, weaker hit responsiveness
- `Cara owns`: safest overall stability, but highest average latency

### 5. Confirm

The group accepts the recommended owner or overrides it.

### 6. Lock temporarily

Ownership should remain stable for a minimum period or until a strong reason to re-elect appears.

Without this, the system will flap and create worse artifacts than a merely suboptimal owner.

## Ownership scoring

Ownership should not be decided by first arrival alone.

It should be scored.

A simple server-side scoring model could combine:

- `network_quality`
- `frame_stability`
- `cpu_headroom`
- `proximity_to_region`
- `current_load`

Conceptually:

`owner_score = network_quality + frame_stability + cpu_headroom + proximity - current_load`

The point is not mathematical precision.

The point is to make the decision intentional and observable.

## Dynamic proximity negotiation

A second use case appears when players are moving toward one another rather than staging first.

Example:

- two groups are traveling
- the server predicts they will soon enter the same active region
- authority expectations are about to matter

In that case the server can start a soft negotiation early.

Possible flow:

1. the server detects converging player clusters
2. it estimates the likely merge region
3. it computes authority candidates before collision
4. it warns players that ownership may shift
5. it prewarms the chosen policy before combat begins

This is better than waiting until the collision already produced lag.

## UI requirements

The interface should explain enough to support coordination without forcing players to learn networking jargon.

Useful signals:

- current mode: `Auto`, `Low Traffic`, `Combat`, `Staging`
- predicted owner
- confidence or stability indicator
- each nearby player's latency summary
- a short explanation of the tradeoff
- whether a re-election is likely soon

Bad signals:

- raw debug spam
- opaque ownership changes
- unexplained lag shifts
- hidden mode changes during critical moments

## Social design value

This kind of UI does more than optimize packets.

It gives groups a shared language for tradeoffs.

Examples:

- let the strongest close-range fighter own the event
- let the most stable machine own a dense base defense
- choose a more balanced owner when support timing matters more than one duelist's precision
- move the event if the current staging area is clearly overloaded

That turns networking from a private failure into a visible coordination choice.

## Safety rules

To keep the system practical:

- default to `Auto`
- use manual confirmation only for organized events
- add cooldowns to owner changes
- keep election local to the active region
- never allow frequent authority flapping
- treat user choice as a preference, not an unconditional override

The server must retain the right to reject a bad human choice if it is likely to destabilize the instance.

## Research value

This is useful because it joins three layers that are usually discussed separately:

- network policy
- runtime authority selection
- player-facing coordination

Most networking research stops at protocol behavior. Most UX work stops at status indicators. Most community design ignores the replication layer completely.

The interesting opportunity is to connect them.

## Open questions

- How much choice should the group really have versus the server auto-selecting the owner?
- How should the UI communicate tradeoffs without overwhelming ordinary players?
- What minimum telemetry is needed to make a good ownership recommendation?
- When should the system stage a negotiation and when should it switch silently?
- How should support roles, ranged roles, and melee roles weight latency tradeoffs differently?
