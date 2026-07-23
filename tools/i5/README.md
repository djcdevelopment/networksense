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

# Stage files/dirs under C:\deploy\baseline on the i5
.\Deploy-ToI5.ps1 -Path .\bundle\ -Dest C:/deploy/baseline/run-042

# Ship the mod straight into the live BepInEx plugins dir
.\Deploy-ToI5.ps1 -Path ..\..\network\mod\ComfyNetworkSense\bin\Release\ComfyNetworkSense.dll -ValheimPlugins

# See the plan without copying
.\Deploy-ToI5.ps1 -Path .\bundle\ -DryRun
```

## Companion persistence

When i5 is enrolled as a Docker-backed Companion client, its `admin` console session owns a
scheduled task named `LumberjacksDockerDesktop`. It starts Docker Desktop at logon; the Companion
compose service uses `restart: unless-stopped`, so its loopback dashboard returns after Docker is
ready. Verify the recovery path without touching Valheim:

```powershell
ssh -o BatchMode=yes i5 'schtasks /Query /TN LumberjacksDockerDesktop /FO LIST'
ssh -o BatchMode=yes i5 'powershell.exe -NoProfile -Command "Invoke-RestMethod http://127.0.0.1:8080/health"'
```

The task starts Docker Desktop only. It does not start Valheim or write the Valheim config.

Every deploy re-hashes every file on both ends (SHA256) and exits 1 on any
mismatch — a green run *is* the receipt. Directories land as
`<Dest>/<dirname>/...`; top-level items with duplicate leaf names are rejected
before anything is copied.

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
