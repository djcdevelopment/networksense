namespace ComfyNetworkSense;

using System;
using System.Reflection;
using HarmonyLib;

/// <summary>
/// Harmony hook for server-side combat telemetry. Postfixes creature damage where the server owns
/// the creature, so a killing blow becomes observable with the attacker attached.
///
/// DIAGNOSTIC BUILD: patches BOTH <c>Character.RPC_Damage(long, HitData)</c> (remote-originated
/// damage) AND <c>Character.Damage(HitData)</c> (the owner's local damage application) and logs
/// which one fires, so a single controlled kill reveals whether the server sees the event at all
/// and via which path. Applied manually in the guarded, fail-soft style of PanelInputPatches.
/// Only RPC_Damage drives emission; the Damage hook is diagnostic-only for now.
/// </summary>
public static class GameplayEventPatches {
  public static void Apply(Harmony harmony) {
    PatchOne(harmony, "RPC_Damage", new[] { typeof(long), typeof(HitData) }, nameof(RpcDamagePostfix));
    PatchOne(harmony, "Damage", new[] { typeof(HitData) }, nameof(DamagePostfix));
  }

  static void PatchOne(Harmony harmony, string methodName, Type[] argTypes, string postfixName) {
    try {
      MethodInfo target = AccessTools.Method(typeof(Character), methodName, argTypes);
      if (target == null) {
        ComfyNetworkSense.LogWarning("GameplayEventPatches: Character." + methodName + " not found; that hook is disabled.");
        return;
      }

      MethodInfo postfix = typeof(GameplayEventPatches)
          .GetMethod(postfixName, BindingFlags.Static | BindingFlags.NonPublic);
      harmony.Patch(target, postfix: new HarmonyMethod(postfix));
      ComfyNetworkSense.LogInfo("GameplayEventPatches: patched Character." + methodName);
    } catch (Exception exception) {
      ComfyNetworkSense.LogWarning("GameplayEventPatches: patching Character." + methodName + " failed: " + exception.Message);
    }
  }

  static void RpcDamagePostfix(Character __instance, HitData hit) {
    try {
      GameplayEventProducer.Active?.OnCreatureDamaged(__instance, hit, "rpc");
    } catch {
      // Telemetry is strictly observational; never disturb the damage path.
    }
  }

  static void DamagePostfix(Character __instance, HitData hit) {
    try {
      // On the client the player owns the creature it melees, so the local Character.Damage path
      // is what fires (not RPC_Damage). Both feed the same classifier, which dedups per creature
      // death, so a kill that trips both hooks still emits exactly one killing_blow.
      GameplayEventProducer.Active?.OnCreatureDamaged(__instance, hit, "damage");
    } catch {
    }
  }
}
