# AM4 deploy lane

`Deploy-NetworkSense.ps1` is the repeatable local-lab server deployment lane for
`ComfyNetworkSense.dll`. It uses BatchMode SSH, stages and hashes the exact DLL,
preserves the previous server artifact, atomically replaces the mounted plugin,
restarts the dedicated-server container, and waits for both the requested plugin
version and Valheim server readiness.

From the repository root:

```powershell
tools\am4\Deploy-NetworkSense.ps1 `
  -OutputPath fieldlab\runs\native-valheim\<run-id>\am4-deploy.json
```

The command fails closed unless the local, AM4 host-mounted, and container-visible
SHA256 values are identical. The emitted JSON is the deployment receipt. This is the
AM4 counterpart to `tools/i5/Deploy-ToI5.ps1`; neither lane falls back to password
authentication.
