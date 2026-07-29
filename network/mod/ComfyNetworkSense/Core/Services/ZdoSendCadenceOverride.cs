namespace ComfyNetworkSense;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

using HarmonyLib;

using UnityEngine;

/// <summary>
/// Optional server-side replacement for Valheim's ZDO send scheduler.
/// </summary>
///
/// <remarks>
/// This is a local implementation of the proven Comfy-era idea behind ReturnToSender, not copied
/// source. The patch is opportunistic: if this Valheim build has no separate send-scheduler helper
/// call inside <c>ZDOMan.Update</c>, Harmony leaves the method unchanged and telemetry reports that
/// the override is unavailable. Default OFF.
/// </remarks>
public static class ZdoSendCadenceOverride {
  static readonly FieldInfo PeersField = AccessTools.Field(typeof(ZDOMan), "m_peers");
  static readonly MethodInfo SendZdosMethod = AccessTools.Method(typeof(ZDOMan), "SendZDOs");
  static readonly MethodInfo VanillaSendToPeersMethod = FindVanillaSendToPeersMethod();

  static float _sendTimer;
  static long _calls;
  static long _vanillaDelegations;
  static long _passes;
  static long _peerSends;
  static long _lastPeerCount;
  static float _lastDurationMs;
  static float _maxDurationMs;
  static string _lastError = string.Empty;

  public static bool PatchInstalled { get; internal set; }

  public static MethodInfo VanillaSendToPeers => VanillaSendToPeersMethod;

  public static void SendZDOToPeers(ZDOMan zdoManager, float dt) {
    // Times the whole rewritten call site (override and vanilla-delegation branches alike), so
    // perf-patchload.jsonl rows are comparable with the prefix/postfix patch sections.
    using NetworkSensePerfProbe.PatchLoadSection _ =
        NetworkSensePerfProbe.MeasurePatchLoad("Patch.ZDOMan.Update.SendCadenceCallSite");
    _calls++;

    if (!PluginConfig.ZdoSendCadenceOverrideEnabled.Value) {
      DelegateToVanilla(zdoManager, dt);
      return;
    }

    try {
      if (PeersField == null || SendZdosMethod == null) {
        _lastError = "required reflection handles unavailable";
        DelegateToVanilla(zdoManager, dt);
        return;
      }

      _sendTimer += dt;
      float interval = Mathf.Max(0.02f, PluginConfig.ZdoSendCadenceOverrideIntervalSeconds.Value);
      if (_sendTimer <= interval) {
        return;
      }

      _sendTimer = 0f;
      float start = Time.realtimeSinceStartup;
      if (PeersField.GetValue(zdoManager) is IEnumerable peers) {
        long peerCount = 0;
        foreach (object peer in peers) {
          if (peer == null) {
            continue;
          }

          SendZdosMethod.Invoke(zdoManager, new[] { peer, (object) false });
          _peerSends++;
          peerCount++;
        }

        _lastPeerCount = peerCount;
      } else {
        _lastPeerCount = 0;
      }

      _passes++;
      _lastDurationMs = (Time.realtimeSinceStartup - start) * 1000.0f;
      if (_lastDurationMs > _maxDurationMs) {
        _maxDurationMs = _lastDurationMs;
      }
      _lastError = string.Empty;
    } catch (Exception exception) {
      Exception root = exception is TargetInvocationException tie && tie.InnerException != null
          ? tie.InnerException : exception;
      _lastError = root.GetType().Name + ": " + root.Message;
      ComfyNetworkSense.LogWarning("ZDO send cadence override failed; falling back to vanilla this tick: " + _lastError);
      DelegateToVanilla(zdoManager, dt);
    }
  }

  static void DelegateToVanilla(ZDOMan zdoManager, float dt) {
    _vanillaDelegations++;
    try {
      VanillaSendToPeersMethod?.Invoke(zdoManager, new object[] { dt });
    } catch (Exception exception) {
      Exception root = exception is TargetInvocationException tie && tie.InnerException != null
          ? tie.InnerException : exception;
      _lastError = "vanilla delegation failed: " + root.GetType().Name + ": " + root.Message;
      throw;
    }
  }

  static MethodInfo FindVanillaSendToPeersMethod() =>
      AccessTools.Method(typeof(ZDOMan), "SendZDOToPeers2", new[] { typeof(float) })
      ?? AccessTools.Method(typeof(ZDOMan), "SendZDOToPeers", new[] { typeof(float) });

  public static Dictionary<string, object> Snapshot() =>
      new() {
          ["zdo_send_cadence_override_enabled"] = PluginConfig.ZdoSendCadenceOverrideEnabled.Value,
          ["zdo_send_cadence_override_interval_seconds"] =
              PluginConfig.ZdoSendCadenceOverrideIntervalSeconds.Value,
          ["zdo_send_cadence_patch_installed"] = PatchInstalled,
          ["zdo_send_cadence_reflection_ok"] =
              PeersField != null && SendZdosMethod != null && VanillaSendToPeersMethod != null,
          ["zdo_send_cadence_calls"] = _calls,
          ["zdo_send_cadence_vanilla_delegations"] = _vanillaDelegations,
          ["zdo_send_cadence_passes"] = _passes,
          ["zdo_send_cadence_peer_sends"] = _peerSends,
          ["zdo_send_cadence_last_peer_count"] = _lastPeerCount,
          ["zdo_send_cadence_last_duration_ms"] = Math.Round(_lastDurationMs, 3),
          ["zdo_send_cadence_max_duration_ms"] = Math.Round(_maxDurationMs, 3),
          ["zdo_send_cadence_last_error"] = _lastError
      };
}

[HarmonyPatch(typeof(ZDOMan))]
static class ZdoSendCadenceOverridePatches {
  [HarmonyTranspiler]
  [HarmonyPatch(nameof(ZDOMan.Update))]
  static IEnumerable<CodeInstruction> UpdateTranspiler(IEnumerable<CodeInstruction> instructions) {
    MethodInfo vanilla = ZdoSendCadenceOverride.VanillaSendToPeers;
    MethodInfo replacement =
        AccessTools.Method(typeof(ZdoSendCadenceOverride), nameof(ZdoSendCadenceOverride.SendZDOToPeers));

    if (vanilla == null || replacement == null) {
      ZdoSendCadenceOverride.PatchInstalled = false;
      return instructions;
    }

    CodeMatcher matcher = new(instructions);
    matcher.Start().MatchStartForward(new CodeMatch(OpCodes.Call, vanilla));
    if (!matcher.IsValid) {
      ZdoSendCadenceOverride.PatchInstalled = false;
      return instructions;
    }

    ZdoSendCadenceOverride.PatchInstalled = true;
    return matcher
        .SetOperandAndAdvance(replacement)
        .InstructionEnumeration();
  }
}
