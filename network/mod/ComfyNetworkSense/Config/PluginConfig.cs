namespace ComfyNetworkSense;

using BepInEx.Configuration;

using UnityEngine;

public static class PluginConfig {
  public static ConfigEntry<bool> IsModEnabled { get; private set; }
  public static ConfigEntry<bool> ShowHudOnStart { get; private set; }
  public static ConfigEntry<bool> WriteTelemetryLogs { get; private set; }
  public static ConfigEntry<float> LiveSampleIntervalSeconds { get; private set; }
  public static ConfigEntry<float> ServerPulseIntervalSeconds { get; private set; }
  public static ConfigEntry<float> NearbyRadiusMeters { get; private set; }
  public static ConfigEntry<float> BuildScanRadiusMeters { get; private set; }
  public static ConfigEntry<float> BenchmarkDurationSeconds { get; private set; }
  public static ConfigEntry<float> HudScale { get; private set; }
  public static ConfigEntry<float> HudOpacity { get; private set; }
  public static ConfigEntry<float> HudMaxWidth { get; private set; }
  public static ConfigEntry<int> HudMarginPixels { get; private set; }
  public static ConfigEntry<string> HudPreset { get; private set; }
  public static ConfigEntry<KeyboardShortcut> ToggleHudShortcut { get; private set; }
  public static ConfigEntry<KeyboardShortcut> CycleHudDetailShortcut { get; private set; }
  public static ConfigEntry<KeyboardShortcut> CycleModeShortcut { get; private set; }
  public static ConfigEntry<KeyboardShortcut> ToggleBenchmarkShortcut { get; private set; }

  public static void Bind(ConfigFile config) {
    IsModEnabled =
        config.Bind(
            "General",
            "isModEnabled",
            true,
            "Enable the ComfyNetworkSense telemetry scaffold.");

    ShowHudOnStart =
        config.Bind(
            "HUD",
            "showHudOnStart",
            true,
            "Show the network HUD when the plugin starts.");

    HudScale =
        config.Bind(
            "HUD",
            "hudScale",
            1.0f,
            "Scale factor for the IMGUI HUD.");

    HudOpacity =
        config.Bind(
            "HUD",
            "hudOpacity",
            0.72f,
            "Opacity for the compact HUD background.");

    HudMaxWidth =
        config.Bind(
            "HUD",
            "hudMaxWidth",
            1120.0f,
            "Maximum compact HUD width in pixels before scale is applied.");

    HudMarginPixels =
        config.Bind(
            "HUD",
            "hudMarginPixels",
            16,
            "Screen margin in pixels for the HUD.");

    HudPreset =
        config.Bind(
            "HUD",
            "hudPreset",
            "Compact",
            "Compact HUD layout preset: Minimal, Compact, or Diagnostic.");

    ToggleHudShortcut =
        config.Bind(
            "HUD",
            "toggleHudShortcut",
            new KeyboardShortcut(KeyCode.None),
            "Optional shortcut that toggles the network HUD. Leave None to use console commands only.");

    CycleHudDetailShortcut =
        config.Bind(
            "HUD",
            "cycleHudDetailShortcut",
            new KeyboardShortcut(KeyCode.None),
            "Optional shortcut that cycles the HUD detail level. Leave None to use console commands only.");

    CycleModeShortcut =
        config.Bind(
            "Modes",
            "cycleModeShortcut",
            new KeyboardShortcut(KeyCode.None),
            "Optional shortcut that cycles Solo -> Combat -> Group Combat -> Town. Leave None to use console commands only.");

    ToggleBenchmarkShortcut =
        config.Bind(
            "Benchmark",
            "toggleBenchmarkShortcut",
            new KeyboardShortcut(KeyCode.None),
            "Optional shortcut that starts or stops the local benchmark capture. Leave None to use console commands only.");

    LiveSampleIntervalSeconds =
        config.Bind(
            "Sampling",
            "liveSampleIntervalSeconds",
            0.5f,
            "How often client samples are captured and written.");

    ServerPulseIntervalSeconds =
        config.Bind(
            "Sampling",
            "serverPulseIntervalSeconds",
            3.0f,
            "How often the server broadcasts a pulse snapshot.");

    NearbyRadiusMeters =
        config.Bind(
            "Sampling",
            "nearbyRadiusMeters",
            64.0f,
            "Radius used for nearby player and entity counts.");

    BuildScanRadiusMeters =
        config.Bind(
            "Sampling",
            "buildScanRadiusMeters",
            64.0f,
            "Radius used for nearby build piece counts.");

    BenchmarkDurationSeconds =
        config.Bind(
            "Benchmark",
            "benchmarkDurationSeconds",
            15.0f,
            "How long a benchmark run lasts before auto-completing.");

    WriteTelemetryLogs =
        config.Bind(
            "Logging",
            "writeTelemetryLogs",
            true,
            "Write JSONL telemetry logs under BepInEx/config/comfy-network-sense.");
  }
}
