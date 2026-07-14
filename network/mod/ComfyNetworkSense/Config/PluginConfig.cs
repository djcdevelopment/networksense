namespace ComfyNetworkSense;

using BepInEx.Configuration;

using UnityEngine;

public static class PluginConfig {
  public static ConfigEntry<bool> IsModEnabled { get; private set; }
  public static ConfigEntry<bool> ShowHudOnStart { get; private set; }
  public static ConfigEntry<bool> WriteTelemetryLogs { get; private set; }
  public static ConfigEntry<float> LiveSampleIntervalSeconds { get; private set; }
  public static ConfigEntry<float> ServerPulseIntervalSeconds { get; private set; }
  public static ConfigEntry<bool> ServerHeartbeatWorldZdoCountEnabled { get; private set; }
  public static ConfigEntry<float> ServerHeartbeatWorldZdoCountIntervalSeconds { get; private set; }
  public static ConfigEntry<float> NearbyRadiusMeters { get; private set; }
  public static ConfigEntry<float> BuildScanRadiusMeters { get; private set; }
  public static ConfigEntry<bool> SceneScanEnabled { get; private set; }
  public static ConfigEntry<float> SceneScanIntervalSeconds { get; private set; }
  public static ConfigEntry<float> BenchmarkDurationSeconds { get; private set; }
  public static ConfigEntry<bool> PerfProbeEnabled { get; private set; }
  public static ConfigEntry<float> PerfHitchThresholdMs { get; private set; }
  public static ConfigEntry<float> PerfSevereHitchThresholdMs { get; private set; }
  public static ConfigEntry<float> PerfSectionWarnThresholdMs { get; private set; }
  public static ConfigEntry<bool> PerfEngineLogProbeEnabled { get; private set; }
  public static ConfigEntry<bool> PerfWorldZdoCountOnSevereHitchEnabled { get; private set; }
  public static ConfigEntry<float> PerfSampleIntervalSeconds { get; private set; }
  public static ConfigEntry<bool> PortalConnectionCacheEnabled { get; private set; }
  public static ConfigEntry<float> PortalConnectionCacheIntervalSeconds { get; private set; }
  public static ConfigEntry<float> PortalConnectionCacheLogIntervalSeconds { get; private set; }
  public static ConfigEntry<bool> SpawnerConnectionCacheEnabled { get; private set; }
  public static ConfigEntry<bool> AutoRehearsalEnabled { get; private set; }
  public static ConfigEntry<string> AutoRehearsalRouteFile { get; private set; }
  public static ConfigEntry<string> AutoRehearsalProfile { get; private set; }
  public static ConfigEntry<float> AutoRehearsalDelaySeconds { get; private set; }
  public static ConfigEntry<bool> AutoRehearsalRunOncePerSession { get; private set; }
  public static ConfigEntry<bool> CoupleAutoRehearsalToNetcodeProbe { get; private set; }
  public static ConfigEntry<bool> RouteGodFlySafeguard { get; private set; }
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
  public static ConfigEntry<string> LumberjacksGatewayUrl { get; private set; }
  public static ConfigEntry<string> LumberjacksCutoverMode { get; private set; }
  public static ConfigEntry<string> LumberjacksEnrollmentManifestId { get; private set; }
  public static ConfigEntry<bool> ZdoAuthoritativeConsumerEnabled { get; private set; }
  public static ConfigEntry<bool> LumberjacksTelemetryHeartbeatEnabled { get; private set; }
  public static ConfigEntry<float> LumberjacksTelemetryHeartbeatIntervalSeconds { get; private set; }
  public static ConfigEntry<string> LumberjacksTelemetryKey { get; private set; }
  public static ConfigEntry<string> LumberjacksRegionId { get; private set; }
  public static ConfigEntry<int> LumberjacksProbeInputCount { get; private set; }
  public static ConfigEntry<float> LumberjacksProjectionInputHz { get; private set; }
  public static ConfigEntry<float> LumberjacksProjectionScale { get; private set; }
  public static ConfigEntry<float> LumberjacksProjectionAnchorMeters { get; private set; }
  public static ConfigEntry<int> LumberjacksProjectionMaxEntities { get; private set; }
  public static ConfigEntry<bool> LumberjacksProjectionDriveInputs { get; private set; }
  public static ConfigEntry<bool> LumberjacksProjectionLabelsEnabled { get; private set; }
  public static ConfigEntry<float> LumberjacksShadowInputHz { get; private set; }
  public static ConfigEntry<float> LumberjacksShadowLogIntervalSeconds { get; private set; }
  public static ConfigEntry<float> LumberjacksShadowMaxValheimSpeedMetersPerSecond { get; private set; }
  public static ConfigEntry<float> LumberjacksShadowLumberjacksMaxUnitsPerSecond { get; private set; }
  public static ConfigEntry<float> LumberjacksShadowMinMoveMetersPerSecond { get; private set; }
  public static ConfigEntry<float> LumberjacksPriorityProbeRadiusMeters { get; private set; }
  public static ConfigEntry<float> LumberjacksPriorityProbeIntervalSeconds { get; private set; }
  public static ConfigEntry<int> LumberjacksPriorityProbeMaxObjectsPerSample { get; private set; }
  public static ConfigEntry<string> LumberjacksEventLogUrl { get; private set; }
  public static ConfigEntry<bool> NetcodeProbeAutoStartEnabled { get; private set; }
  public static ConfigEntry<float> NetcodeProbeAutoStartDelaySeconds { get; private set; }
  public static ConfigEntry<float> NetcodeProbeAutoStopSeconds { get; private set; }
  public static ConfigEntry<int> NetcodeProbeMaxDetailRows { get; private set; }
  public static ConfigEntry<bool> OwnershipObserveEnabled { get; private set; }
  public static ConfigEntry<bool> OwnershipPinEnabled { get; private set; }
  public static ConfigEntry<int> OwnershipPinAutoCaptureMax { get; private set; }
  public static ConfigEntry<string> OwnershipPinPrefabs { get; private set; }
  public static ConfigEntry<bool> ZdoRedirectEnabled { get; private set; }
  public static ConfigEntry<string> ZdoRedirectPrefabs { get; private set; }
  public static ConfigEntry<string> ZdoRedirectEndpoint { get; private set; }
  public static ConfigEntry<string> ZdoRedirectWindowId { get; private set; }
  public static ConfigEntry<float> ZdoRedirectActiveSeconds { get; private set; }
  public static ConfigEntry<bool> ZdoInjectionEnabled { get; private set; }
  public static ConfigEntry<string> ZdoInjectionPrefabs { get; private set; }
  public static ConfigEntry<string> ZdoInjectionEndpoint { get; private set; }
  public static ConfigEntry<string> ZdoInjectionWindowId { get; private set; }
  public static ConfigEntry<string> ZdoInjectionClientId { get; private set; }
  public static ConfigEntry<string> ZdoInjectionAuthorityId { get; private set; }
  public static ConfigEntry<float> ZdoInjectionPollSeconds { get; private set; }
  public static ConfigEntry<float> ZdoInjectionActiveSeconds { get; private set; }
  public static ConfigEntry<bool> HandshakeResponderEnabled { get; private set; }
  public static ConfigEntry<string> HandshakeResponderEndpoint { get; private set; }
  public static ConfigEntry<string> HandshakeResponderWindowId { get; private set; }
  public static ConfigEntry<float> HandshakeResponderPollSeconds { get; private set; }
  public static ConfigEntry<float> HandshakeResponderActiveSeconds { get; private set; }
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

