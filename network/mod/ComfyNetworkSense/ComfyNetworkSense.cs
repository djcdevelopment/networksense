namespace ComfyNetworkSense;

using System;
using System.Globalization;
using System.Reflection;

using BepInEx;
using BepInEx.Logging;

using HarmonyLib;

using UnityEngine;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class ComfyNetworkSense : BaseUnityPlugin {
  public const string PluginGuid = "djcdevelopment.valheim.comfynetworksense";
  public const string PluginName = "ComfyNetworkSense";
  public const string PluginVersion = "0.1.0";

  public static ComfyNetworkSense Instance { get; private set; }

  static ManualLogSource _logger;

  TelemetryCoordinator _coordinator;

  void Awake() {
    Instance = this;
    _logger = Logger;

    PluginConfig.Bind(Config);

    _coordinator = new();

    Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), harmonyInstanceId: PluginGuid);

    LogInfo("Telemetry scaffold ready.");
  }

  void Update() {
    if (!PluginConfig.IsModEnabled.Value) {
      return;
    }

    _coordinator?.Update(Time.unscaledDeltaTime);
  }

  void OnGUI() {
    if (!PluginConfig.IsModEnabled.Value) {
      return;
    }

    _coordinator?.DrawHud();
  }

  void OnDestroy() {
    _coordinator?.Dispose();
    _coordinator = null;

    if (Instance == this) {
      Instance = null;
    }
  }

  public static void HandleServerPulse(long senderId, ZPackage package) {
    Instance?._coordinator?.HandleServerPulse(senderId, package);
  }

  public static void LogInfo(object message) {
    _logger?.LogInfo($"[{DateTime.Now.ToString(DateTimeFormatInfo.InvariantInfo)}] {message}");
  }

  public static void LogWarning(object message) {
    _logger?.LogWarning($"[{DateTime.Now.ToString(DateTimeFormatInfo.InvariantInfo)}] {message}");
  }
}
