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

<!-- hearth-offload:begin -->
## Local-first offload (HEARTH)

HEARTH is an always-on MCP door on loopback at `http://127.0.0.1:8710/mcp`. Before spending
metered frontier tokens on a self-contained sub-task, delegate it with
`mcp__hearth__local_generate`. Keep frontier reasoning for architecture, multi-file logic,
judgment, and anything needing whole-repo or whole-conversation context.

**Reach for `local_generate` — don't reason inline — when the sub-task is:**
- summarizing / condensing a file, log, or diff you have already read
- extracting structured data (fields, lists, JSON) from unstructured text
- generating boilerplate (config, test scaffold, docstring, commit-message draft)
- classifying / labeling / yes-no triage over a chunk of text
- drafting prose you will then edit (retro notes, PR-body first pass)

**The door routes itself — pin a rung only with cause** (`backend="name"`), preferred order:
- `gcp-gemini` — near-free frontier-class flash; the default target for self-contained work.
- `gcp-gemini-pro` — **pin-only**; the large-context reach flash cannot carry. Omit `max_tokens`
  and let the rung apply its own default.
- `omen-arc` — **the door default** and the sunk-cost local rung: resident, no cold-start tax,
  so spend freely on grunt work. Consumers keep using port 8082 unchanged.
- `omen-arc-oss` — banked fire, **pin-only**; it costs a model swap, so pin it with cause.
- `omen-swap` — the rotation rung, **pin-only** and always with `model=`. Port 8081 owns the
  model lifecycle: load and unload only through the door's rotation-window tools, **never** the
  bare llama-swap unload endpoint, which unloads production too.

**Rules:**
- Don't paste file contents — pass `files=[...]` and the door packs them scope-guarded.
  Repo-relative paths resolve against the primary root, and absolute paths anywhere under the
  HEARTH scope root reach other repos; only context from outside that root travels in the body.
- The offloaded model cannot run tools and cannot see your conversation — briefs stand alone.
- Trust the result metadata, not the model's self-report: check `ok` first, then read `text`;
  `backend` and `routed_by` are the proof of where the work actually ran.
- `ok:false` or unusable output → one retry at most, then do the task yourself; never loop on a
  cold worker. If the door itself is down, run `/checkmcp` once.
- For async, minutes-scale work (research briefs, simple builds) use `submit_task` (returns a
  `plan_id`; poll `task_status`). The brief must be self-contained.
- Never paste secrets — tokens, keys, credential file contents — into a prompt or `files=` pack.
- A `task_family=` label is expected after C-05 lands; until then, do not pass it.

<!-- synced by tools/ops/sync-offload-block.mjs from docs/agents/hearth-offload-block.md; edit the source, not this copy -->
<!-- hearth-offload:end -->
