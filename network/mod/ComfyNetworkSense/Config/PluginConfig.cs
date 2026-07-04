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
  public static ConfigEntry<bool> AutoRehearsalEnabled { get; private set; }
  public static ConfigEntry<string> AutoRehearsalRouteFile { get; private set; }
  public static ConfigEntry<string> AutoRehearsalProfile { get; private set; }
  public static ConfigEntry<float> AutoRehearsalDelaySeconds { get; private set; }
  public static ConfigEntry<bool> AutoRehearsalRunOncePerSession { get; private set; }
  public static ConfigEntry<bool> AutoJoinEnabled { get; private set; }
  public static ConfigEntry<string> AutoJoinCharacterName { get; private set; }
  public static ConfigEntry<int> AutoJoinCharacterIndex { get; private set; }
  public static ConfigEntry<bool> AutoJoinDeriveFromHostname { get; private set; }
  public static ConfigEntry<bool> AutoJoinCreateIfMissing { get; private set; }
  public static ConfigEntry<string> AutoJoinNewCharacterName { get; private set; }
  public static ConfigEntry<float> AutoJoinInitialDelaySeconds { get; private set; }
  public static ConfigEntry<float> AutoJoinPollIntervalSeconds { get; private set; }
  public static ConfigEntry<float> AutoJoinTimeoutSeconds { get; private set; }
  public static ConfigEntry<bool> MatrixCheckinEnabled { get; private set; }
  public static ConfigEntry<string> MatrixGatewayUrl { get; private set; }
  public static ConfigEntry<float> MatrixPollIntervalSeconds { get; private set; }
  public static ConfigEntry<string> MatrixClientId { get; private set; }
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

    AutoRehearsalEnabled =
        config.Bind(
            "Automation",
            "autoRehearsalEnabled",
            false,
            "Automatically start a route rehearsal after a local player is available. Intended for private lab clients only.");

    AutoRehearsalRouteFile =
        config.Bind(
            "Automation",
            "autoRehearsalRouteFile",
            "teleport-route.tsv",
            "Route file under BepInEx/config/comfy-network-sense used by automatic rehearsals.");

    AutoRehearsalProfile =
        config.Bind(
            "Automation",
            "autoRehearsalProfile",
            "host_full",
            "Resource profile label recorded in automatic rehearsal markers.");

    AutoRehearsalDelaySeconds =
        config.Bind(
            "Automation",
            "autoRehearsalDelaySeconds",
            20.0f,
            "Seconds to wait after the local player is available before automatic rehearsal starts.");

    AutoRehearsalRunOncePerSession =
        config.Bind(
            "Automation",
            "autoRehearsalRunOncePerSession",
            true,
            "Run the automatic rehearsal at most once per Valheim client session.");

    AutoJoinEnabled =
        config.Bind(
            "AutoJoin",
            "autoJoinEnabled",
            false,
            "Automatically pick a character and connect at the FejdStartup screen. Intended for private lab/swarm clients only. The COMFY_AUTOJOIN environment variable overrides this at runtime.");

    AutoJoinCharacterName =
        config.Bind(
            "AutoJoin",
            "autoJoinCharacterName",
            "",
            "If set, auto-join selects the character profile with this name. Overridden by the COMFY_AUTOJOIN_CHARACTER environment variable. Leave blank to select by index / hostname.");

    AutoJoinCharacterIndex =
        config.Bind(
            "AutoJoin",
            "autoJoinCharacterIndex",
            0,
            "Zero-based character profile index used when no name is matched. Overridden by COMFY_AUTOJOIN_INDEX and, unless disabled, by the hostname-derived index.");

    AutoJoinDeriveFromHostname =
        config.Bind(
            "AutoJoin",
            "autoJoinDeriveFromHostname",
            true,
            "Derive the character index from the trailing number in the machine hostname (e.g. valheim-client-02 -> index 1), so a swarm sharing one config still spreads across distinct characters. A matched name or COMFY_AUTOJOIN_INDEX takes priority.");

    AutoJoinCreateIfMissing =
        config.Bind(
            "AutoJoin",
            "autoJoinCreateIfMissing",
            true,
            "If no character profiles exist, create one before connecting.");

    AutoJoinNewCharacterName =
        config.Bind(
            "AutoJoin",
            "autoJoinNewCharacterName",
            "comfyplayer",
            "Base name used when auto-join has to create a character. The hostname number is appended when available.");

    AutoJoinInitialDelaySeconds =
        config.Bind(
            "AutoJoin",
            "autoJoinInitialDelaySeconds",
            8.0f,
            "Seconds to wait after the main menu loads before driving character selection.");

    AutoJoinPollIntervalSeconds =
        config.Bind(
            "AutoJoin",
            "autoJoinPollIntervalSeconds",
            1.0f,
            "How often to re-check for available character profiles while waiting.");

    AutoJoinTimeoutSeconds =
        config.Bind(
            "AutoJoin",
            "autoJoinTimeoutSeconds",
            120.0f,
            "Give up driving auto-join if no character is selectable within this many seconds.");

    MatrixCheckinEnabled =
        config.Bind(
            "Matrix",
            "matrixCheckinEnabled",
            false,
            "Continuously check out benchmark cells from a gateway, teleport to each, run a NetworkSense benchmark, and report results. Intended for private lab/swarm clients only. The COMFY_MATRIX_CHECKIN environment variable overrides this at runtime.");

    MatrixGatewayUrl =
        config.Bind(
            "Matrix",
            "matrixGatewayUrl",
            "http://127.0.0.1:8720",
            "Base URL of the matrix gateway (no trailing slash). In containers this is usually http://comfy-gateway:8720. Overridden by the COMFY_GATEWAY_URL environment variable.");

    MatrixPollIntervalSeconds =
        config.Bind(
            "Matrix",
            "matrixPollIntervalSeconds",
            5.0f,
            "Seconds to back off between matrix check-out attempts when idle/done or after an error.");

    MatrixClientId =
        config.Bind(
            "Matrix",
            "matrixClientId",
            "",
            "Client id reported to the matrix gateway. Leave blank to use the machine hostname.");

    WriteTelemetryLogs =
        config.Bind(
            "Logging",
            "writeTelemetryLogs",
            true,
            "Write JSONL telemetry logs under BepInEx/config/comfy-network-sense.");
  }
}
