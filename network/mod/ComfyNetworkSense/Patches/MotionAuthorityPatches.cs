namespace ComfyNetworkSense;

using HarmonyLib;

/// <summary>
/// C6's negative-control seam. Once a remote player has produced a canonical
/// Lumberjacks motion frame, Valheim's ZDO-driven presentation writer and later
/// native position mutations are suppressed for that entity. Initial scene creation
/// is allowed before the first canonical frame; C7 removes the native join itself.
/// </summary>
[HarmonyPatch(typeof(ZSyncTransform), nameof(ZSyncTransform.CustomFixedUpdate))]
static class MotionAuthorityTransformPatch {
  [HarmonyPrefix]
  [HarmonyPriority(Priority.First)]
  static bool Prefix(ZSyncTransform __instance) =>
      !LumberjacksMotionRunner.SuppressNativeTransform(__instance);
}

[HarmonyPatch(typeof(ZDO), nameof(ZDO.InternalSetPosition))]
static class MotionAuthorityPositionPatch {
  [HarmonyPrefix]
  [HarmonyPriority(Priority.First)]
  static bool Prefix(ZDO __instance) =>
      !LumberjacksMotionRunner.MaskNativePosition(__instance);
}
