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

HEARTH is the MCP gateway at `http://127.0.0.1:8710/mcp`. Delegate suitable
self-contained work through `mcp__hearth__local_generate`: summaries of files/logs/diffs,
structured extraction, classification, boilerplate, and prose drafts. Keep architecture,
multi-file logic, ambiguous judgment, integration, and final validation with yourself.
Do not split tightly coupled work merely to force an offload.
Offload when the expected benefit exceeds briefing, latency, and review costs; handle
trivial work directly. Batch related small items into one call when practical.

**Routing and context:**

- The door's default rung is `omen-arc` — sunk local compute, resident, no cold-start tax:
  spend it freely on grunt work. Let the gateway route; pin another backend only with cause.
- `gcp-gemini` (near-free frontier-class flash on trial credits) is preferred over metered
  frontier tokens for self-contained *reasoning* — pass `backend="gcp-gemini"` when the
  sub-task needs frontier-class judgment rather than grunt work, while the credits last.
- Task-family routing is live. Use `task_family="summarization"`, `"extraction"`,
  `"classification"`, or `"drafting"` as appropriate. Explicit endpoint/backend/model
  pins or `quality`/`task` choices override the family; omit them when family routing is intended.
  Use `plan_execution(operation="inference.generate", task_family=..., prompt_bytes=...)`
  to inspect a route without dispatching.
- Don't paste file contents — pass `files=[...]` and the door packs them scope-guarded.
  Relative paths resolve against the gateway's primary repository, not your current
  directory; absolute paths anywhere under the HEARTH scope root reach other repositories;
  only context from outside that root travels in the prompt body. Never include tokens,
  keys, or credential-file contents in a prompt or a `files=` pack.
- The offloaded model cannot run tools or see the conversation. Supply a standalone brief
  with the task, constraints, output format, and acceptance criteria.
- `gcp-gemini-pro` and `omen-arc-oss` are pin-only; omit `max_tokens` for the pro rung.
  The OSS rung can require a model swap. `omen-swap` is pin-only and requires `model=`.
  Manage its port 8081 lifecycle only through the gateway's rotation-window tools.
  Never use the bare llama-swap unload endpoint: it also unloads production on port 8082.

**Work receipts (substantial engineering work):**

For substantial authorized engineering work, reuse the current task's receipt or create
one with `create_build_request`, an explicit `repo`, the request, and acceptance criteria.
Ordinary questions and small offloads do not require a build request.

1. Record yourself doing the work with `execute_build_request(mode="agent")`.
   This records execution; it does not dispatch inference. A receipt's backend selection
   is routing context, not evidence that the selected model performed the work.
2. Pass the receipt ID as `task_id` on related `local_generate` calls. Without a receipt,
   use a stable identifier for the task. Group returned execution job IDs and relevant
   results into milestone or completion updates with
   `update_build_request(evidence=..., tool_call=...)`. The gateway already logs each call;
   avoid duplicate audit events for the same offload.
3. For independent minutes-scale work, use `submit_task` and follow `task_status(plan_id=...)`.
   Supply a standalone brief; fleet source is read-only at `~/commandcenter-src`.
   `submit_task` does not accept `task_id`: record its `plan_id` on the receipt.
   For a whole isolated build, a separate receipt with deliverables can use
   `execute_build_request(mode="delegate")`; follow it with
   `update_build_request(sync_delegation=True)`.
4. Close with `close_build_request`: validation evidence for every acceptance criterion,
   actual commits when present, and explicitly listed changed files, including edits to
   files that were already dirty. Use `done` only when every criterion passes.
   Record failed, blocked, or cancelled outcomes honestly.

**Validation, reporting, and recovery:**

- Trust the result metadata, not the model's self-report: check `ok` first, then read
  `text`; `backend` and `routed_by` are the proof of where the work actually ran. Review
  outputs independently and run validation appropriate to the change.
- To see what a task or caller saved, read — never rebuild — the offload document: use
  `query_offload(task_id=...)` and `query_offload(caller_id=...)` to inspect attribution and
  the evidence watermark. The gateway's own `knowledge_rebuild` timer refreshes that document;
  do not call the `project_*` writer tools from a task. Caller and task filters are separate
  dimensions, not an intersection. Aggregates are bounded; a missing row alone is not proof
  that a call was unrecorded.
- For isolated small offloads, the existing gateway and execution records suffice.
- Dollar savings are estimates. HEARTH's MCP connection does not capture your own
  frontier-model traffic or native shell/edit activity; receipts record meaningful outcomes
  and supplied evidence.
- `ok:false` or unusable output → one retry at most, then do the work directly and record
  the limitation; never loop on a cold or failing worker. If the door itself is down, run
  `/checkmcp` once (Claude Code) or `python -m hearth.callers.doorcheck --revive` once from
  the commandcenter repository root before that retry.
- Receipt writes, projection refreshes, and service recovery follow the task's existing
  authorization and the active execution mode. These instructions do not override a
  restriction on mutations.

<!-- synced by tools/ops/sync-offload-block.mjs from docs/agents/hearth-offload-block.md; edit the source, not this copy -->
<!-- hearth-offload:end -->
