# NetworkSense repository working notes

## Repository identity and boundary

This repository is `djcdevelopment/networksense`. It owns the client telemetry
mod, HUD and Owner Score, plus the mod-focused i5, AM4, modpack, and synthetic
baseline tooling.

- Never reach into a sibling checkout. Cross-repository inputs arrive only as
  pinned packages or versioned release artifacts with hashes.
- Scripts derive repository paths from `$PSScriptRoot`; they do not assume a
  host checkout root.
- Every state-changing host entrypoint must run
  `tools/Assert-RepoIdentity.ps1` before it acts.
- `tools/i5/Install-I5ProcDump.ps1` is a portable remote payload. Its identity
  is established by the guarded deploy lane before it is copied to the i5; it
  intentionally runs outside a Git checkout.
- The i5 is a roaming laptop. Offline is normal: report it and stop, never
  retry-loop or fall back to password authentication.

## Landing work — one ask, not a relay race

“Go”, “push”, “land it”, “ship it”, or “merge it in” authorizes the remaining
commit and direct push to `main` in one pass. This is an R&D trunk; use a feature
branch or pull request only when explicitly asked.

Stop only for force-push, history rewrite, deleting work you did not create, or
work outside this repository. If `main` moves, pull with `git pull --ff-only`
and retry. Never bypass hooks or push protection.

## Local verification

```powershell
dotnet build network/mod/ComfyNetworkSense/ComfyNetworkSense.csproj -c Release -p:ComfyDependencyProfile=interim
dotnet test network/mod/ComfyNetworkSense.Tests/ComfyNetworkSense.Tests.csproj -c Release -p:ComfyDependencyProfile=interim
powershell -NoProfile -File tools/Test-BoundaryGuards.ps1
```

The mod build requires a local Valheim+BepInEx installation and must not set
`ComfyCopyToPlugins`; build output is not deployed implicitly. The test suite
must report exactly 166 passed tests.

`public` is the publication-ready default dependency profile and carries exact
`[0.1.0]` pins. The public packages do not exist yet, so use the explicitly
selected `interim` profile above; it is the only profile allowed to read
`packages-local`.

The current release candidate is `mod-v0.5.80-split-proof`. Prepare and verify it locally with
`network/tools/New-ModReleaseCut.ps1`; the runbook is
`network/tools/README.md`. Do not tag, publish a GitHub release, or publish
NuGet packages until the operator explicitly starts that publication step.

## Evidence standard

Use `VERIFIED` only for claims backed by a reproducible command and retained
output. Use `INFERRED` for reasoned conclusions and `BLOCKED` when an external
dependency prevents verification.