    ServerHeartbeatWorldZdoCountEnabled =
        config.Bind(
            "Sampling",
            "serverHeartbeatWorldZdoCountEnabled",
            false,
            "Include ZDOMan.NrOfObjects() in server heartbeat rows. This can be very expensive on massive saves; keep disabled for hitch isolation.");

    ServerHeartbeatWorldZdoCountIntervalSeconds =
        config.Bind(
            "Sampling",
            "serverHeartbeatWorldZdoCountIntervalSeconds",
            300.0f,
            "Minimum seconds between world ZDO count refreshes when serverHeartbeatWorldZdoCountEnabled is true.");

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

    SceneScanEnabled =
        config.Bind(
            "Sampling",
            "sceneScanEnabled",
            true,
            "Capture nearby entity/build-piece counts. Disable for perf isolation runs.");

    SceneScanIntervalSeconds =
        config.Bind(
            "Sampling",
            "sceneScanIntervalSeconds",
            2.0f,
            "Minimum seconds between local scene scans for nearby entity/build-piece counts.");

    BenchmarkDurationSeconds =
        config.Bind(
            "Benchmark",
            "benchmarkDurationSeconds",
            15.0f,
            "How long a benchmark run lasts before auto-completing.");

    PerfProbeEnabled =
        config.Bind(
            "Perf",
            "perfProbeEnabled",
            true,
            "Write perf-hitches.jsonl, perf-sections.jsonl, and perf-engine-log.jsonl for main-thread hitch investigation.");

