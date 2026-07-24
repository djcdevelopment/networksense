# The swarm harness (removed 2026-07-21)

## Scoped lab restoration (2026-07-24)

The full swarm harness remains removed. A small, separate `LabAutoJoinPatches`
implementation restores only existing-character selection for disposable,
profile-gated headless/rendered lab clients. It is opt-in through
`COMFY_AUTOJOIN=true` or the `[LabAutoJoin]` config section, defaults off, and is
not intended for physical OMEN/i5 player installs. It does not create profiles,
teleport, run the matrix runner, or execute arbitrary commands. The Compose client
profile is the only supported place to enable it.

This mod used to carry a harness for running **fleets of headless Valheim clients
unattended** — the "independent agent" regime, where an AI agent drove builds and tests for
hours with nobody watching. That regime ended; the operator now drives interactively with
two Steam accounts he owns. The harness was removed rather than left commented out, because
git is a better archive than a comment block: commented-out code rots silently, a commit SHA
does not.

**Everything below is recoverable in full.** The last commit containing it is **`1887626`**.

```powershell
# read a removed file at its last living revision
git show 1887626:network/mod/ComfyNetworkSense/Core/Services/MatrixCheckinRunner.cs
git show 1887626:network/mod/ComfyNetworkSense/Patches/AutoCharacterSelectPatches.cs

# or restore one into the working tree
git checkout 1887626 -- network/mod/ComfyNetworkSense/Patches/AutoCharacterSelectPatches.cs

# see the removal itself
git show <the commit after 1887626> -- network/mod/ComfyNetworkSense
```

## What was removed

| Piece | What it did |
|---|---|
| `Patches/AutoCharacterSelectPatches.cs` | Drove the FejdStartup character-select screen so a spawned container connected instead of idling at the menu with `rtt_ms = 0`. |
| `Core/Services/MatrixCheckinRunner.cs` | Polled a gateway for benchmark cells, teleported to each, ran a capture window, posted results back. |
| `ComfyNetworkSense.TryStartAutoRehearsal` | Ran the route rehearsal automatically once a local player existed. |
| `ComfyNetworkSense.TryCoupleAutoRehearsalToNetcodeProbe` | Fired that same walk when the netcode probe auto-started, so captured traffic existed "without a human hand-walking the route". |
| 19 config keys | `[AutoJoin]` (9), `[Automation] autoRehearsal*` (5) plus `coupleAutoRehearsalToNetcodeProbe`, `[Matrix]` (4). |

## What deliberately stayed

- **`network_sense_rehearsal`** — the manual console command, and all the route-walking
  machinery under it (`TryStartRehearsal`, `RunTeleportRoute`, route file parsing). Only the
  *automatic* wrappers went. This is an operator tool and it got more useful, not less.
- **`routeGodFlySafeguard`** — it guards that manual walk from killing the character on a
  post-teleport fall. It was grouped with the swarm keys in the audit; that was wrong.
- **The netcode probe itself**, including its auto-start keys. Those are a separate decision
  (D3 in `fieldlab/docs/config-surface-decisions.md`), not yet taken.

## Still orphaned, for a later pass

Removing the mod side left consumers elsewhere with nothing to talk to. None of these break
anything today — they simply have no client now:

- `fieldlab/autonomous/valheim-lab.compose.yml` and `valheim-lab.env.example` — the swarm
  container definitions, which set `COMFY_AUTOJOIN` and `COMFY_MATRIX_CHECKIN`.
- `fieldlab/autonomous/client-init/20-comfy-valheim-autostart.sh`.
- `network/mcp/comfy_gateway/toolsurface/matrix.py` (~16 KB) and the `valheim_matrix_*` MCP
  tools it serves. **Do not delete this casually**: it is a registered provider in
  `network/mcp/etc/start-comfy-gateway.cmd`, so removing it changes the comfy-gateway tool
  manifest and needs a gateway bounce to take effect cleanly.
- A stale `autoRehearsal` reference in `network/mcp/comfy_gateway/toolsurface/valheim.py`.

## Why you might want it back

The honest counter-argument, recorded so the decision can be revisited on its merits: this
was the only ready-made multi-client harness in the repo. If proving a Lumberjacks
optimisation under scale ever needs twenty simultaneous clients, the hostname-derived
character spreading and the matrix checkout/report loop are genuinely fiddly to rebuild.
That is a real capability that was dropped, not merely dead code that was swept up.
