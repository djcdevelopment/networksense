# NetworkSense mod release lane

The mod release is built on a development host that already has the licensed
Valheim and BepInEx compile inputs. GitHub Actions never builds the mod. A
published-release workflow downloads the four host-built assets and verifies
their hashes, assembly identity, source revision, and boundary receipt.

## Current split-proof candidate

The current candidate is `mod-v0.5.80-split-proof`: source, Thunderstore
manifest, tag version, and assembly metadata must all identify `0.5.80`.

Until the two public `0.1.0` contract packages exist, prepare and verify a local
candidate with the explicitly selected interim dependency profile:

```powershell
$tag = 'mod-v0.5.80-split-proof'
network/tools/New-ModReleaseCut.ps1 -Mode Prepare -ReleaseTag $tag -ExpectedPluginVersion 0.5.80 -DependencyProfile interim
network/tools/New-ModReleaseCut.ps1 -Mode Verify -ReleaseTag $tag -ExpectedPluginVersion 0.5.80 -DependencyProfile interim
network/tools/Test-ModReleaseTamperFixtures.ps1 -ArtifactDirectory "artifacts/releases/$tag" -ExpectedTag $tag -ExpectedDependencyProfile interim
```

This is preparation only. Those commands do not create a Git tag or GitHub
release and do not publish a NuGet package.

After `Comfy.Quest.Contracts` and `Comfy.Transport.Contracts` version `0.1.0`
are publicly available, restore with the default public profile and make the
publication candidate from a clean tagged checkout:

```powershell
$tag = 'mod-v0.5.80-split-proof'
dotnet restore network/mod/ComfyNetworkSense/ComfyNetworkSense.csproj -p:ComfyDependencyProfile=public
network/tools/New-ModReleaseCut.ps1 -Mode Prepare -ReleaseTag $tag -ExpectedPluginVersion 0.5.80 -DependencyProfile public -RequireTagAtHead
```

Upload exactly these assets before publishing the GitHub release:

- `ComfyNetworkSense.dll`
- `release-manifest.json` (`comfy-mod-release-manifest/v1`)
- `boundary-receipt.json` (`comfy-mod-boundary-receipt/v1`)
- `SHA256SUMS`

The boundary receipt records the release tag, source revision, source digest,
plugin version, baked release ID, DLL hash and byte count, SDK/config/profile,
contract package hashes, and every Valheim+BepInEx assembly used to compile.
The cutter always forces a Release rebuild with `ComfyCopyToPlugins=false` and
also proves the live plugin hash did not change.

## Dependency profiles

`public` is the publication-ready default. It reads exact `[0.1.0]` pins from
`eng/dependencies.public.props` and uses the NuGet.org-only `nuget.config`.

`interim` must be selected explicitly. It reads `0.1.0-local` pins from
`eng/dependencies.interim.props` and uses `nuget.interim.config`, which retains
the tracked `packages-local` feed. CI uses this profile until public packages
are available; changing the profile does not change release-bundle evidence.
