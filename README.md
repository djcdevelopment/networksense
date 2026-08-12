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
dotnet build network/mod/ComfyNetworkSense/ComfyNetworkSense.csproj -c Release -p:ComfyDependencyProfile=interim
dotnet test network/mod/ComfyNetworkSense.Tests/ComfyNetworkSense.Tests.csproj -c Release -p:ComfyDependencyProfile=interim
powershell -NoProfile -File tools/Test-BoundaryGuards.ps1
```

The expected xUnit result is exactly `166 passed, 0 failed`.

The publication-ready profile uses exact public `[0.1.0]` pins and a
NuGet.org-only feed. Those package IDs are not published yet, so local and CI
builds must explicitly select `ComfyDependencyProfile=interim`; that profile
retains the tracked `packages-local/` feed without weakening the public pins.

## Prepare a mod release

The current candidate is `mod-v0.5.80-split-proof`. A local development host
with Valheim+BepInEx prepares the DLL and boundary evidence; GitHub only verifies
the published assets and never cloud-builds against licensed game assemblies.

```powershell
$tag = 'mod-v0.5.80-split-proof'
network/tools/New-ModReleaseCut.ps1 -Mode Prepare -ReleaseTag $tag -ExpectedPluginVersion 0.5.80 -DependencyProfile interim
network/tools/New-ModReleaseCut.ps1 -Mode Verify -ReleaseTag $tag -ExpectedPluginVersion 0.5.80 -DependencyProfile interim
network/tools/Test-ModReleaseTamperFixtures.ps1 -ArtifactDirectory "artifacts/releases/$tag" -ExpectedTag $tag -ExpectedDependencyProfile interim
```

These commands create no tag or release and publish nothing. See
[the release-lane runbook](network/tools/README.md) for the public-package cut.

## Integration model

NetworkSense publishes a DLL, release manifest, boundary receipt, and checksum.
Platform and Quest integrations consume those artifacts or their own versioned
HTTP/package contracts. No build or script requires another repository to be
checked out.