    PerfHitchThresholdMs =
        config.Bind(
            "Perf",
            "hitchThresholdMs",
            250.0f,
            "Frame duration threshold for writing perf-hitches.jsonl.");

    PerfSevereHitchThresholdMs =
        config.Bind(
            "Perf",
            "severeHitchThresholdMs",
            2000.0f,
            "Frame duration threshold for tagging a hitch as severe.");

    PerfSectionWarnThresholdMs =
        config.Bind(
            "Perf",
            "sectionWarnThresholdMs",
            25.0f,
            "Section duration threshold for writing perf-sections.jsonl.");

    PerfEngineLogProbeEnabled =
        config.Bind(
            "Perf",
            "engineLogProbeEnabled",
            true,
            "Count Unity/BepInEx log messages using Application.logMessageReceivedThreaded.");

    PerfWorldZdoCountOnSevereHitchEnabled =
        config.Bind(
            "Perf",
            "worldZdoCountOnSevereHitchEnabled",
            false,
            "Call ZDOMan.NrOfObjects() when writing severe hitch rows. This can amplify hitches on massive saves; keep disabled unless explicitly testing world-count cost.");

    PerfSampleIntervalSeconds =
        config.Bind(
            "Perf",
            "perfSampleIntervalSeconds",
            1.0f,
            "Minimum seconds between perf-engine-log.jsonl aggregate rows.");

    PortalConnectionCacheEnabled =
        config.Bind(
            "PortalFix",
            "portalConnectionCacheEnabled",
            true,
            "Replace Valheim's server portal connection scan with a cached tag lookup. Intended for massive portal-count lab worlds.");

    PortalConnectionCacheIntervalSeconds =
        config.Bind(
            "PortalFix",
            "portalConnectionCacheIntervalSeconds",
            5.0f,
            "Seconds between cached server portal connection passes.");

    PortalConnectionCacheLogIntervalSeconds =
        config.Bind(
            "PortalFix",
            "portalConnectionCacheLogIntervalSeconds",
            60.0f,
            "Minimum seconds between cached portal-loop summary log rows. Set to 0 to disable summary logging.");

    SpawnerConnectionCacheEnabled =
        config.Bind(
            "SpawnerFix",
            "spawnerConnectionCacheEnabled",
            true,
            "Replace Valheim's spawner connection pass with an O(n) cached hash lookup. Intended for massive spawned-ZDO lab worlds.");

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

    CoupleAutoRehearsalToNetcodeProbe =
        config.Bind(
            "Automation",
            "coupleAutoRehearsalToNetcodeProbe",
            false,
            "When the netcode probe auto-starts on a client, also trigger the automatic route rehearsal so captured ZDO traffic exists without a human hand-walking the route. Requires a local player; skipped headless on the dedicated server. Intended for private lab clients only.");

    RouteGodFlySafeguard =
        config.Bind(
            "Automation",
            "routeGodFlySafeguard",
            true,
            "Before any teleport route or rehearsal walk starts, enable god mode + debug-fly on the local player so post-teleport falls cannot kill the character mid-walk. Calls the Player API directly rather than the 'god'/'fly' console commands, which are cheat-gated (IsCheatsEnabled() returns ZNet.IsServer(), false on a client joined to a dedicated server) and would be rejected client-side. No-op headless.");

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

    LumberjacksGatewayUrl =
        config.Bind(
            "Lumberjacks",
            "lumberjacksGatewayUrl",
            "ws://127.0.0.1:4000",
            "Lumberjacks Gateway WebSocket URL used by network_sense_lumberjacks_probe. Overridden by COMFY_LUMBERJACKS_GATEWAY_URL.");

