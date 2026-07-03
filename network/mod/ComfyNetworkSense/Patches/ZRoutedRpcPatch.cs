namespace ComfyNetworkSense;

using System;

using HarmonyLib;

[HarmonyPatch(typeof(ZRoutedRpc))]
static class ZRoutedRpcPatch {
  [HarmonyPostfix]
  [HarmonyPatch("Awake")]
  static void AwakePostfix(ref ZRoutedRpc __instance) {
    if (!PluginConfig.IsModEnabled.Value || __instance == null) {
      return;
    }

    __instance.Register(
        ServerPulseBroadcaster.ServerPulseRpc,
        new Action<long, ZPackage>(ComfyNetworkSense.HandleServerPulse));
  }
}
