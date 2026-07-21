namespace ComfyNetworkSense;

using System;
using System.Reflection;
using HarmonyLib;

/// <summary>
/// Harmony hook for server-side combat telemetry. Postfix on <c>Character.RPC_Damage(long, HitData)</c>
/// — on a dedicated server the server owns creatures, so their damage/death is applied through this
/// RPC, which is where the killing blow becomes observable with the attacker attached.
///
/// Applied manually (not via CreateAndPatchAll) in the guarded, fail-soft style of
/// <c>PanelInputPatches</c>: combat method signatures shift between Valheim builds, so a missing
/// target logs a warning and disables the hook rather than aborting plugin load. The postfix body
/// swallows everything — telemetry must never break combat — and no-ops unless the producer is
/// armed (<see cref="GameplayEventProducer.Active"/> is null when disabled).
/// </summary>
public static class GameplayEventPatches {
  public static void Apply(Harmony harmony) {
    try {
      MethodInfo target = AccessTools.Method(typeof(Character), "RPC_Damage", new[] { typeof(long), typeof(HitData) });
      if (target == null) {
        ComfyNetworkSense.LogWarning("GameplayEventPatches: Character.RPC_Damage(long, HitData) not found; gameplay telemetry disabled.");
        return;
      }

      MethodInfo postfix = typeof(GameplayEventPatches)
          .GetMethod(nameof(RpcDamagePostfix), BindingFlags.Static | BindingFlags.NonPublic);
      harmony.Patch(target, postfix: new HarmonyMethod(postfix));
    } catch (Exception exception) {
      ComfyNetworkSense.LogWarning("GameplayEventPatches.Apply failed: " + exception.Message);
    }
  }

  static void RpcDamagePostfix(Character __instance, HitData hit) {
    try {
      GameplayEventProducer.Active?.OnCreatureDamaged(__instance, hit);
    } catch {
      // Telemetry is strictly observational; never let it disturb the damage path.
    }
  }
}