    LumberjacksCutoverMode =
        config.Bind(
            "Lumberjacks",
            "lumberjacksCutoverMode",
            "native",
            "Declared gameplay-network mode: native, mirrored, or lumberjacks-primary. This is a declaration for telemetry and rollout control; primary mode must not be enabled until the coverage gate passes.");

    LumberjacksEnrollmentManifestId =
        config.Bind(
            "Lumberjacks",
            "lumberjacksEnrollmentManifestId",
            "",
            "Enrollment manifest revision assigned by the Lumberjacks control plane. Empty means this installation is not enrolled.");

    ZdoAuthoritativeConsumerEnabled = config.Bind(
        "Lumberjacks", "zdoAuthoritativeConsumerEnabled", false,
        "Apply queued Lumberjacks ZDO envelopes through RPC_ZDOData. Requires an explicit authoritative window.");

    LumberjacksTelemetryHeartbeatEnabled =
        config.Bind("Lumberjacks", "lumberjacksTelemetryHeartbeatEnabled", true,
            "Publish an identity-free server heartbeat to the Lumberjacks Gateway from dedicated servers.");

    LumberjacksTelemetryHeartbeatIntervalSeconds =
        config.Bind("Lumberjacks", "lumberjacksTelemetryHeartbeatIntervalSeconds", 5.0f,
            "Seconds between aggregate Lumberjacks telemetry heartbeats.");

    LumberjacksTelemetryKey =
        config.Bind("Lumberjacks", "lumberjacksTelemetryKey", "",
            "Optional shared key sent as X-Lumberjacks-Telemetry-Key for heartbeat ingestion.");

    LumberjacksRegionId =
        config.Bind(
            "Lumberjacks",
            "lumberjacksRegionId",
            "region-spawn",
            "Lumberjacks region id used by network_sense_lumberjacks_probe unless supplied on the console command.");

    LumberjacksProbeInputCount =
        config.Bind(
            "Lumberjacks",
            "lumberjacksProbeInputCount",
            12,
            "Number of JSON player_input messages sent by network_sense_lumberjacks_probe after joining a Lumberjacks region.");

    LumberjacksProjectionInputHz =
        config.Bind(
            "Lumberjacks",
            "lumberjacksProjectionInputHz",
            6.0f,
            "How often network_sense_lumberjacks_projection sends JSON player_input while driveInputs is enabled.");

    LumberjacksProjectionScale =
        config.Bind(
            "Lumberjacks",
            "lumberjacksProjectionScale",
            1.0f,
            "Scale from Lumberjacks region coordinates into local Valheim proxy marker offsets.");

    LumberjacksProjectionAnchorMeters =
        config.Bind(
            "Lumberjacks",
            "lumberjacksProjectionAnchorMeters",
            8.0f,
            "Meters in front of the local Valheim player where Lumberjacks proxy projection is anchored.");

    LumberjacksProjectionMaxEntities =
        config.Bind(
            "Lumberjacks",
            "lumberjacksProjectionMaxEntities",
            64,
            "Maximum number of local-only Lumberjacks proxy markers to keep in the Valheim scene.");

    LumberjacksProjectionDriveInputs =
        config.Bind(
            "Lumberjacks",
            "lumberjacksProjectionDriveInputs",
            true,
            "When true, network_sense_lumberjacks_projection sends lightweight player_input so entity_update rows continue flowing.");

    LumberjacksProjectionLabelsEnabled =
        config.Bind(
            "Lumberjacks",
            "lumberjacksProjectionLabelsEnabled",
            true,
            "Show small TextMesh labels above local-only Lumberjacks proxy markers.");

    LumberjacksShadowInputHz =
        config.Bind(
            "Lumberjacks",
            "lumberjacksShadowInputHz",
            20.0f,
            "How often network_sense_lumberjacks_shadow samples Valheim local-player motion and sends derived player_input.");

    LumberjacksShadowLogIntervalSeconds =
        config.Bind(
            "Lumberjacks",
            "lumberjacksShadowLogIntervalSeconds",
            2.0f,
            "How often network_sense_lumberjacks_shadow writes rolling drift sample rows.");

