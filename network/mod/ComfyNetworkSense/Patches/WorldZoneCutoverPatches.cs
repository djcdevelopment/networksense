namespace ComfyNetworkSense;

using HarmonyLib;

[HarmonyPatch(typeof(ZRpc), nameof(ZRpc.Invoke))]
static class WorldZoneBlankPeerInfoPatch {
  [HarmonyPrefix]
  [HarmonyPriority(Priority.High)]
  static void InvokePrefix(string method, object[] parameters) =>
      WorldZoneCutoverRunner.BlankServerWorldPayload(method, parameters);
}

[HarmonyPatch(typeof(ZNet))]
static class WorldZoneSubstitutePeerInfoPatch {
  [HarmonyPrefix]
  [HarmonyPriority(Priority.High)]
  [HarmonyPatch("RPC_PeerInfo", new[] { typeof(ZRpc), typeof(ZPackage) })]
  static bool RpcPeerInfoPrefix(ZRpc rpc, ref ZPackage pkg) =>
      WorldZoneCutoverRunner.TryPrepareClientPeerInfo(rpc, ref pkg, out _);
}
