# Repository boundary: NetworkSense

## Purpose

NetworkSense is the sovereign Valheim client-observability add-on. It packages
network telemetry, the in-game HUD, Owner Score, and the bounded tooling used to
build, package, and deploy that mod.

## Owns

- `network/mod/ComfyNetworkSense` and its 166-test suite.
- Client telemetry, HUD, Owner Score, transport truth, and mod-side policies.
- Generic NetworkSense deployment lanes for i5 and AM4.
- Modpack assembly and synthetic-baseline extraction tooling.
- NetworkSense build and release manifests.

## Does not own

- Transport services and contracts implementation (`lumberjacks-platform`).
- Quest Lab, Runtime, Contracts, Studio, or Quest deployment tools
  (`comfy-quest`).
- The development MCP gateway (`isolate`).
- Fleet knowledge, evidence archive, or repository index (`baseline`).

## Published artifacts

| Artifact | Format | Verification |
| --- | --- | --- |
| `ComfyNetworkSense.dll` | GitHub release asset on `mod-v*` | Release manifest and SHA-256 checksum |
| NetworkSense modpack | ZIP | Package SHA-256 and release identifier |

## Consumed artifacts

| Artifact | Producer | Pin |
| --- | --- | --- |
| `Comfy.Quest.Contracts` | `comfy-quest` | `0.1.0-local` during extraction; exact public version after publication |
| `Comfy.Transport.Contracts` | `lumberjacks-platform` | `0.1.0-local` during extraction; exact public version after publication |
| Companion/Gateway HTTP surfaces | `lumberjacks-platform` | Versioned API and identity response |
| Dev MCP HTTP surface | `isolate` | Loopback endpoint; no source checkout dependency |
| Motion-phase analyzer | `lumberjacks-platform` | Explicit artifact path plus required SHA-256 |

## Boundary guards

- G1: `tools/Assert-NoReachIn.ps1` rejects executable/config references to a
  host work root, baseline source URLs, or deep traversals into former monorepo
  roots. Its disabled bad fixture is exercised by `tools/Test-BoundaryGuards.ps1`.
- G2: `tools/Assert-RepoIdentity.ps1` verifies the Git origin before every
  state-changing host entrypoint.
- G5: clean-checkout and empty-package-cache builds consume only tracked local
  packages during the extraction phase; public exact pins replace them at the
  package-publication commitment point.