    LumberjacksShadowMaxValheimSpeedMetersPerSecond =
        config.Bind(
            "Lumberjacks",
            "lumberjacksShadowMaxValheimSpeedMetersPerSecond",
            8.0f,
            "Valheim horizontal speed mapped to 100 percent Lumberjacks player_input for shadow authority comparison.");

    LumberjacksShadowLumberjacksMaxUnitsPerSecond =
        config.Bind(
            "Lumberjacks",
            "lumberjacksShadowLumberjacksMaxUnitsPerSecond",
            200.0f,
            "Lumberjacks authoritative units per second at 100 percent input, used only to scale drift metrics back to Valheim meters.");

    LumberjacksShadowMinMoveMetersPerSecond =
        config.Bind(
            "Lumberjacks",
            "lumberjacksShadowMinMoveMetersPerSecond",
            0.03f,
            "Minimum sampled Valheim horizontal speed in meters per second before shadow authority sends moving input instead of idle.");

    LumberjacksPriorityProbeRadiusMeters =
        config.Bind(
            "Lumberjacks",
            "lumberjacksPriorityProbeRadiusMeters",
            96.0f,
            "Radius used by network_sense_lumberjacks_priority_probe and priority_route when classifying loaded local Valheim objects.");

    LumberjacksPriorityProbeIntervalSeconds =
        config.Bind(
            "Lumberjacks",
            "lumberjacksPriorityProbeIntervalSeconds",
            5.0f,
            "Seconds between priority/load-order scans while network_sense_lumberjacks_priority_probe is running.");

    LumberjacksPriorityProbeMaxObjectsPerSample =
        config.Bind(
            "Lumberjacks",
            "lumberjacksPriorityProbeMaxObjectsPerSample",
            96,
            "Maximum per-object priority rows emitted per scan. Summary rows still include full scanned counts.");

    LumberjacksEventLogUrl =
        config.Bind(
            "Lumberjacks",
            "lumberjacksEventLogUrl",
            "http://127.0.0.1:4002",
            "Lumberjacks EventLog base URL used by live priority mirror commands.");

    NetcodeProbeAutoStartEnabled =
        config.Bind(
            "Netcode",
            "netcodeProbeAutoStartEnabled",
            false,
            "Automatically start the rung I1 ZDO netcode reachability probe once at least one network peer is connected. Works headless on both the dedicated server and clients (no local player required). Intended for private lab runs.");

    NetcodeProbeAutoStartDelaySeconds =
        config.Bind(
            "Netcode",
            "netcodeProbeAutoStartDelaySeconds",
            25.0f,
            "Seconds to wait after the first peer connects before the netcode probe auto-starts.");

    NetcodeProbeAutoStopSeconds =
        config.Bind(
            "Netcode",
            "netcodeProbeAutoStopSeconds",
            150.0f,
            "If greater than 0, automatically stop the netcode probe this many seconds after it auto-starts, writing the lifecycle counters row. 0 = run until shutdown.");

    NetcodeProbeMaxDetailRows =
        config.Bind(
            "Netcode",
            "netcodeProbeMaxDetailRows",
            5000,
            "Maximum per-ZDO detail rows written to netcode-probe.jsonl per auto-started run. Aggregate counters keep counting past the cap.");

    OwnershipObserveEnabled =
        config.Bind(
            "Netcode",
            "ownershipObserveEnabled",
            false,
            "P3/I2 observe-only ownership-churn logger. When on, server-side Harmony postfixes on ZDO.SetOwner / SetOwnerInternal record every ownership change (uid, old/new owner, revision, sector, via, is_server) to ownership-churn.jsonl for the window a netcode probe run is active. Changes NO gameplay - pure measurement for the ownership-seizure (pin) work; off = no observation, zero cost (rollback flag). Intended for private lab runs on the dedicated server.");

