# i5 deploy lane

Push-deploy from OMEN (`C:\work\baseline`) to the i5 laptop over the tailnet,
so an agent working in this repo can ship file updates (mod DLLs, configs,
test bundles) to the second Valheim test client without anyone at the keyboard
on either end.

## The lane

- **Transport:** ssh, via the `i5` alias in `~/.ssh/config` on OMEN.
- **Address:** `i5-laptop.tail8e749c.ts.net` (tailnet MagicDNS). Stable while
  the laptop roams; when both boxes are on the home LAN, Tailscale upgrades the
  tunnel to a direct LAN path automatically.
- **Auth:** OMEN's `~/.ssh/id_ed25519` is authorized for user `admin` on the
  i5. `admin` is an Administrator, and Windows OpenSSH gives admin ssh sessions
  a full (non-UAC-filtered) token — so deploys into
  `C:\Program Files (x86)\...` work without any elevation dance.
- **Doctrine:** ADR-0014 (commandcenter `fleet/inventory.toml`) keeps
  HEARTH/mechnet *machine control-loop* lanes off the tailnet, and reserves
  Tailscale for human/roaming access — naming the i5 as exactly that roamer.
  This lane is operator dev tooling to the sanctioned roaming node, not part of
  the control loop, so the tailnet is the correct transport for it.

Canonical `~/.ssh/config` block (already installed on OMEN, 2026-07-23):

```
Host i5
    HostName i5-laptop.tail8e749c.ts.net
    User admin
    IdentityFile ~/.ssh/id_ed25519
    IdentitiesOnly yes
    StrictHostKeyChecking accept-new
    ConnectTimeout 8
    ServerAliveInterval 15
    ServerAliveCountMax 4
```

## Remote layout (verified 2026-07-23)

| What | Value |
|---|---|
| Hostname / user | `DESKTOP-T685KEI`, `WORKGROUP\admin` (Administrator) |
| OS / PowerShell | Windows 10 22H2, PowerShell 5.1 |
| Staging root | `C:\deploy\baseline` (auto-created on first deploy) |
| Valheim plugins | `C:\Program Files (x86)\Steam\steamapps\common\Valheim\BepInEx\plugins` |

## Usage

```powershell
# Is the lane up? (tailnet presence, port, key auth, remote layout)
.\Test-I5Link.ps1

# Prove directory deploy excludes are actually honored on the remote
.\Test-DeployToI5Fixtures.ps1

# Stage files/dirs under C:\deploy\baseline on the i5
.\Deploy-ToI5.ps1 -Path .\bundle\ -Dest C:/deploy/baseline/run-042

# Ship the mod straight into the live BepInEx plugins dir
.\Deploy-ToI5.ps1 -Path ..\..\network\mod\ComfyNetworkSense\bin\Release\ComfyNetworkSense.dll -ValheimPlugins

# After one Test-I5Link preflight, prepare/run/report/export Quest Lab through the
# bounded request mailbox (never a console or keystroke lane)
.\Invoke-I5QuestLabBatch.ps1 prepare -Suite all-schools
.\Invoke-I5QuestLabBatch.ps1 run -Suite all-schools
.\Invoke-I5QuestLabBatch.ps1 report
.\Invoke-I5QuestLabBatch.ps1 export

# Raise one visual comparison in a single request and collect its identify/log receipt
.\Invoke-I5QuestLabBatch.ps1 gallery_compare -Profile marble-wide -CompareProfile marble-grand

# Render and validate the exact request without touching i5
.\Invoke-I5QuestLabBatch.ps1 gallery_compare -Profile marble-wide -CompareProfile marble-grand -DryRun

# Use the same fixed mailbox on local OMEN when it is the explicitly selected live lane
.\Invoke-I5QuestLabBatch.ps1 run -Suite creator-events -Lane omen

# Start/rebuild the i5 Companion with the Valheim directory mounted
.\Start-I5Companion.ps1

# Sync the current Companion source/runtime inputs, then start/rebuild it
.\Sync-I5Companion.ps1

# Keep the client-only i5 Companion lane on Explore while using OMEN's local-Lab Gateway
.\Sync-I5Companion.ps1 -GatewayUrl http://100.124.12.37:4000

# Recover Docker Desktop and recreate i5 Companion if Docker CLI/engine gets stuck
.\Repair-I5DockerDesktop.ps1

# Start concurrent OMEN+i5 transport captures for a two-client movement test
.\Start-TwoClientCapture.ps1 -DurationSeconds 30 -IntervalSeconds 1 -Label sprint-stutter

# Also collect both evidence bundle zips into a local folder
.\Start-TwoClientCapture.ps1 -DurationSeconds 30 -IntervalSeconds 1 -Label sprint-stutter -BundleDirectory .\captures\sprint-stutter

# Collect both bundles and fail closed unless both motion-phase summaries are readable
.\Start-TwoClientCapture.ps1 -DurationSeconds 30 -IntervalSeconds 1 -Label phase-baseline -CollectPhaseSummaries

# Fast operator readout without the full JSON body
.\Start-TwoClientCapture.ps1 -DurationSeconds 30 -IntervalSeconds 1 -Label sprint-stutter -SummaryOnly

# Compact console plus a saved JSON receipt
.\Start-TwoClientCapture.ps1 -DurationSeconds 30 -IntervalSeconds 1 -Label sprint-stutter -SummaryOnly -OutputJson .\captures\sprint-stutter\result.json

# Compact Wave 0 receipt plus the remaining real-client test list
.\Test-Wave0Readiness.ps1 -SummaryOnly -OutputJson .\captures\wave0-readiness.json

# Gateway motion relay seam, no clients required
..\wave0\Test-Wave0SyntheticMotion.ps1 -OutputJson .\captures\wave0-synthetic-motion.json

# Drive a bounded named movement pattern on both joined clients
.\Start-TwoClientMotionTest.ps1 -Pattern straight_north -DurationSeconds 10

# Set the Wave 0 apply/observe split without keyboard/KVM work
.\Set-TwoClientApplyRoles.ps1 -ApplyClient omen

# Run a bounded physical feel window; capture starts before role/motion commands
.\Start-TwoClientFeelWindow.ps1 -Pattern straight_north -MotionDurationSeconds 10 `
  -ApplyClient omen -RoleReversal -Label sprint-role-reversal -CollectBundles

