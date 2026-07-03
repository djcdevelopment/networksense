# Network Research

This directory is a research fork inside the repo.

It exists to hold the networking ideas, historical references, design notes, and performance principles that sit behind the game-facing work elsewhere in `comfy`.

The tone here is deliberately more shareable than the rest of the repo. These files are meant to be passed to other developers, friends, or collaborators who want to understand the networking thesis without first reading the full Comfy history.

## What belongs here

- research notes on multiplayer architecture
- historical lineage from older online games
- design principles for bandwidth, CPU, and replication
- transport and serialization experiments
- notes on congestion handling, prioritization, and graceful degradation
- references worth preserving for future writing or talks

## What this is not

This is not a polished engine spec yet.

It is also not limited to Valheim. Some of the pressure that motivated this work came from observing Valheim server behavior, but the goal here is broader: recover and restate the older discipline of building multiplayer systems around scarcity, prioritization, and explicit tradeoffs.

## Suggested reading order

1. `research-framing.md`
2. `authority-negotiation-ui.md`
3. `player-opt-in-modes.md`
4. `observability-and-experiments.md`
5. `telemetry-and-scores.md`
6. future architecture notes, experiments, or source logs added beside it

## Current scaffold

The first working Valheim plugin scaffold for this research lives at:

- `network/mod/ComfyNetworkSense/`

## Working thesis

Modern game networking often assumes surplus CPU, surplus bandwidth, and surplus client performance.

This research starts from the opposite assumption:

- constraints are normal
- congestion is guaranteed somewhere
- not all state deserves the same delivery cost
- systems should degrade by policy instead of by accident

That is the baseline for everything in this directory.
