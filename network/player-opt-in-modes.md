# Player Opt-In Modes

## Why this matters

Not every player session is a raid, a crowded event, or a dense settlement defense.

A large amount of community play is quiet:

- farming
- decorating
- sorting storage
- tending animals
- scouting alone
- moving through low-density space

If the game is CPU-limited and large parts of its runtime are effectively single-thread bound, then one useful lever is to let solo or low-risk players opt into a mode that reduces the burden they place on the shared simulation.

This should not feel like a punishment.

It should feel like a clear trade:

- lower impact on the world and server
- lower local pressure in exchange for reduced fidelity or scope
- optional perks or recognition for helping the shared environment

## Core idea

Let players voluntarily say:

`I am doing low-intensity play right now. Optimize around that.`

This is a contract between player and server.

The player gives up some freshness, range, or simulation richness.

The server gains more room to preserve the critical path elsewhere.

## Proposed mode

### Low Impact

Designed for isolated or low-risk activity.

Good matches:

- solo farming
- base maintenance
- crafting
- inventory work
- solo gathering in a quiet region

Possible behaviors:

- lower frequency for distant updates
- reduced ambient or decorative replication
- smaller active relevance radius
- lower priority for noncritical sync
- delayed freshness for crowded remote areas

This should not affect core integrity:

- inventory correctness
- world mutation correctness
- combat safety if danger appears nearby

If actual danger starts, the mode should automatically step upward.

## Solo benchmark concept

One interesting idea is to let the client run a lightweight benchmark while the player is already in a safe, isolated activity state.

The purpose is not synthetic bragging rights.

It is to estimate how much local headroom the client really has under the current settings.

Potential inputs:

- frame stability over a short fixed window
- simulation frametime under local activity
- effect of current graphics or object settings
- behavior near the player's base versus in open terrain

This can help answer:

- is the player actually CPU-limited?
- are ultra settings materially harming local simulation quality?
- does this player have enough headroom to own a busy region?
- should the system recommend `Low Impact`, `Auto`, or `Combat-ready`?

## Important constraint

The benchmark should not pretend to measure everything.

A solo benchmark cannot fully predict:

- crowded combat behavior
- portal flood conditions
- mass structure synchronization
- multi-player ownership pressure

It is best used as one signal, not as the whole truth.

## Why opt-in matters

For community games, voluntary participation matters more than silent restriction.

If players feel the system is secretly downgrading them, they will resent it.

If the UI says:

`You are farming alone. Switch to Low Impact to reduce shared load and gain a small solo perk.`

that feels like an informed choice.

## Incentive design

If the mode helps the community, it can be rewarded modestly.

Possible incentives:

- slight stamina regeneration bonus
- slight movement bonus
- farming or gathering efficiency bonus
- repair or maintenance efficiency bonus
- reduced food decay while doing quiet work

The perk should be:

- useful
- thematic
- not mandatory
- not so strong that players exploit it during combat

That means the bonus should fall off or disable quickly when:

- enemies aggro nearby
- the player enters a crowded event
- the player begins combat actions
- the player joins an active authority negotiation zone

## Design principle

This is not just optimization.

It is behavior shaping.

The system is telling players:

- quiet play is different from high-pressure play
- the server benefits when you declare your intent
- helping the shared load can come with a fair reward

That is healthier than pretending all activities are technically equivalent.

## Relationship to authority selection

Low Impact should feed into ownership and authority logic.

A player in Low Impact mode is implicitly saying:

- do not prefer me as an owner for a busy combat region
- I am not asking for first-class latency right now
- optimize me for efficiency unless danger rises

That makes authority selection clearer.

The system can reserve ownership preference for players in `Auto` or `Combat` mode who have sufficient headroom and better current telemetry.

## UI requirements

The mode should be simple to understand.

Useful UI elements:

- current mode badge
- short description of what the mode changes
- whether the player is currently a poor owner candidate
- optional perk currently active
- warning when danger will disable the perk
- recommendation based on local conditions

Helpful example text:

`Low Impact active: smaller relevance radius, reduced distant sync, +5% stamina regen while out of danger.`

## Safety rules

- never let perks meaningfully distort combat balance
- disable or taper perks quickly on danger
- do not let Low Impact hide nearby threats
- avoid excessive manual toggling during ordinary play
- let the server override the mode when the local situation changes sharply

## Research value

This idea connects three useful concepts:

- performance policy
- player intent signaling
- incentive-backed community cooperation

It treats optimization as part of the social contract instead of a hidden technical tax.

## Open questions

- What is the smallest useful perk that feels meaningful without becoming exploit bait?
- What local measurements are stable enough to use for player recommendations?
- How aggressively should Low Impact reduce relevance radius or update freshness?
- When should the mode auto-disable versus merely warn the player?
- How should players understand the difference between `Low Impact`, `Auto`, and `Combat` in one glance?
