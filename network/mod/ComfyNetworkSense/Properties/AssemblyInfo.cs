using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("ComfyNetworkSense")]
[assembly: AssemblyDescription("")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("ComfyNetworkSense")]
[assembly: AssemblyCopyright("Copyright © 2026")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

[assembly: ComVisible(false)]
[assembly: Guid("63de1fb5-8d95-4bb2-b279-8d313fca7749")]

[assembly: AssemblyVersion(ComfyNetworkSense.ComfyNetworkSense.PluginVersion)]
[assembly: AssemblyFileVersion(ComfyNetworkSense.ComfyNetworkSense.PluginVersion)]

// Projects the ReleaseId const into assembly metadata so the SHIPPED ARTIFACT can be asked what
// release it belongs to, rather than the question only being answerable by reading source.
//
// This exists because the release id has to be set in two places at a cut - here as a const (this
// project sets GenerateAssemblyInfo=false, so it cannot take an MSBuild property the way the
// Gateway does) and on the Gateway build as -p:LumberjacksExpectedModRelease. Two places is a cut
// that can set one and forget the other, and the failure it produces is a Gateway that rejects the
// very mod it shipped with. Nobody can eyeball that. With both artifacts carrying the id as
// AssemblyMetadata under the same shape, a cut can BUILD BOTH AND COMPARE THEM, which is the only
// thing that makes two places survivable.
[assembly: AssemblyMetadata("LumberjacksModReleaseId", ComfyNetworkSense.ComfyNetworkSense.ReleaseId)]
