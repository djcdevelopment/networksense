# ComfyNetworkSense

ComfyNetworkSense is a Valheim/BepInEx instrument panel for local network
conditions. It records bounded client and server telemetry, renders the HUD and
transport truth strip, calculates Owner Score, and exposes explicit diagnostic
controls. It is not a replacement netcode implementation.

## Build

The project targets `net48` and references the operator's local Valheim and
BepInEx assemblies. From the repository root:

```powershell
dotnet build network/mod/ComfyNetworkSense/ComfyNetworkSense.csproj -c Release -p:ComfyDependencyProfile=interim
```

If Valheim is elsewhere, pass `-p:ValheimDir="D:\path\to\Valheim"`. Normal
builds must leave `ComfyCopyToPlugins` unset. Deployment is performed by the
hash-verifying i5/AM4/release lanes rather than by an implicit post-build copy.

## Tests

The Unity-free source seams are compiled into the focused net8 xUnit project:

```powershell
dotnet test network/mod/ComfyNetworkSense.Tests/ComfyNetworkSense.Tests.csproj -c Release
```

The required result is exactly 166 passed tests.

## Package boundary

Quest glue and transport admission/policy source arrive through
`Comfy.Quest.Contracts` and `Comfy.Transport.Contracts`. They are compiled into
the single mod DLL through NuGet `contentFiles`; no sibling repository source is
linked. The publication profile carries exact `[0.1.0]` pins and uses only
NuGet.org. Until those packages are public, builds explicitly select the
`interim` profile shown above, which retains `0.1.0-local` in `packages-local`.

## Runtime integrations

- Platform Gateway and Companion capabilities are consumed through their
  versioned HTTP contracts and `/identity` responses.
- The optional development MCP side channel is a loopback HTTP integration.
  Its implementation is owned by `djcdevelopment/isolate`; this repository does
  not require that source checkout.
- Dashboard setup documentation is owned by
  `djcdevelopment/lumberjacks-platform` and is linked as a web resource from the
  HUD when the local dashboard is unavailable.

Telemetry and event outputs live beneath the BepInEx configuration directory.
The MCP side channel and invasive diagnostic features are off by default and
must be enabled explicitly in a Dev/Lab profile.

See the root [BOUNDARY.md](../../../BOUNDARY.md) for the ownership and artifact
contract, and [COMMANDS.md](COMMANDS.md) for the in-game command surface.