# Same bounded run, plus receive/drain/bind/render phase summaries from both clients
.\Start-TwoClientFeelWindow.ps1 -Pattern straight_north -MotionDurationSeconds 10 `
  -ApplyClient omen -RoleReversal -Label cre-e06 -CollectPhaseSummaries

# See the plan without copying
.\Deploy-ToI5.ps1 -Path .\bundle\ -DryRun
```

## Companion persistence

When i5 is enrolled as a Docker-backed Companion client, its `admin` console session owns a
scheduled task named `LumberjacksDockerDesktop`. It starts Docker Desktop at logon, permits starts
on battery, does not stop when the laptop changes to battery power, and has no task execution time
limit. The Companion compose service uses `restart: unless-stopped`, so its loopback dashboard
returns after Docker is ready. Verify the recovery path without touching Valheim:

Always start the i5 Companion through `Start-I5Companion.ps1` or the equivalent compose command with
both compose files:

```powershell
docker compose -p lumberjacks-companion --env-file C:\deploy\baseline\i5-companion\tools\companion\.env `
  -f C:\deploy\baseline\i5-companion\tools\companion\docker-compose.yml `
  -f C:\deploy\baseline\i5-companion\tools\companion\docker-compose.valheim.yml up -d --build
```

Starting with only `docker-compose.yml` creates a read-only dashboard. It will show
`valheim.found=false`, `config_found=false`, and no enrollment hash because `/valheim` is not mounted.

```powershell
ssh -o BatchMode=yes i5 'schtasks /Query /TN LumberjacksDockerDesktop /FO LIST'
ssh -o BatchMode=yes i5 'powershell.exe -NoProfile -Command "Invoke-RestMethod http://127.0.0.1:8080/health"'
```

The task starts Docker Desktop only. It does not start Valheim or write the Valheim config.

`Start-I5Companion.ps1` requires the Docker Desktop Linux engine before it runs
`docker compose`. If Docker is installed but the engine pipe/server is unavailable,
the script tries the `LumberjacksDockerDesktop` scheduled task once, waits briefly,
then exits with a clear error. Do not work around that by launching only the base
compose file; that recreates the read-only dashboard with no `/valheim` mount.

If Docker Desktop is installed but the Linux engine is missing, Docker CLI calls
hang, the Companion container is stuck in `Created`/zombie state, or `/health`
works after sleep while `/api/v0/companion/status` blocks on the Valheim bind
mount, run:

```powershell
.\Repair-I5DockerDesktop.ps1
```

