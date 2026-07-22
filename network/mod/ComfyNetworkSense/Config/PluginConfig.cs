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
  public static ConfigEntry<bool> RouteGodFlySafeguard { get; private set; }
  public static ConfigEntry<bool> AutoPortOnJoinEnabled { get; private set; }
  public static ConfigEntry<float> AutoPortDelaySeconds { get; private set; }
  public static ConfigEntry<float> AutoPortHeightMeters { get; private set; }
  public static ConfigEntry<string> LumberjacksGatewayUrl { get; private set; }
  public static ConfigEntry<string> LumberjacksCutoverMode { get; private set; }
  public static ConfigEntry<string> LumberjacksEnrollmentManifestId { get; private set; }
  public static ConfigEntry<string> LumberjacksAuthoritativeWindowId { get; private set; }
  public static ConfigEntry<string> LumberjacksEnrollmentId { get; private set; }
  public static ConfigEntry<bool> ZdoAuthoritativeConsumerEnabled { get; private set; }
  public static ConfigEntry<bool> LumberjacksTelemetryHeartbeatEnabled { get; private set; }
  public static ConfigEntry<float> LumberjacksTelemetryHeartbeatIntervalSeconds { get; private set; }
  public static ConfigEntry<string> LumberjacksTelemetryKey { get; private set; }
  public static ConfigEntry<string> LumberjacksClientAccessKey { get; private set; }
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
  public static ConfigEntry<int> NetcodeProbeMaxDetailRows { get; private set; }
  public static ConfigEntry<bool> ZdoRedirectEnabled { get; private set; }
  public static ConfigEntry<string> ZdoRedirectPrefabs { get; private set; }
  public static ConfigEntry<string> ZdoRedirectEndpoint { get; private set; }
  public static ConfigEntry<string> ZdoRedirectWindowId { get; private set; }
  public static ConfigEntry<float> ZdoRedirectActiveSeconds { get; private set; }
  public static ConfigEntry<int> ZdoRedirectMaxPriorityRank { get; private set; }
  public static ConfigEntry<string> ZdoLandmarkReach { get; private set; }
  public static ConfigEntry<bool> GameplayEventProducerEnabled { get; private set; }
  public static ConfigEntry<bool> GameplayEventVerboseLogging { get; private set; }
  public static ConfigEntry<bool> QuestEvaluatorEnabled { get; private set; }
  public static ConfigEntry<bool> ZdoBandShapingEnabled { get; private set; }
  public static ConfigEntry<float> ZdoInnerRadiusMeters { get; private set; }
  public static ConfigEntry<float> ZdoOuterRadiusMeters { get; private set; }
  public static ConfigEntry<float> ZdoThinHz { get; private set; }
  public static ConfigEntry<bool> ZdoCoPresenceShadowEnabled { get; private set; }
  public static ConfigEntry<bool> ZdoCoPresenceFanoutEnabled { get; private set; }
  public static ConfigEntry<bool> HandshakeResponderEnabled { get; private set; }
  public static ConfigEntry<bool> HandshakeResponderStrictMode { get; private set; }
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

    RouteGodFlySafeguard =
        config.Bind(
            "Automation",
            "routeGodFlySafeguard",
            true,
            "Before any teleport route or rehearsal walk starts, enable god mode + debug-fly on the local player so post-teleport falls cannot kill the character mid-walk. Calls the Player API directly rather than the 'god'/'fly' console commands, which are cheat-gated (IsCheatsEnabled() returns ZNet.IsServer(), false on a client joined to a dedicated server) and would be rejected client-side. No-op headless.");

    AutoPortOnJoinEnabled =
        config.Bind(
            "Automation",
            "autoPortOnJoinEnabled",
            false,
            "CLIENT opt-in test harness: when true, on joining a server this client waits "
            + "autoPortDelaySeconds, then teleports itself to the world's densest ZDO region (the "
            + "server computes it and pushes the coordinate), with god + debug-fly enabled, "
            + "autoPortHeightMeters off the ground. Replaces the removed swarm-harness auto-port. "
            + "OFF by default and per-client, so it only ever moves the operator who opted in — a "
            + "real joining player with the default config is never teleported. No-op headless.");

    AutoPortDelaySeconds =
        config.Bind(
            "Automation",
            "autoPortDelaySeconds",
            20.0f,
            "Seconds after join before the auto-port fires (autoPortOnJoinEnabled). Gives the world "
            + "and local player time to finish loading so the teleport lands cleanly.");

    AutoPortHeightMeters =
        config.Bind(
            "Automation",
            "autoPortHeightMeters",
            40.0f,
            "Height in meters above the densest region's ground the auto-port drops you at "
            + "(autoPortOnJoinEnabled) — airborne with debug-fly so you don't land stuck in trees/builds.");

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

    LumberjacksAuthoritativeWindowId =
        config.Bind("Lumberjacks", "lumberjacksAuthoritativeWindowId", "",
            "Shared authoritative queue/window id. Falls back to lumberjacksEnrollmentManifestId for older installations.");

    LumberjacksEnrollmentId =
        config.Bind("Lumberjacks", "lumberjacksEnrollmentId", "",
            "Per-player enrollment id issued after Steam invite redemption. This is not the authoritative queue id.");

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

    LumberjacksClientAccessKey =
        config.Bind("Lumberjacks", "lumberjacksClientAccessKey", "",
            "Optional shared client key sent to authenticated volunteer Gateway endpoints. Keep empty for the private OMEN tunnel.");

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

    NetcodeProbeMaxDetailRows =
        config.Bind(
            "Netcode",
            "netcodeProbeMaxDetailRows",
            5000,
            "Maximum per-ZDO detail rows written to netcode-probe.jsonl per auto-started run. Aggregate counters keep counting past the cap.");

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
            + "THIS IS THE LIVE SERVING PATH in lumberjacks-primary mode, not a lab experiment - the "
            + "description said 'intended for private lab runs' long after it began carrying "
            + "production traffic. It still defaults OFF deliberately: off IS the standing rollback, "
            + "so a mod dropped into any server changes nothing until armed.");

    ZdoRedirectPrefabs =
        config.Bind(
            "Netcode",
            "zdoRedirectPrefabs",
            "",
            "Comma-separated prefab name allowlist for the redirect (e.g. 'Beech1'). REQUIRED — "
            + "unlike the pin, blank does NOT mean 'any': suppressing every ZDO would freeze "
            + "world sync for the client, so an empty list refuses to start. The explicit value "
            + "'*' selects all prefabs and must only be armed behind the authoritative coverage "
            + "gate. Names are otherwise matched by stable hash.");

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
            0.0f,
            "0 = no cap, which is the production posture (infra/gcp/p7/README.md pins it). Defaulted "
            + "to 90 until 2026-07-21, which meant a VM configured from defaults would silently "
            + "auto-disarm the redirect 90s into a session and resume native sync - the safe value "
            + "lived only in a runbook. The finite window is now opt-in. "
            + "Suppression sub-window in seconds. Set SHORTER than the netcode-probe window and the "
            + "redirect auto-disarms mid-capture, so the still-running probe records native sends "
            + "of the tagged prefab RESUMING — the in-window rollback rehearsal (P4 step 11), "
            + "hands-free. 0 = suppress for the whole probe window.");

    ZdoRedirectMaxPriorityRank =
        config.Bind(
            "Netcode",
            "zdoRedirectMaxPriorityRank",
            6,
            "Final Importance gate for the Lumberjacks network boundary. A candidate survives "
            + "only when LumberjacksPriorityClassifier assigns a rank at or below this value. "
            + "Rejected candidates remain in Valheim's native send list and never enter the "
            + "Gateway payload. 6 preserves the prior all-tier behavior; 4 admits the proven "
            + "interactive/structural/storage lanes while leaving support/decorative work native.");

    ZdoLandmarkReach =
        config.Bind(
            "Netcode",
            "zdoLandmarkReach",
            "",
            "Landmark reach map: comma-separated 'prefabName=reachMeters' entries granting an object "
            + "long-range PRESENCE (landmark-reach-design.md). A landmark is admitted at the redirect "
            + "boundary when the observer is within its reach EVEN IF its priority rank exceeds "
            + "zdoRedirectMaxPriorityRank — the parallel reliable-lane exemption. Blank = no landmarks "
            + "(rank gate alone decides). Names matched by stable hash, same as zdoRedirectPrefabs; "
            + "entries with a missing or non-positive reach are ignored. A landmark is static, so this "
            + "adds no per-tick datagram cost — it only widens what redirect admits, once, at arrival.");

    GameplayEventProducerEnabled =
        config.Bind(
            "Gameplay",
            "gameplayEventProducerEnabled",
            false,
            "Client-side gameplay-event telemetry (community-telemetry gameplay seam). OFF (default) "
            + "= no combat hook emits; the Character.Damage/OnDeath postfixes are no-ops and combat is "
            + "byte-for-byte untouched. ON = the client (which owns the creature it fights) observes "
            + "first hits and killing blows (attributed to the acting player) and relays them to the "
            + "server over the ComfyNetworkSense_GameplayEvent routed RPC; the server POSTs first_hit / "
            + "killing_blow / weapon_used to the gateway (POST /valheim/events), which drives the public "
            + "/api/v0/telemetry/events feed and the durable EventLog. Hot-reloadable.");

    GameplayEventVerboseLogging =
        config.Bind(
            "Gameplay",
            "gameplayEventVerboseLogging",
            false,
            "Per-hit [gp] diagnostic logging for the gameplay-event seam. OFF (default) = only discrete "
            + "lifecycle lines are logged (RPC registered, event sent, server received). ON = also log "
            + "every damage hook and death the producer sees — a firehose during combat, useful only "
            + "when debugging why an event did or didn't emit. Hot-reloadable.");

    QuestEvaluatorEnabled =
        config.Bind(
            "Gameplay",
            "questEvaluatorEnabled",
            false,
            "Quest evaluator (community-telemetry quest track, consumes the gameplay-event seam). OFF "
            + "(default) = tracked quests loaded from quest-view.json are never matched and no "
            + "quest_completed events emit; the gameplay producer is unaffected. ON = classified "
            + "gameplay events (first_hit / killing_blow) are matched against the player's quest-view.json "
            + "and a match relays a quest_completed event to the server over the same routed RPC. The "
            + "quest-view.json itself always loads (independent of this flag); this only gates matching. "
            + "Hot-reloadable.");

    ZdoBandShapingEnabled =
        config.Bind(
            "Netcode",
            "zdoBandShapingEnabled",
            false,
            "Master switch for distance-band AoI rate-shaping of the ZDO redirect (aoi-baseline-"
            + "20260721). OFF (default) = today's behaviour: every admitted ZDO is redirected on every "
            + "sync pass. ON = shape by the observing peer's distance: near (0..inner) full rate, mid "
            + "(inner..outer) thinned to zdoThinHz, far (>outer) dropped, landmarks (zdoLandmarkReach) "
            + "always emitted. Hot-reloadable — flip it off to revert an armed redirect to 1:1 live.");

    ZdoInnerRadiusMeters =
        config.Bind(
            "Netcode",
            "zdoInnerRadiusMeters",
            30.0f,
            "Inner band edge in meters (zdoBandShapingEnabled). Objects at or within this distance of "
            + "the observing peer are redirected every pass — where the player actually interacts. The "
            + "baseline showed the full-rate band is the whole cost, and area goes as r², so ~30m "
            + "removes most of the objects a 50m band carried.");

    ZdoOuterRadiusMeters =
        config.Bind(
            "Netcode",
            "zdoOuterRadiusMeters",
            64.0f,
            "Outer band edge in meters (zdoBandShapingEnabled). Objects beyond this are DROPPED (not "
            + "emitted, re-evaluated as the peer moves). Defaults to 64 = Valheim's zone size "
            + "(nearbyRadiusMeters/buildScanRadiusMeters), so a dropped object is outside the loaded, "
            + "active zone — both systems agree nothing is needed there.");

    ZdoThinHz =
        config.Bind(
            "Netcode",
            "zdoThinHz",
            5.0f,
            "Mid-band (inner..outer) emit rate in Hz (zdoBandShapingEnabled). The mid band is thinned "
            + "to at most this many redirects/second per (peer, object) — 20Hz→5Hz takes most of the "
            + "bandwidth saving with none of the staleness risk of dropping, since the object is still "
            + "inside the active zone. <=0 disables thinning (mid emits every pass).");

    ZdoCoPresenceShadowEnabled =
        config.Bind(
            "Netcode",
            "zdoCoPresenceShadowEnabled",
            false,
            "Phase 0 of the ownership/visibility split (ADR 0013): co-presence SHADOW. OFF (default) = "
            + "no effect. ON = for each admitted ZDO, compute which OTHER connected observers are in-band "
            + "(near/mid) for it and emit a 'copresence_shadow' telemetry row naming them — WITHOUT "
            + "changing delivery. Zero behaviour change: it only measures the fan-out a correct model "
            + "would produce, so the log names the exact shared-area ZDOs a co-located player is starved "
            + "of under today's single-recipient stamping. Hot-reloadable; arm only during a 2+ client "
            + "shadow test (it iterates all peers per admitted candidate).");

    ZdoCoPresenceFanoutEnabled =
        config.Bind(
            "Netcode",
            "zdoCoPresenceFanoutEnabled",
            false,
            "Phase 2 of the ownership/visibility split (ADR 0013): co-presence FAN-OUT. OFF (default) = "
            + "today's single-recipient delivery, byte-for-byte. ON = the one CreateSyncList pass that "
            + "sees a contended ZDO emits a read copy to EVERY in-band observer (each stamped for its "
            + "own recipient, its own m_zdos acked), so co-located players all see the same buildings/"
            + "portals — reuses Redirect, so queue/dedup/ack behaviour is unchanged. One logical "
            + "revision produces at most one redirect per recipient. Ownership is NOT changed. This "
            + "REPLACES the single-recipient path per candidate; flip it off for instant rollback. Arm "
            + "only with the shadow verified first (they share the same evaluation).");

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
            + "(needPassword/salt/ban/full/password/dup). Empty refuses to arm. https is supported "
            + "and certificate-validated; an unverifiable certificate fails the request.");

    HandshakeResponderStrictMode =
        config.Bind(
            "Netcode",
            "handshakeResponderStrictMode",
            false,
            "M1 strict admission. OFF (default) = fail-OPEN: if the gateway is unreachable or its "
            + "verdict is unparseable, the player is passed through to vanilla, so the responder can "
            + "never lock anyone out on its own fault. ON = fail-CLOSED: those same faults REJECT the "
            + "join (ErrorConnectFailed), because an authority that cannot be consulted must not be "
            + "assumed to say yes. ON means a down gateway closes the server to everyone - that is "
            + "the intended trade, and it is why this ships OFF and is flipped deliberately, with "
            + "handshakeResponderEnabled=false as the way back out. Leaving this OFF is also an "
            + "explicit, labelled operator mode: native recovery.");

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
            0.0f,
            "0 runs until the probe stops - no cap, the production posture. Defaulted to 90 until "
            + "2026-07-21, which meant new players joining after the 90s mark hit native Valheim "
            + "admission instead of the Lumberjacks responder. A shorter window still gives the "
            + "in-window rollback rehearsal, but it is now opt-in rather than the default.");

    WriteTelemetryLogs =
        config.Bind(
            "Logging",
            "writeTelemetryLogs",
            true,
            "Write JSONL telemetry logs under BepInEx/config/comfy-network-sense.");
  }
}
