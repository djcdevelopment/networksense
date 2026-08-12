# i5 deploy lane

This directory owns the bounded NetworkSense lane from the current checkout to
the roaming i5 Valheim client. It uses the operator-owned `i5` SSH alias,
`BatchMode=yes`, short connection timeouts, and SHA-256 verification on both
ends. Offline is normal: report it and stop without retrying.

## Boundary

The default remote staging root is `C:\deploy\networksense`. Live-plugin and
live-config switches target only the i5 Valheim BepInEx directories. Platform
services are contacted through their published HTTP surface; Quest batching is
owned by `comfy-quest` and is not present here.

Every host-side state-changing entrypoint verifies that its Git origin is
`djcdevelopment/networksense` before acting. `Install-I5ProcDump.ps1` is the one
portable payload: run it only after the guarded deploy lane has copied it to
the remote staging root.

## Usage

```powershell
# One preflight at the start of a live block.
.\Test-I5Link.ps1

# Preview or stage a bundle with hash verification.
.\Deploy-ToI5.ps1 -Path .\bundle -DryRun
.\Deploy-ToI5.ps1 -Path .\bundle -Dest C:/deploy/networksense/run-042

# Deploy a verified mod DLL directly to BepInEx plugins.
.\Deploy-ToI5.ps1 `
  -Path ..\..\network\mod\ComfyNetworkSense\bin\Release\ComfyNetworkSense.dll `
  -ValheimPlugins

# Exercise directory exclusions and large-manifest batching. This writes only
# beneath the NetworkSense remote fixture root.
.\Test-DeployToI5Fixtures.ps1

# Bounded two-client evidence lanes.
.\Start-TwoClientCapture.ps1 -DurationSeconds 30 -Label sprint-stutter
.\Set-TwoClientApplyRoles.ps1 -ApplyClient omen
.\Start-TwoClientMotionTest.ps1 -Pattern straight_north -DurationSeconds 10
.\Start-TwoClientFeelWindow.ps1 -Pattern straight_north -RoleReversal -DryRun
```

Deploys compare a per-file SHA-256 manifest. A green run is the receipt.
Top-level duplicate filenames are rejected, and excluded build directories are
never copied. The scripts never fall back to password authentication.