The repair script runs over SSH, restarts only Docker Desktop service/processes
when required, and recognizes a non-answering status route even when the Docker
API itself is healthy. It starts Docker through the interactive scheduled task
so the engine survives the SSH session, corrects that task's battery/time-limit
settings, waits for `docker info --format '{{.OSType}}'` to return `linux`, then
recreates the Companion with both compose files and verifies:

- `/api/v0/companion/status` is readable;
- `/api/v0/companion/wave0/packet` is readable;
- the status body does not expose raw enrollment ids;
- the Companion container has Docker `init` enabled.

Use `-NoCompanionStart` when only the Docker engine needs to be checked or
recovered. The command is bounded and emits a JSON receipt; if it fails, keep
the receipt and stop instead of retry-looping.

For Companion development, prefer `Sync-I5Companion.ps1`. It uses the verified deploy lane for the
minimal Docker build context, clears only the stale remote `src\Game.Companion` staging directory,
then calls `Start-I5Companion.ps1`. Use `-DryRun` to inspect the copy plan or `-NoStart` when only
staging files is desired. Profile and Gateway selection flow through to the launcher. In `Lab`, the
i5 launcher defaults to OMEN's canonical tailnet Gateway (`http://100.124.12.37:4000`); pass
`-GatewayUrl` explicitly when validating a different bounded Lab origin.

For two-client transport evidence, use `Start-TwoClientCapture.ps1`. Start both Valheim clients,
begin the capture, move both characters during the window, then compare the `omen` and `i5`
summaries. The output includes a top-level `comparison` verdict before the raw per-machine
summaries. A useful Lumberjacks motion run should show peer count above zero and advancing
`motion_received` counters. If peer count rises but motion remains zero, the comparison calls out
that visible movement is still native Valheim for that run. Each summary also records the observed
motion states and final WebSocket/UDP readiness; a missing or stale Valheim heartbeat is treated as
incomplete evidence rather than a healthy motion result.

The capture deliberately separates three truths: client-local motion readiness, Gateway relay
counters, and visual application. A `motion_ready_no_gateway_delta` verdict means the client
connected and reported an active motion lane, but no Gateway motion counter advanced; investigate
publish/recipient binding/relay evidence before calling the movement native or tuning smoothing.
The mod's motion tile also reports direct ZDO hits, ZDO-object hits, player-index hits, unresolved
lookups, and index rebuilds. Those counters identify the Valheim binding seam without requiring a
live movement course for every code change.

Before asking for a live course, run the read-only seam gates in this order:

1. Run `tools\wave0\Test-Wave0SyntheticMotion.ps1` from the repo root. It proves the Gateway
   motion relay seams that do not need Valheim clients: distinct-recipient fan-out, same-recipient
   suppression, unauthorized drop, malformed-frame drop, sequence stale drop, and source-ZDO binding.
2. Run `Test-Wave0Readiness.ps1`. It confirms P7, OMEN, retained heartbeat-age evidence,
   readable motion telemetry, and the optional i5 lane in one receipt. If i5 is offline,
   the script records `WAIT` and prints the exact return-test list instead of failing the
   whole preflight.
   The public package pointer may advance independently of the admitted mod identity.
   Readiness therefore requires each Companion's `installed.release` to match the public
   package release, `installed.mod_release` to match the Gateway release, and the package
   SHA-256 to match; it does not require the package and Gateway release labels to be equal.
3. Confirm Gateway, server mod, and both Companion package releases agree.
4. Confirm each client has an enrollment, access-key presence, partition/region, and active
   WebSocket/UDP readiness without printing secret values.
5. Run the bounded capture with both clients idle. This proves the telemetry surfaces and gives a
   baseline for server-heartbeat age/variation, peer count, relay counters, and Valheim binding counters.
6. Only if those gates pass, run one bounded APPLY course. The course is evidence collection, not
   a prerequisite for discovering whether the release is aligned.

If a gate fails, stop at that boundary and use its receipt; do not repeat the same join/movement
experiment until the failing seam has changed.

For network-condition analysis without starting Valheim, run Test-NetworkCondition.ps1. It reads
only the retained client-local JSONL on OMEN and i5 and reports distribution values plus the exact
Heartbeat-age provenance. The client field previously called `rtt_ms` is Valheim's
`ZNet.GetServerPing()` value: `ZRpc.GetTimeSinceLastPing()` in seconds, emitted as
`server_ping_age_ms`. It is not a measured round-trip time. Use this before changing
interpolation, packet cadence, or the tailnet path.
Pass `-BundleDirectory` to collect both machine-local evidence bundle zips onto OMEN for review.
Pass `-SummaryOnly` during rapid live testing when the compact verdict is enough.
Pass `-OutputJson` to save the full comparison and raw per-machine summaries while keeping the
console compact.