    OwnershipPinEnabled =
        config.Bind(
            "Netcode",
            "ownershipPinEnabled",
            false,
            "P3/I2 ownership-seizure PIN. BEHAVIOUR-CHANGING (server-side/am4 only), rollback flag. "
            + "When on, server-side Harmony prefixes on ZDO.SetOwner / SetOwnerInternal SKIP the "
            + "vanilla ownership transfer on a small auto-captured set of ZDOs, scoped to exactly "
            + "the two churn funnels (ReleaseNearbyZDOS release/reseize + RPC_ZDOData remote-apply), "
            + "so a pinned ZDO's owner does not transfer on zone entry. It writes NO synthetic "
            + "owners/revisions - it only skips vanilla owner changes; uncaptured ZDOs transfer "
            + "normally (the negative control). off = vanilla behaviour, zero cost. Requires "
            + "ownershipObserveEnabled for the paired per-transfer negative-control detail. Intended "
            + "for private lab runs on the dedicated server; coupled to the netcode-probe window.");

    OwnershipPinAutoCaptureMax =
        config.Bind(
            "Netcode",
            "ownershipPinAutoCaptureMax",
            25,
            "Maximum number of ZDOs the pin will auto-capture (and then hold) in a run. Keeps the "
            + "pin test-scoped on the ~9M-ZDO world: the first N owned ZDOs the churn funnel tries "
            + "to transfer are pinned; every ZDO past the cap transfers normally.");

    OwnershipPinPrefabs =
        config.Bind(
            "Netcode",
            "ownershipPinPrefabs",
            "",
            "Optional comma-separated prefab name allowlist for the pin (e.g. 'Beech1,Rock_4'). "
            + "When set, only ZDOs whose prefab is in the list are eligible for auto-capture "
            + "(targeted test). Blank = any prefab is eligible (broad test). Names are matched by "
            + "stable hash, so no ZNetScene lookup is needed.");

    ZdoRedirectEnabled =
        config.Bind(
            "Netcode",
            "zdoRedirectEnabled",
            false,
            "P4/I3 outbound REDIRECT. BEHAVIOUR-CHANGING (server-side/am4 only), rollback flag. "
            + "When on, a server-side Harmony postfix on ZDOMan.CreateSyncList removes "
            + "allowlisted-prefab ZDOs from the native send list (replicating the native per-peer "
            + "ack so revision bookkeeping stays exact) and posts the wire-equivalent payload to "
            + "the Lumberjacks gateway instead. Writes no persisted ZDO state (send path is "
            + "runtime bookkeeping only). Non-tagged ZDOs sync normally (the negative control). "
            + "REFUSES to arm with an empty prefab allowlist. off = vanilla behaviour, zero cost. "
            + "Intended for private lab runs on the dedicated server; coupled to the "
            + "netcode-probe window.");

    ZdoRedirectPrefabs =
        config.Bind(
            "Netcode",
            "zdoRedirectPrefabs",
            "",
            "Comma-separated prefab name allowlist for the redirect (e.g. 'Beech1'). REQUIRED — "
            + "unlike the pin, blank does NOT mean 'any': suppressing every ZDO would freeze "
            + "world sync for the client, so an empty list refuses to start. Names are matched "
            + "by stable hash.");

    ZdoRedirectEndpoint =
        config.Bind(
            "Netcode",
            "zdoRedirectEndpoint",
            "http://100.124.12.37:4000",
            "Base URL of the Lumberjacks gateway (OMEN tailnet). The runner POSTs envelope "
            + "batches to <endpoint>/valheim/zdo-redirect/receipts. The gateway must be launched "
            + "with a 0.0.0.0 bind (its appsettings default is localhost-only).");

    ZdoRedirectWindowId =
        config.Bind(
            "Netcode",
            "zdoRedirectWindowId",
            "",
            "Window id stamped on every envelope + local row (the gate join key against the "
            + "gateway's per-window receipt counters). Blank = auto 'i3-<utc timestamp>' per run.");

    ZdoRedirectActiveSeconds =
        config.Bind(
            "Netcode",
            "zdoRedirectActiveSeconds",
            90.0f,
            "Suppression sub-window in seconds. Set SHORTER than the netcode-probe window and the "
            + "redirect auto-disarms mid-capture, so the still-running probe records native sends "
            + "of the tagged prefab RESUMING — the in-window rollback rehearsal (P4 step 11), "
            + "hands-free. 0 = suppress for the whole probe window.");

