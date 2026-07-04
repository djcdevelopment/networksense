namespace ComfyNetworkSense;

using System;
using System.Reflection;

using HarmonyLib;

using UnityEngine;

public static class PanelInputPatches {
  public static void Apply(Harmony harmony) {
    TryPatch(
        harmony,
        AccessTools.Method(typeof(GameCamera), "UpdateMouseCapture"),
        nameof(UpdateMouseCapturePostfix),
        "GameCamera.UpdateMouseCapture");

    TryPatch(
        harmony,
        AccessTools.Method(typeof(Character), "TakeInput"),
        nameof(TakeInputPostfix),
        "Character.TakeInput");
  }

  static void TryPatch(Harmony harmony, MethodBase target, string postfixName, string label) {
    try {
      if (target == null) {
        throw new InvalidOperationException("method not found");
      }

      harmony.Patch(target, postfix: new HarmonyMethod(typeof(PanelInputPatches), postfixName));
      ComfyNetworkSense.LogInfo($"Panel input patch applied: {label}");
    } catch (Exception exception) {
      ComfyNetworkSense.LogWarning($"Panel input patch skipped: {label}: {exception.Message}");
    }
  }

  static void UpdateMouseCapturePostfix() {
    if (ComfyNetworkSense.IsPanelOpen) {
      Cursor.lockState = CursorLockMode.None;
      Cursor.visible = true;
    }
  }

  static void TakeInputPostfix(Character __instance, ref bool __result) {
    if (ComfyNetworkSense.IsPanelOpen && __instance == Player.m_localPlayer) {
      __result = false;
    }
  }
}