`Start-TwoClientMotionTest.ps1` delivers the same command through each machine's localhost
Companion. Allowed patterns are `straight_north`, `straight_east`, `stutter_north`, and `circle`,
with a one-to-60-second bound. The mod consumes the command on Unity's main thread and appends
`companion-motion-receipts.jsonl`; this is intentionally not a general console or keyboard-
injection bridge.

`Set-TwoClientApplyRoles.ps1` uses that same bounded Companion command lane to set exactly one
client's Lumberjacks motion apply switch. The mod applies `set_apply` on Unity's main thread and
records the same transport-control event as the HUD toggle. Use `-ApplyClient omen` for the first
Wave 0 pass and `-ApplyClient i5` for role reversal.

`Start-TwoClientFeelWindow.ps1` composes the readiness check, concurrent capture, apply/observe
role command, and named motion command into one bounded window. It starts capture before issuing
movement, never launches or closes Valheim, and can run the reverse APPLY role immediately after
the first window. This is the preferred human-light physical test: the two clients are joined once,
the agent drives the allow-listed movement surface, and the human records only smooth/rough/mixed,
whether the result followed APPLY, and the first felt correction. Use `-DryRun` to inspect the
sequence without querying either machine. Use `-SkipReadiness` only when a current readiness receipt
is already known to be valid. The detailed account-role and experiment strategy is in
`plans/m7-physical-feel-lane-strategy.md`.

`-CollectPhaseSummaries` is implemented by the shared `Start-TwoClientCapture.ps1`
path. It implies bundle collection, extracts each machine's raw `samples.jsonl`, and
runs the CRE-E06 analyzer. The formal Wave 0 live gate and the physical feel window
therefore retain the same OMEN/i5 summaries for receive spacing, drain/coalescing,
binding, LateUpdate work, target error, and source-agnostic interframe displacement.
The capture exits nonzero when either machine is missing the phase contract or a
summary cannot be produced.

### Connected-player evidence handoff

For the live gate, leave both clients fully loaded and ready to move before starting the capture.
During the capture window, connect both accounts if they are not already connected, wait for the
peer count to reach two, then run the same short movement pattern on both clients: straight sprint,
brief stutter steps, and a stop. The useful receipt should identify both player names, show peers
above zero, and preserve the motion state/readiness fields on both machines. A `native_motion_only`
verdict means the players were visible but Lumberjacks motion counters did not advance; it is valid
evidence, not a failed test. A `lumberjacks_motion_observed` verdict means the counters advanced and
the movement feel can be compared against the captured deltas. Any `incomplete_telemetry` result is
discarded until both Companion lanes report readable Gateway, Valheim, cutover, and motion surfaces.

Every deploy re-hashes every manifest file on both ends (SHA256) and exits 1 on
any mismatch — a green run *is* the receipt. Directories land as
`<Dest>/<dirname>/...`; top-level items with duplicate leaf names are rejected
before anything is copied. When `-ExcludeDirectoryName` is used, the deploy
copies exact manifest files rather than recursively copying the whole directory,
so excluded `bin`, `obj`, or other build-output folders do not land remotely.

`Invoke-I5QuestLabBatch.ps1` composes that verified config deploy with one fixed request
mailbox. Requests expire within 30 minutes and accept only prepare/run/reset/report/export
or Gallery v2 build/compare/identify/clear/rebuild, with allowlisted suites and profiles.
The plugin consumes each request once on Unity's main thread and writes a request receipt;
the helper retrieves it, any exported suite receipt, and a filtered Quest Lab log tail into
ignored `captures/questlab/<lane>/` (`i5` or `omen`). A timeout reports that the request was delivered but not
consumed and never reissues it. Run `Test-I5Link.ps1` once at the start of the live block;
do not repeat the preflight between these bounded requests.

### Quest Lab live block — one human pass

With the final DLL/config already SHA-verified and Valheim loaded into a private world:

```powershell
# Headless-in-game proof of all 34 source-shared evaluator events
.\Invoke-I5QuestLabBatch.ps1 run -Suite creator-events

# One setup request safely clears marked old builds, raises the compact default course,
# and installs eight ordinary quests. Targets and supplies are staged at point of use.
.\Invoke-I5QuestLabBatch.ps1 prepare -Suite all-schools
.\Invoke-I5QuestLabBatch.ps1 run -Suite all-schools
```

