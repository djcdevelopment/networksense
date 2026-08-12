# NetworkSense

NetworkSense is the standalone Valheim client-observability add-on extracted
from the Baseline research monorepo. It contains the BepInEx mod, its focused
test suite, and bounded mod deployment/package tooling.

## Start here

- [Repository boundary](BOUNDARY.md)
- [Working rules](AGENTS.md)
- [Extraction provenance](PROVENANCE.md)
- [Mod documentation](network/mod/ComfyNetworkSense/README.md)
- [i5 deploy lane](tools/i5/README.md)

## Build and test

The release build needs a local Valheim installation with BepInEx. It never
copies into the live plugin directory unless `ComfyCopyToPlugins=true` is
explicitly supplied; normal verification must leave that property unset.

```powershell
dotnet build network/mod/ComfyNetworkSense/ComfyNetworkSense.csproj -c Release
dotnet test network/mod/ComfyNetworkSense.Tests/ComfyNetworkSense.Tests.csproj -c Release
powershell -NoProfile -File tools/Test-BoundaryGuards.ps1
```

The expected xUnit result is exactly `166 passed, 0 failed`.

During extraction, the two cross-repository source packages are vendored in
`packages-local/`. They are temporary: once the `0.1.0` package releases are
published, consumers switch to exact public pins and the local feed is removed.

## Integration model

NetworkSense publishes a DLL, release manifest, and checksum. Platform and
Quest integrations consume those artifacts or their own versioned HTTP/package
contracts. No build or script requires another repository to be checked out.