    ZdoInjectionEnabled =
        config.Bind(
            "Netcode",
            "zdoInjectionEnabled",
            false,
            "P5/I4 inbound injection rollback flag. Client-side only and coupled to the finite "
            + "netcode-probe window. Polls Lumberjacks for a bounded synthetic fixture, rebuilds "
            + "the vanilla ZDOData wire package, and applies it through RPC_ZDOData. off = no "
            + "polling or state changes.");

    ZdoInjectionPrefabs =
        config.Bind(
            "Netcode",
            "zdoInjectionPrefabs",
            "",
            "Required comma-separated prefab allowlist for inbound synthetic fixtures (for "
            + "example 'Wood'). Empty refuses to arm.");

    ZdoInjectionEndpoint =
        config.Bind(
            "Netcode",
            "zdoInjectionEndpoint",
            "http://127.0.0.1:4000",
            "Lumberjacks gateway base URL. The rendered OMEN client polls the local gateway.");

    ZdoInjectionWindowId =
        config.Bind(
            "Netcode",
            "zdoInjectionWindowId",
            "",
            "P5 gate window id. Blank generates i4-<utc timestamp>; gate staging should set it.");

    ZdoInjectionClientId =
        config.Bind(
            "Netcode",
            "zdoInjectionClientId",
            "omen",
            "Client id used for Lumberjacks poll/ack bookkeeping.");

    ZdoInjectionAuthorityId =
        config.Bind(
            "Netcode",
            "zdoInjectionAuthorityId",
            "5497853135698",
            "Non-zero Int64 reserved for the synthetic Lumberjacks ZDO uid/owner namespace. "
            + "Commands outside this namespace are rejected before package construction.");

    ZdoInjectionPollSeconds =
        config.Bind(
            "Netcode",
            "zdoInjectionPollSeconds",
            1.0f,
            "Seconds between off-thread gateway polls; clamped to at least 0.25 seconds.");

    ZdoInjectionActiveSeconds =
        config.Bind(
            "Netcode",
            "zdoInjectionActiveSeconds",
            90.0f,
            "Injection sub-window seconds. Shorter than the probe window provides automatic "
            + "rollback rehearsal. 0 runs until the probe stops.");

    HandshakeResponderEnabled =
        config.Bind(
            "Netcode",
            "handshakeResponderEnabled",
            false,
            "P6/I5 server-side handshake responder rollback flag (am4 only). When armed, a Harmony "
            + "hook answers RPC_ServerHandshake/RPC_PeerInfo with decisions supplied by the "
            + "Lumberjacks handshake responder (Steam-only per I6). off = pure pass-through; an "
            + "empty endpoint also refuses to arm. Fail-safe default OFF. NB: the RPC interceptor "
            + "is built + wire-validated at the armed session (first real-client bytes in-game).");

    HandshakeResponderEndpoint =
        config.Bind(
            "Netcode",
            "handshakeResponderEndpoint",
            "http://127.0.0.1:4000",
            "Lumberjacks gateway base URL the am4 responder polls for the emulated server context "
            + "(needPassword/salt/ban/full/password/dup). Empty refuses to arm.");

    HandshakeResponderWindowId =
        config.Bind(
            "Netcode",
            "handshakeResponderWindowId",
            "",
            "P6/I5 gate window id. Blank generates i5-<utc timestamp>; gate staging sets it.");

    HandshakeResponderPollSeconds =
        config.Bind(
            "Netcode",
            "handshakeResponderPollSeconds",
            2.0f,
            "Seconds between off-thread context polls of the Lumberjacks responder; clamped to at "
            + "least 0.5 seconds. Server-side HTTP uses a raw TcpClient POST (Mono trap, ADR 0003).");

    HandshakeResponderActiveSeconds =
        config.Bind(
            "Netcode",
            "handshakeResponderActiveSeconds",
            90.0f,
            "Handshake responder sub-window seconds; shorter than the probe window gives an "
            + "in-window rollback rehearsal. 0 runs until the probe stops.");

    WriteTelemetryLogs =
        config.Bind(
            "Logging",
            "writeTelemetryLogs",
            true,
            "Write JSONL telemetry logs under BepInEx/config/comfy-network-sense.");
  }
}