The human arrives at a ground welcome camp: take food from the picnic-table item stands if
desired, pick up the bronze axe, and strike the Birch; then take the ascent portal to the
canopy-clear deck. Pick up the bow and arrows on the player side of
Combat and kill the Greyling at its rune; pick up the hammer and wood at Building and place any
piece; put the coal waiting directly in front of Crafting's smelter into it; and write the hub
sign labelled `sign here`. Picking up any staged item witnesses Inventory, the portal witnesses
World, and weapon/tool use normally witnesses Progression; if that one remains, jump or use any
skill once. Every station is only 9 m from the hub. Then collect and close the run:

```powershell
.\Invoke-I5QuestLabBatch.ps1 report
.\Invoke-I5QuestLabBatch.ps1 export
.\Invoke-I5QuestLabBatch.ps1 reset
```

Gallery clear and rebuild now return a player standing on a selected raised floor to the
natural terrain at the same X/Z before removing marked objects. If Valheim does not accept or
complete that movement, deletion fails closed and the request receipt says why. These requests
also wait a bounded five seconds for queued marked ZDO destroys to disappear before claiming
success. `prepare all-schools` uses the same clear-all-then-build lifecycle, so it is the normal
one-command reset between human passes. The bounded operations give the operator identify →
safe clear → one-command comparison → selected
rebuild, without one-off console entry:

```powershell
.\Invoke-I5QuestLabBatch.ps1 gallery_identify
.\Invoke-I5QuestLabBatch.ps1 gallery_clear -Selector all
.\Invoke-I5QuestLabBatch.ps1 gallery_compare -Profile marble-wide -CompareProfile marble-grand
# after visual selection:
.\Invoke-I5QuestLabBatch.ps1 gallery_clear -Selector all
.\Invoke-I5QuestLabBatch.ps1 gallery_build # selected default: marble-grand
```

The visual choice is deliberately not inferred from a build receipt. Copy
`tools/component-packets/samples/questlab-gallery-acceptance.sample.json` into the ignored
capture directory, name the human and comparison request, choose the profile, and turn each
observation true only after looking in game. The r11 form explicitly records the floor, scale,
hall width, monument runes, mid-spoke rune banners, focused sign lighting, welcome camp, and
Quest grid readability. The final verifier rejects a missing school,
catalog event, coalescing witness, lifecycle operation, human decision, mixed-machine evidence,
or same-action double completion:

```powershell
python tools/component-packets/verify_questlab_release.py `
  --creator-events <creator-suite.json> `
  --all-schools <live-suite.json> `
  --gallery-request <build-request.json> `
  --gallery-request <compare-request.json> `
  --gallery-request <identify-request.json> `
  --gallery-request <clear-request.json> `
  --gallery-request <rebuild-request.json> `
  --gallery-acceptance <gallery-acceptance.json> `
  --expected-version 0.2.0 `
  --expected-release questlab-v0.2.0-20260808-r11 `
  --write captures/questlab/omen/release-verification.json
```

## Rules for agents

- **Offline is normal.** The i5 is a roaming laptop, `expect="optional"` in the
  fleet inventory. If `Test-I5Link` fails, report it and stop — never loop or
  retry more than once.
- **Never password-auth.** Everything here runs `-o BatchMode=yes`; if key auth
  breaks, that's a human's runbook (below), not a prompt to answer.
- **Deploy targets:** the staging root and (when asked) the plugins dir. Don't
  spray files elsewhere on the i5 without Derek asking for it.
- The `#< CLIXML` noise on stderr from remote powershell calls is cosmetic
  (PowerShell 5.1 remoting chatter), not an error.

## Re-trust runbook (only if the i5 is rebuilt or the key stops working)

On the i5, in an **admin** PowerShell (`admin` is an Administrator, so the key
belongs in the machine-wide admin file, not `~\.ssh\authorized_keys`):

```powershell
$key = 'PASTE CONTENTS OF OMEN ~\.ssh\id_ed25519.pub'
$f = 'C:\ProgramData\ssh\administrators_authorized_keys'
Add-Content -Path $f -Value $key -Encoding ascii
icacls $f /inheritance:r /grant 'Administrators:F' /grant 'SYSTEM:F'
Restart-Service sshd
```

Then from OMEN: `ssh -o BatchMode=yes i5 whoami` must print
`desktop-t685kei\admin` with no prompt.
