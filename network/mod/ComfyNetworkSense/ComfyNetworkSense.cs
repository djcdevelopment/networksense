namespace ComfyNetworkSense;

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;

using BepInEx;
using BepInEx.Logging;

using HarmonyLib;

using UnityEngine;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class ComfyNetworkSense : BaseUnityPlugin {
  public const string PluginGuid = "djcdevelopment.valheim.comfynetworksense";
  public const string PluginName = "ComfyNetworkSense";
  public const string PluginVersion = "0.4.8";

  public static ComfyNetworkSense Instance { get; private set; }

  static ManualLogSource _logger;
  static readonly ConcurrentQueue<string> _mainThreadMessages = new();
  TelemetryCoordinator _coordinator;
  MatrixCheckinRunner _matrixCheckinRunner;
  LumberjacksBridgeProbe _lumberjacksBridgeProbe;
  LumberjacksProjectionRunner _lumberjacksProjectionRunner;
  LumberjacksShadowAuthorityRunner _lumberjacksShadowAuthorityRunner;
  Harmony _harmony;
  bool _routeRunning;
  bool _autoRehearsalArmed;
  bool _autoRehearsalStarted;
  float _autoRehearsalStartAt = -1.0f;

  void Awake() {
    Instance = this;
    _logger = Logger;

    PluginConfig.Bind(Config);

    _coordinator = new();
    _lumberjacksBridgeProbe = new();
    _lumberjacksProjectionRunner = new();
    _lumberjacksShadowAuthorityRunner = new();

    _harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), harmonyInstanceId: PluginGuid);
    PanelInputPatches.Apply(_harmony);
    AutoCharacterSelectPatches.Apply(_harmony);
    RegisterConsoleCommands();

    // Matrix check-in poller: off by default; only starts for lab/swarm clients that opt in via
    // COMFY_MATRIX_CHECKIN or the [Matrix] config section. It waits internally for a local player.
    if (MatrixCheckinRunner.IsEnabled()) {
      _matrixCheckinRunner = new();
      _matrixCheckinRunner.Start(this, _coordinator);
    } else {
      LogInfo("Matrix check-in disabled (set COMFY_MATRIX_CHECKIN=1 or [Matrix] matrixCheckinEnabled to enable).");
    }

    LogInfo("Telemetry scaffold ready.");
  }

  void Update() {
    if (!PluginConfig.IsModEnabled.Value) {
      return;
    }

    float deltaTime = Time.unscaledDeltaTime;
    using NetworkSensePerfProbe.Section section = NetworkSensePerfProbe.Measure("ComfyNetworkSense.Update");
    NetworkSensePerfProbe.Active?.UpdateFrame(deltaTime);

    using (NetworkSensePerfProbe.Measure("ComfyNetworkSense.FlushMainThreadMessages")) {
      FlushMainThreadMessages();
    }

    if (_coordinator != null && _coordinator.IsPanelOpen && Input.GetKeyDown(KeyCode.Escape)) {
      _coordinator.ClosePanel();
    }

    using (NetworkSensePerfProbe.Measure("ComfyNetworkSense.TelemetryCoordinator.Update")) {
      _coordinator?.Update(deltaTime);
    }

    using (NetworkSensePerfProbe.Measure("ComfyNetworkSense.LumberjacksProjectionRunner.Update")) {
      _lumberjacksProjectionRunner?.Update(deltaTime);
    }

    using (NetworkSensePerfProbe.Measure("ComfyNetworkSense.LumberjacksShadowAuthorityRunner.Update")) {
      _lumberjacksShadowAuthorityRunner?.Update(deltaTime, _coordinator);
    }

    using (NetworkSensePerfProbe.Measure("ComfyNetworkSense.TryStartAutoRehearsal")) {
      TryStartAutoRehearsal();
    }
  }

  void OnGUI() {
    if (!PluginConfig.IsModEnabled.Value) {
      return;
    }

    _coordinator?.DrawHud();
  }

  void OnDestroy() {
    _matrixCheckinRunner?.Stop();
    _matrixCheckinRunner = null;
    _lumberjacksBridgeProbe = null;
    _lumberjacksProjectionRunner?.Dispose();
    _lumberjacksProjectionRunner = null;
    _lumberjacksShadowAuthorityRunner?.Dispose();
    _lumberjacksShadowAuthorityRunner = null;
    _coordinator?.Dispose();
    _coordinator = null;
    _harmony?.UnpatchSelf();
    _harmony = null;

    if (Instance == this) {
      Instance = null;
    }
  }

  public static void HandleServerPulse(long senderId, ZPackage package) {
    Instance?._coordinator?.HandleServerPulse(senderId, package);
  }

  void RegisterConsoleCommands() {
    new Terminal.ConsoleCommand("network_sense_hud", "toggle the ComfyNetworkSense HUD", _ => RunCommand(_coordinator.ToggleHud));
    new Terminal.ConsoleCommand(
        "network_sense_detail",
        "cycle ComfyNetworkSense HUD detail: Summary -> Diagnostic -> DeepDebug",
        _ => RunCommand(_coordinator.CycleHudDetail));
    new Terminal.ConsoleCommand(
        "network_sense_mode",
        "cycle or set ComfyNetworkSense mode: network_sense_mode [solo|combat|group|town]",
        SetModeCommand);
    new Terminal.ConsoleCommand(
        "network_sense_benchmark",
        "start or cancel the ComfyNetworkSense benchmark capture",
        _ => RunCommand(_coordinator.ToggleBenchmark));
    new Terminal.ConsoleCommand(
        "network_sense_status",
        "show ComfyNetworkSense HUD/detail/mode/benchmark status",
        _ => RunCommand(_coordinator.GetStatus));
    new Terminal.ConsoleCommand(
        "network_sense_perf_status",
        "show ComfyNetworkSense perf probe and telemetry writer status",
        _ => RunCommand(_coordinator.GetPerfStatus));
    new Terminal.ConsoleCommand(
        "network_sense_perf_mark",
        "record a ComfyNetworkSense perf marker: network_sense_perf_mark <label>",
        PerfMarkerCommand);
    new Terminal.ConsoleCommand(
        "network_sense_panel",
        "toggle the ComfyNetworkSense debug panel: network_sense_panel [debug|signals|raven]",
        PanelCommand);
    new Terminal.ConsoleCommand(
        "network_sense_debug",
        "open the ComfyNetworkSense debug panel",
        _ => OpenPanelCommand("debug"));
    new Terminal.ConsoleCommand(
        "network_sense_raven",
        "open the ComfyNetworkSense Raven panel",
        _ => OpenPanelCommand("raven"));
    new Terminal.ConsoleCommand(
        "network_sense_reload_config",
        "reload ComfyNetworkSense BepInEx config",
        _ => ReloadPluginConfig());
    new Terminal.ConsoleCommand(
        "network_sense_export_session",
        "write a compact ComfyNetworkSense session export JSON",
        _ => RunCommand(_coordinator.ExportSession));
    new Terminal.ConsoleCommand(
        "network_sense_mcp_status",
        "check whether the local Comfy MCP gateway is reachable",
        _ => CheckMcpGateway());
    new Terminal.ConsoleCommand(
        "network_sense_mcp_note",
        "record a NetworkSense developer note: network_sense_mcp_note <text>",
        RecordNoteCommand);
    new Terminal.ConsoleCommand(
        "network_sense_mcp_mark",
        "record a NetworkSense test marker: network_sense_mcp_mark <label>",
        RecordMarkerCommand);
    new Terminal.ConsoleCommand(
        "network_sense_lumberjacks_probe",
        "probe Lumberjacks Gateway from Valheim: network_sense_lumberjacks_probe [ws-url] [region-id] [input-count]",
        LumberjacksProbeCommand);
    new Terminal.ConsoleCommand(
        "network_sense_lumberjacks_projection",
        "project Lumberjacks entity updates as local-only Valheim markers: network_sense_lumberjacks_projection [start|stop|status] [ws-url] [region-id]",
        LumberjacksProjectionCommand);
    new Terminal.ConsoleCommand(
        "network_sense_lumberjacks_shadow",
        "compare Lumberjacks authoritative movement against local Valheim motion without corrections: network_sense_lumberjacks_shadow [start|stop|status] [ws-url] [region-id]",
        LumberjacksShadowCommand);
    new Terminal.ConsoleCommand(
        "network_sense_lumberjacks_shadow_route",
        "run a teleport route with per-stop Lumberjacks shadow movement: network_sense_lumberjacks_shadow_route [teleport-route.tsv] [movement_only|stationary] [ws-url] [region-id]",
        LumberjacksShadowRouteCommand);
    new Terminal.ConsoleCommand(
        "network_sense_tp",
        "teleport local player for baseline capture: network_sense_tp x z [label] or network_sense_tp x y z [label]",
        TeleportCommand);
    new Terminal.ConsoleCommand(
        "network_sense_route_run",
        "run a NetworkSense teleport route file: network_sense_route_run [teleport-route.tsv]",
        RouteRunCommand);
    new Terminal.ConsoleCommand(
        "network_sense_rehearsal",
        "run a one-command NetworkSense route rehearsal: network_sense_rehearsal [teleport-route.tsv] [profile]",
        RehearsalCommand);
  }

  object SetModeCommand(Terminal.ConsoleEventArgs args) {
    if (args.Length < 2) {
      return RunCommand(_coordinator.CycleMode);
    }

    if (TryParseMode(args[1], out NetworkSenseMode mode)) {
      return RunCommand(() => _coordinator.SetMode(mode));
    }

    string message = "Usage: network_sense_mode [solo|combat|group|town]";
    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
    LogWarning(message);
    return false;
  }

  object PanelCommand(Terminal.ConsoleEventArgs args) {
    string tab = args.Length >= 2 ? args[1] : null;
    _coordinator.TogglePanel(tab);
    string message = "NetworkSense panel toggled.";
    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
    LogInfo(message);
    return message;
  }

  object OpenPanelCommand(string tab) {
    _coordinator.OpenPanel(tab);
    string message = $"NetworkSense {tab} panel opened.";
    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
    LogInfo(message);
    return message;
  }

  static object RunCommand(Func<string> action) {
    string message = action();
    LogInfo(message);
    return message;
  }

  object ReloadPluginConfig() {
    Config.Reload();
    string message = "NetworkSense config reloaded.";
    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
    LogInfo(message);
    return message;
  }

  object CheckMcpGateway() {
    const string endpoint = "http://127.0.0.1:8720/healthz";
    _ = Task.Run(async () => {
      string message;
      try {
        HttpWebRequest request = (HttpWebRequest) WebRequest.Create(endpoint);
        request.Method = "GET";
        request.Timeout = 2000;
        request.ReadWriteTimeout = 2000;
        request.Headers.Add("X-Comfy-Key", "valheim-mod-local");

        using WebResponse response = await request.GetResponseAsync().ConfigureAwait(false);
        message = $"Comfy MCP gateway reachable: HTTP {((HttpWebResponse) response).StatusCode}.";
      } catch (Exception exception) {
        message = $"Comfy MCP gateway unreachable: {exception.GetType().Name}: {exception.Message}";
      }

      _mainThreadMessages.Enqueue(message);
      LogInfo(message);
    });

    return "NetworkSense MCP status check started.";
  }

  object LumberjacksProbeCommand(Terminal.ConsoleEventArgs args) {
    string gatewayUrl = args.Length >= 2
        ? args[1]
        : PluginConfig.LumberjacksGatewayUrl.Value;
    string regionId = args.Length >= 3
        ? args[2]
        : PluginConfig.LumberjacksRegionId.Value;
    int inputCount = PluginConfig.LumberjacksProbeInputCount.Value;

    if (args.Length >= 4 && int.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)) {
      inputCount = parsed;
    }

    string message = _lumberjacksBridgeProbe.Start(gatewayUrl, regionId, inputCount, _coordinator);
    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
    LogInfo(message);
    return message;
  }

  object LumberjacksProjectionCommand(Terminal.ConsoleEventArgs args) {
    string action = args.Length >= 2 ? args[1].Trim().ToLowerInvariant() : "start";

    switch (action) {
      case "start":
      case "run":
      case "on": {
        string gatewayUrl = args.Length >= 3
            ? args[2]
            : PluginConfig.LumberjacksGatewayUrl.Value;
        string regionId = args.Length >= 4
            ? args[3]
            : PluginConfig.LumberjacksRegionId.Value;
        bool driveInputs = PluginConfig.LumberjacksProjectionDriveInputs.Value;
        string message = _lumberjacksProjectionRunner.Start(gatewayUrl, regionId, driveInputs, _coordinator);
        MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
        LogInfo(message);
        return message;
      }

      case "stop":
      case "off": {
        string message = _lumberjacksProjectionRunner.Stop(_coordinator);
        MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
        LogInfo(message);
        return message;
      }

      case "status": {
        string message = _lumberjacksProjectionRunner.GetStatus();
        _coordinator.RecordLumberjacksProjection(_lumberjacksProjectionRunner.BuildStatusRow("status"));
        MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
        LogInfo(message);
        return message;
      }

      default: {
        string message = "Usage: network_sense_lumberjacks_projection [start|stop|status] [ws-url] [region-id]";
        MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
        LogWarning(message);
        return false;
      }
    }
  }

  object LumberjacksShadowCommand(Terminal.ConsoleEventArgs args) {
    string action = args.Length >= 2 ? args[1].Trim().ToLowerInvariant() : "start";

    switch (action) {
      case "start":
      case "run":
      case "on": {
        string gatewayUrl = args.Length >= 3
            ? args[2]
            : PluginConfig.LumberjacksGatewayUrl.Value;
        string regionId = args.Length >= 4
            ? args[3]
            : PluginConfig.LumberjacksRegionId.Value;
        string message = _lumberjacksShadowAuthorityRunner.Start(gatewayUrl, regionId, _coordinator);
        MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
        LogInfo(message);
        return message;
      }

      case "stop":
      case "off": {
        string message = _lumberjacksShadowAuthorityRunner.Stop(_coordinator);
        MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
        LogInfo(message);
        return message;
      }

      case "status": {
        string message = _lumberjacksShadowAuthorityRunner.GetStatus();
        _coordinator.RecordLumberjacksShadow(_lumberjacksShadowAuthorityRunner.BuildStatusRow("status"));
        MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
        LogInfo(message);
        return message;
      }

      default: {
        string message = "Usage: network_sense_lumberjacks_shadow [start|stop|status] [ws-url] [region-id]";
        MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
        LogWarning(message);
        return false;
      }
    }
  }

  object LumberjacksShadowRouteCommand(Terminal.ConsoleEventArgs args) {
    if (_routeRunning) {
      string busyMessage = "NetworkSense route is already running.";
      MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, busyMessage);
      LogWarning(busyMessage);
      return false;
    }

    string fileName = args.Length >= 2 ? args[1] : "teleport-route.tsv";
    string profile = args.Length >= 3 ? args[2] : "movement_only";
    string gatewayUrl = args.Length >= 4
        ? args[3]
        : PluginConfig.LumberjacksGatewayUrl.Value;
    string regionId = args.Length >= 5
        ? args[4]
        : PluginConfig.LumberjacksRegionId.Value;

    fileName = NormalizeRouteFileName(fileName);
    profile = string.IsNullOrWhiteSpace(profile) ? "movement_only" : profile.Trim();
    string routePath = Path.Combine(Paths.ConfigPath, "comfy-network-sense", fileName);
    if (!TryLoadRouteStops(routePath, out List<RouteStop> stops, out string error)) {
      MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, error);
      LogWarning(error);
      return false;
    }

    StartCoroutine(RunLumberjacksShadowRoute(stops, routePath, profile, gatewayUrl, regionId));
    string message = $"Lumberjacks shadow route started: {stops.Count} stops from {fileName}, profile={profile}.";
    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
    LogInfo(message);
    return message;
  }

  object RecordNoteCommand(Terminal.ConsoleEventArgs args) {
    string note = JoinArgs(args, startIndex: 1);

    if (string.IsNullOrWhiteSpace(note)) {
      string message = "Usage: network_sense_mcp_note <text>";
      MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
      return false;
    }

    return RunCommand(() => _coordinator.RecordDevNote(note));
  }

  object RecordMarkerCommand(Terminal.ConsoleEventArgs args) {
    string label = JoinArgs(args, startIndex: 1);

    if (string.IsNullOrWhiteSpace(label)) {
      string message = "Usage: network_sense_mcp_mark <label>";
      MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
      return false;
    }

    return RunCommand(() => _coordinator.RecordDevMarker(label));
  }

  object PerfMarkerCommand(Terminal.ConsoleEventArgs args) {
    string label = JoinArgs(args, startIndex: 1);

    if (string.IsNullOrWhiteSpace(label)) {
      string message = "Usage: network_sense_perf_mark <label>";
      MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
      return false;
    }

    NetworkSensePerfProbe.Active?.Mark(label);
    string marker = $"perf_mark {label}";
    _coordinator.RecordDevMarker(marker);
    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, $"NetworkSense perf marker recorded: {label}");
    LogInfo(marker);
    return marker;
  }

  object TeleportCommand(Terminal.ConsoleEventArgs args) {
    if (!TryParseTeleportArgs(args, out Vector3 target, out string label, out string error)) {
      MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, error);
      LogWarning(error);
      return false;
    }

    Player player = Player.m_localPlayer;
    if (player == null) {
      string missingPlayerMessage = "NetworkSense teleport failed: Player.m_localPlayer is not available.";
      MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, missingPlayerMessage);
      LogWarning(missingPlayerMessage);
      return false;
    }

    Vector3 before = ((Component) player).transform.position;
    bool moved = TryTeleport(player, target);
    string marker = string.IsNullOrWhiteSpace(label)
        ? $"teleport {target.x:0.##} {target.y:0.##} {target.z:0.##}"
        : $"teleport {label} {target.x:0.##} {target.y:0.##} {target.z:0.##}";

    _coordinator.RecordDevMarker(marker);

    string message =
        moved
            ? $"NetworkSense teleported from {FormatVector(before)} to {FormatVector(target)}."
            : $"NetworkSense teleport requested fallback move to {FormatVector(target)}.";
    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
    LogInfo(message);
    return message;
  }

  object RouteRunCommand(Terminal.ConsoleEventArgs args) {
    if (_routeRunning) {
      string busyMessage = "NetworkSense route is already running.";
      MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, busyMessage);
      LogWarning(busyMessage);
      return false;
    }

    string fileName = args.Length >= 2 ? args[1] : "teleport-route.tsv";
    string routePath = Path.Combine(Paths.ConfigPath, "comfy-network-sense", fileName);
    if (!TryLoadRouteStops(routePath, out List<RouteStop> stops, out string error)) {
      MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, error);
      LogWarning(error);
      return false;
    }

    StartCoroutine(RunTeleportRoute(stops, routePath));
    string message = $"NetworkSense route started: {stops.Count} stops from {fileName}.";
    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
    LogInfo(message);
    return message;
  }

  object RehearsalCommand(Terminal.ConsoleEventArgs args) {
    string fileName = args.Length >= 2 ? args[1] : "teleport-route.tsv";
    string profile = args.Length >= 3 ? args[2] : "host_full";
    if (!TryStartRehearsal(fileName, profile, initiatedByAuto: false, out string message)) {
      MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
      LogWarning(message);
      return false;
    }

    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
    LogInfo(message);
    return message;
  }

  bool TryStartRehearsal(string fileName, string profile, bool initiatedByAuto, out string message) {
    if (_routeRunning) {
      message = "NetworkSense route is already running.";
      return false;
    }

    fileName = NormalizeRouteFileName(fileName);
    profile = string.IsNullOrWhiteSpace(profile) ? "host_full" : profile.Trim();
    string routePath = Path.Combine(Paths.ConfigPath, "comfy-network-sense", fileName);
    if (!TryLoadRouteStops(routePath, out List<RouteStop> stops, out string error)) {
      message = error;
      return false;
    }

    if (!initiatedByAuto) {
      RunCommand(_coordinator.ReloadConfig);
    }

    CheckMcpGateway();
    _coordinator.RecordDevMarker($"route_rehearsal {profile} start");
    if (initiatedByAuto) {
      _coordinator.RecordDevMarker($"auto_rehearsal start profile={profile} file={fileName}");
    }

    StartCoroutine(RunTeleportRoute(stops, routePath, profile, exportOnComplete: true, autoRehearsal: initiatedByAuto));
    message = $"NetworkSense rehearsal started: {stops.Count} stops from {fileName}, profile={profile}.";
    return true;
  }

  void TryStartAutoRehearsal() {
    if (!PluginConfig.AutoRehearsalEnabled.Value || _coordinator == null || _routeRunning) {
      return;
    }

    if (_autoRehearsalStarted && PluginConfig.AutoRehearsalRunOncePerSession.Value) {
      return;
    }

    Player player = Player.m_localPlayer;
    if (player == null) {
      _autoRehearsalArmed = false;
      _autoRehearsalStartAt = -1.0f;
      return;
    }

    if (!_autoRehearsalArmed) {
      float delay = Math.Max(0.0f, PluginConfig.AutoRehearsalDelaySeconds.Value);
      _autoRehearsalStartAt = Time.time + delay;
      _autoRehearsalArmed = true;
      _coordinator.RecordDevMarker(
          $"auto_rehearsal armed profile={PluginConfig.AutoRehearsalProfile.Value} file={PluginConfig.AutoRehearsalRouteFile.Value} delay={delay:0.##}");
      return;
    }

    if (_autoRehearsalStartAt >= 0.0f && Time.time < _autoRehearsalStartAt) {
      return;
    }

    _autoRehearsalStarted = true;
    if (!TryStartRehearsal(
        PluginConfig.AutoRehearsalRouteFile.Value,
        PluginConfig.AutoRehearsalProfile.Value,
        initiatedByAuto: true,
        out string message)) {
      _coordinator.RecordDevMarker($"auto_rehearsal blocked {message}");
      MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
      LogWarning(message);
      return;
    }

    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
    LogInfo(message);
  }

  static string JoinArgs(Terminal.ConsoleEventArgs args, int startIndex) {
    if (args.Length <= startIndex) {
      return string.Empty;
    }

    string[] values = new string[args.Length - startIndex];

    for (int index = startIndex; index < args.Length; index++) {
      values[index - startIndex] = args[index];
    }

    return string.Join(" ", values);
  }

  static string NormalizeRouteFileName(string fileName) {
    string normalized = Path.GetFileName((fileName ?? string.Empty).Trim());
    return string.IsNullOrWhiteSpace(normalized) ? "teleport-route.tsv" : normalized;
  }

  bool TryParseTeleportArgs(Terminal.ConsoleEventArgs args, out Vector3 target, out string label, out string error) {
    target = Vector3.zero;
    label = string.Empty;
    error = "Usage: network_sense_tp x z [label] or network_sense_tp x y z [label]";

    if (args == null || args.Length < 3 || !args.TryParameterFloat(1, out float x)) {
      return false;
    }

    if (args.Length >= 4
        && args.TryParameterFloat(2, out float explicitY)
        && args.TryParameterFloat(3, out float explicitZ)) {
      target = new Vector3(x, explicitY, explicitZ);
      label = JoinArgs(args, startIndex: 4);
      return true;
    }

    if (!args.TryParameterFloat(2, out float z)) {
      return false;
    }

    float y = 80.0f;
    if (TryResolveGroundHeight(x, z, out float groundHeight)) {
      y = groundHeight + 3.0f;
    }

    target = new Vector3(x, y, z);
    label = JoinArgs(args, startIndex: 3);
    return true;
  }

  static bool TryResolveGroundHeight(float x, float z, out float height) {
    using NetworkSensePerfProbe.Section section = NetworkSensePerfProbe.Measure("ComfyNetworkSense.TryResolveGroundHeight");

    height = 0.0f;
    try {
      if (ZoneSystem.instance == null) {
        return false;
      }

      return ZoneSystem.instance.GetGroundHeight(new Vector3(x, 0.0f, z), out height);
    } catch (Exception exception) {
      LogWarning($"NetworkSense ground-height lookup failed: {exception.Message}");
      return false;
    }
  }

  static bool TryTeleport(Player player, Vector3 target) {
    using NetworkSensePerfProbe.Section section = NetworkSensePerfProbe.Measure("ComfyNetworkSense.TryTeleport");

    try {
      MethodInfo method = null;
      foreach (MethodInfo candidate in player.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) {
        if (candidate.Name == "TeleportTo" && candidate.GetParameters().Length == 3) {
          method = candidate;
          break;
        }
      }

      if (method != null) {
        object result = method.Invoke(player, new object[] { target, Quaternion.identity, true });
        if (result is bool ok && ok) {
          return true;
        }
      }

      ((Component) player).transform.position = target;
      Rigidbody body = ((Component) player).GetComponent<Rigidbody>();
      if (body != null) {
        body.position = target;
        body.linearVelocity = Vector3.zero;
      }
      return true;
    } catch (Exception exception) {
      LogWarning($"NetworkSense teleport failed: {exception.Message}");
      return false;
    }
  }

  static string FormatVector(Vector3 value) {
    return string.Format(
        CultureInfo.InvariantCulture,
        "{0:0.##},{1:0.##},{2:0.##}",
        value.x,
        value.y,
        value.z);
  }

  bool TryLoadRouteStops(string routePath, out List<RouteStop> stops, out string error) {
    stops = new List<RouteStop>();
    error = string.Empty;

    if (!File.Exists(routePath)) {
      error = $"NetworkSense route file not found: {routePath}";
      return false;
    }

    string[] lines = File.ReadAllLines(routePath);
    for (int index = 0; index < lines.Length; index++) {
      string line = lines[index].Trim();
      if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) {
        continue;
      }

      string[] parts = line.Contains("\t")
          ? line.Split('\t')
          : line.Split(',');
      if (parts.Length == 0 || string.Equals(parts[0].Trim(), "id", StringComparison.OrdinalIgnoreCase)) {
        continue;
      }

      if (parts.Length < 5
          || !TryParseRouteFloat(parts[1], out float x)
          || !TryParseRouteFloat(parts[2], out float z)
          || !TryParseRouteFloat(parts[3], out float settleSeconds)
          || !TryParseRouteFloat(parts[4], out float benchmarkSeconds)) {
        error = $"NetworkSense route parse failed at line {index + 1}: expected id,x,z,settle_seconds,benchmark_seconds[,y]";
        return false;
      }

      float? y = null;
      if (parts.Length >= 6 && TryParseRouteFloat(parts[5], out float explicitY)) {
        y = explicitY;
      }

      stops.Add(new RouteStop {
          Id = parts[0].Trim(),
          X = x,
          Z = z,
          SettleSeconds = Math.Max(0.0f, settleSeconds),
          BenchmarkSeconds = Math.Max(1.0f, benchmarkSeconds),
          Y = y
      });
    }

    if (stops.Count == 0) {
      error = $"NetworkSense route file has no stops: {routePath}";
      return false;
    }

    return true;
  }

  IEnumerator RunTeleportRoute(
      List<RouteStop> stops,
      string routePath,
      string rehearsalProfile = null,
      bool exportOnComplete = false,
      bool autoRehearsal = false) {
    _routeRunning = true;
    bool aborted = false;
    NetworkSensePerfProbe.SetRouteState("route", "", "start");
    _coordinator.RecordDevMarker($"route_run start stops={stops.Count} file={Path.GetFileName(routePath)}");

    foreach (RouteStop stop in stops) {
      Player player = Player.m_localPlayer;
      if (player == null) {
        NetworkSensePerfProbe.SetRouteState("route", stop.Id, "abort_missing_player");
        _coordinator.RecordDevMarker($"route_run abort {stop.Id} missing_player");
        aborted = true;
        break;
      }

      NetworkSensePerfProbe.SetRouteState("route", stop.Id, "resolve_target");
      Vector3 target;
      using (NetworkSensePerfProbe.Measure("RunTeleportRoute.ResolveRouteTarget")) {
        target = ResolveRouteTarget(stop);
      }

      _coordinator.RecordDevMarker($"{stop.Id} start");

      NetworkSensePerfProbe.SetRouteState("route", stop.Id, "teleport");
      bool moved;
      using (NetworkSensePerfProbe.Measure("RunTeleportRoute.TryTeleport")) {
        moved = TryTeleport(player, target);
      }
      _coordinator.RecordDevMarker($"{stop.Id} teleport moved={moved} target={FormatVector(target)}");

      if (stop.SettleSeconds > 0.0f) {
        NetworkSensePerfProbe.SetRouteState("route", stop.Id, "settle");
        yield return new WaitForSeconds(stop.SettleSeconds);
      }

      NetworkSensePerfProbe.SetRouteState("route", stop.Id, "benchmark_start");
      if (!_coordinator.BenchmarkRunning) {
        _coordinator.ToggleBenchmark();
      }

      NetworkSensePerfProbe.SetRouteState("route", stop.Id, "benchmark_window");
      yield return new WaitForSeconds(stop.BenchmarkSeconds);

      float waitStarted = Time.time;
      NetworkSensePerfProbe.SetRouteState("route", stop.Id, "benchmark_wait");
      while (_coordinator.BenchmarkRunning && Time.time - waitStarted < 10.0f) {
        yield return null;
      }

      if (_coordinator.BenchmarkRunning) {
        NetworkSensePerfProbe.SetRouteState("route", stop.Id, "benchmark_timeout_cancel");
        _coordinator.ToggleBenchmark();
        _coordinator.RecordDevMarker($"{stop.Id} benchmark_cancelled_after_timeout");
      }

      NetworkSensePerfProbe.SetRouteState("route", stop.Id, "stop_end");
      _coordinator.RecordDevMarker($"{stop.Id} end");
    }

    NetworkSensePerfProbe.SetRouteState("route", "", aborted ? "abort" : "end");
    _coordinator.RecordDevMarker(aborted ? "route_run abort" : "route_run end");

    if (!string.IsNullOrWhiteSpace(rehearsalProfile)) {
      _coordinator.RecordDevMarker($"route_rehearsal {rehearsalProfile} {(aborted ? "abort" : "end")}");
    }

    if (autoRehearsal) {
      _coordinator.RecordDevMarker(aborted ? "auto_rehearsal abort" : "auto_rehearsal end");
    }

    if (exportOnComplete) {
      NetworkSensePerfProbe.SetRouteState("route", "", "export");
      string exportMessage = _coordinator.ExportSession();
      LogInfo(exportMessage);
    }

    NetworkSensePerfProbe.SetRouteState("idle");
    _routeRunning = false;
  }

  IEnumerator RunLumberjacksShadowRoute(
      List<RouteStop> stops,
      string routePath,
      string profile,
      string gatewayUrl,
      string regionId) {
    _routeRunning = true;
    bool aborted = false;
    string activeStopId = string.Empty;
    bool moveDuringBenchmark =
        string.Equals(profile, "movement_only", StringComparison.OrdinalIgnoreCase)
        || string.Equals(profile, "shadow_movement", StringComparison.OrdinalIgnoreCase);

    NetworkSensePerfProbe.SetRouteState("shadow_route", "", "start");
    _coordinator.RecordDevMarker(
        $"lumberjacks_shadow_route {profile} start stops={stops.Count} file={Path.GetFileName(routePath)}");

    try {
      foreach (RouteStop stop in stops) {
        activeStopId = stop.Id;
        if (!TryGetUsableLocalPlayer(out Player player)) {
          NetworkSensePerfProbe.SetRouteState("shadow_route", stop.Id, "abort_missing_player");
          _coordinator.RecordDevMarker($"lumberjacks_shadow_route abort {stop.Id} missing_player");
          aborted = true;
          break;
        }

        NetworkSensePerfProbe.SetRouteState("shadow_route", stop.Id, "resolve_target");
        Vector3 target;
        using (NetworkSensePerfProbe.Measure("RunLumberjacksShadowRoute.ResolveRouteTarget")) {
          target = ResolveRouteTarget(stop);
        }

        _coordinator.RecordDevMarker($"{stop.Id} shadow_route_start");

        NetworkSensePerfProbe.SetRouteState("shadow_route", stop.Id, "teleport");
        bool moved;
        using (NetworkSensePerfProbe.Measure("RunLumberjacksShadowRoute.TryTeleport")) {
          moved = TryTeleport(player, target);
        }
        _coordinator.RecordDevMarker($"{stop.Id} shadow_route_teleport moved={moved} target={FormatVector(target)}");

        if (stop.SettleSeconds > 0.0f) {
          NetworkSensePerfProbe.SetRouteState("shadow_route", stop.Id, "settle");
          yield return new WaitForSeconds(stop.SettleSeconds);
        }

        NetworkSensePerfProbe.SetRouteState("shadow_route", stop.Id, "wait_local_player");
        yield return WaitForUsableLocalPlayer("shadow_route", stop.Id, "wait_local_player", 45.0f);
        if (!TryGetUsableLocalPlayer(out player)) {
          NetworkSensePerfProbe.SetRouteState("shadow_route", stop.Id, "abort_local_player_not_ready");
          _coordinator.RecordDevMarker($"lumberjacks_shadow_route abort {stop.Id} local_player_not_ready_after_teleport");
          aborted = true;
          break;
        }

        bool shadowStarted = false;
        bool durationOverridden = false;
        float previousDuration = PluginConfig.BenchmarkDurationSeconds.Value;

        try {
          _lumberjacksShadowAuthorityRunner.SetRouteContext("shadow_route", stop.Id, "start");
          string startMessage = _lumberjacksShadowAuthorityRunner.Start(gatewayUrl, regionId, _coordinator);
          shadowStarted = _lumberjacksShadowAuthorityRunner.IsRunning;
          _lumberjacksShadowAuthorityRunner.SetRouteContext("shadow_route", stop.Id, "connect");
          _coordinator.RecordLumberjacksShadow(_lumberjacksShadowAuthorityRunner.BuildStatusRow("route_start"));
          LogInfo(startMessage);
          _coordinator.RecordDevMarker($"{stop.Id} shadow_start {startMessage}");

          NetworkSensePerfProbe.SetRouteState("shadow_route", stop.Id, "connect");
          yield return new WaitForSeconds(2.0f);

          yield return WaitForUsableLocalPlayer("shadow_route", stop.Id, "wait_local_player_before_benchmark", 15.0f);
          if (!TryGetUsableLocalPlayer(out player)) {
            NetworkSensePerfProbe.SetRouteState("shadow_route", stop.Id, "abort_local_player_missing_before_benchmark");
            _coordinator.RecordDevMarker($"lumberjacks_shadow_route abort {stop.Id} local_player_missing_before_benchmark");
            aborted = true;
            break;
          }

          float benchmarkSeconds = Mathf.Max(1.0f, stop.BenchmarkSeconds);
          PluginConfig.BenchmarkDurationSeconds.Value = benchmarkSeconds;
          durationOverridden = true;

          NetworkSensePerfProbe.SetRouteState("shadow_route", stop.Id, "benchmark_start");
          _lumberjacksShadowAuthorityRunner.SetRouteContext("shadow_route", stop.Id, "benchmark_start");
          if (!_coordinator.BenchmarkRunning) {
            _coordinator.StartBenchmark();
          }

          float benchmarkStartedAt = Time.realtimeSinceStartup;
          float maxWaitSeconds = benchmarkSeconds + 15.0f;
          Vector3 origin = ((Component) player).transform.position;

          NetworkSensePerfProbe.SetRouteState("shadow_route", stop.Id, moveDuringBenchmark ? "movement_window" : "stationary_window");
          while (_coordinator.BenchmarkRunning && Time.realtimeSinceStartup - benchmarkStartedAt < maxWaitSeconds) {
            string routePhase = moveDuringBenchmark ? "movement_window" : "stationary_window";
            _lumberjacksShadowAuthorityRunner.SetRouteContext("shadow_route", stop.Id, routePhase);

            if (moveDuringBenchmark) {
              if (TryGetUsableLocalPlayer(out Player currentPlayer)) {
                StepRouteMovementPattern(currentPlayer, origin, Time.realtimeSinceStartup - benchmarkStartedAt);
              } else {
                NetworkSensePerfProbe.SetRouteState("shadow_route", stop.Id, "movement_wait_local_player");
              }
            }
            yield return null;
          }

          if (_coordinator.BenchmarkRunning) {
            NetworkSensePerfProbe.SetRouteState("shadow_route", stop.Id, "benchmark_timeout_cancel");
            _coordinator.CancelBenchmark();
            _coordinator.RecordDevMarker($"{stop.Id} shadow_route_benchmark_cancelled_after_timeout");
          }

          _coordinator.ConsumeLatestBenchmarkResult();
        } finally {
          if (durationOverridden) {
            PluginConfig.BenchmarkDurationSeconds.Value = previousDuration;
          }

          if (shadowStarted && _lumberjacksShadowAuthorityRunner.IsRunning) {
            NetworkSensePerfProbe.SetRouteState("shadow_route", stop.Id, "shadow_stop");
            _lumberjacksShadowAuthorityRunner.SetRouteContext("shadow_route", stop.Id, aborted ? "route_abort" : "route_stop");
            _coordinator.RecordLumberjacksShadow(_lumberjacksShadowAuthorityRunner.BuildStatusRow(aborted ? "route_abort" : "route_stop"));
            string stopMessage = _lumberjacksShadowAuthorityRunner.Stop(_coordinator);
            LogInfo(stopMessage);
            _coordinator.RecordDevMarker($"{stop.Id} shadow_end {stopMessage}");
          }
        }

        if (aborted) {
          break;
        }

        _coordinator.RecordDevMarker($"{stop.Id} shadow_route_end");
        yield return new WaitForSeconds(0.5f);
      }
    } finally {
      if (_lumberjacksShadowAuthorityRunner.IsRunning) {
        _lumberjacksShadowAuthorityRunner.SetRouteContext("shadow_route", activeStopId, "route_abort");
        _coordinator.RecordLumberjacksShadow(_lumberjacksShadowAuthorityRunner.BuildStatusRow("route_abort"));
        string stopMessage = _lumberjacksShadowAuthorityRunner.Stop(_coordinator);
        LogInfo(stopMessage);
      }

      NetworkSensePerfProbe.SetRouteState("shadow_route", "", aborted ? "abort" : "end");
      _coordinator.RecordDevMarker(aborted ? "lumberjacks_shadow_route abort" : "lumberjacks_shadow_route end");

      NetworkSensePerfProbe.SetRouteState("shadow_route", "", "export");
      string exportMessage = _coordinator.ExportSession();
      LogInfo(exportMessage);

      NetworkSensePerfProbe.SetRouteState("idle");
      _routeRunning = false;
    }
  }

  static IEnumerator WaitForUsableLocalPlayer(string routeState, string stopId, string phase, float timeoutSeconds) {
    float startedAt = Time.realtimeSinceStartup;
    float stableSeconds = 0.0f;
    while (Time.realtimeSinceStartup - startedAt < timeoutSeconds) {
      NetworkSensePerfProbe.SetRouteState(routeState, stopId, phase);
      if (TryGetUsableLocalPlayer(out _)) {
        stableSeconds += Time.unscaledDeltaTime;
        if (stableSeconds >= 1.0f) {
          yield break;
        }
      } else {
        stableSeconds = 0.0f;
      }

      yield return null;
    }
  }

  static bool TryGetUsableLocalPlayer(out Player player) {
    player = Player.m_localPlayer;
    if (!player) {
      player = null;
      return false;
    }

    try {
      _ = ((Component) player).transform.position;
      return true;
    } catch {
      player = null;
      return false;
    }
  }

  static Vector3 ResolveRouteTarget(RouteStop stop) {
    using NetworkSensePerfProbe.Section section = NetworkSensePerfProbe.Measure("ComfyNetworkSense.ResolveRouteTarget");

    float y = stop.Y ?? 80.0f;
    if (!stop.Y.HasValue && TryResolveGroundHeight(stop.X, stop.Z, out float groundHeight)) {
      y = groundHeight + 3.0f;
    }

    return new Vector3(stop.X, y, stop.Z);
  }

  static void StepRouteMovementPattern(Player player, Vector3 origin, float elapsedSeconds) {
    using NetworkSensePerfProbe.Section section = NetworkSensePerfProbe.Measure("ComfyNetworkSense.StepRouteMovementPattern");

    try {
      const float radius = 4.0f;
      float angle = elapsedSeconds * 1.5f;
      Vector3 offset = new(Mathf.Cos(angle) * radius, 0.0f, Mathf.Sin(angle) * radius);
      Vector3 next = origin + offset;
      if (TryResolveGroundHeight(next.x, next.z, out float ground)) {
        next.y = ground + 1.0f;
      } else {
        next.y = origin.y;
      }
      ((Component) player).transform.position = next;
    } catch (Exception exception) {
      LogWarning($"NetworkSense route movement step failed: {exception.Message}");
    }
  }

  static bool TryParseRouteFloat(string value, out float result) {
    return float.TryParse((value ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
  }

  static void FlushMainThreadMessages() {
    while (_mainThreadMessages.TryDequeue(out string message)) {
      MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
    }
  }

  public static void EnqueueMainThreadMessage(string message) {
    _mainThreadMessages.Enqueue(message);
  }

  public static bool IsPanelOpen => Instance?._coordinator?.IsPanelOpen == true;

  static bool TryParseMode(string value, out NetworkSenseMode mode) {
    switch ((value ?? string.Empty).Trim().ToLowerInvariant()) {
      case "solo":
      case "auto":
        mode = NetworkSenseMode.Solo;
        return true;
      case "combat":
        mode = NetworkSenseMode.Combat;
        return true;
      case "group":
      case "groupcombat":
      case "group-combat":
      case "group_combat":
        mode = NetworkSenseMode.GroupCombat;
        return true;
      case "town":
      case "base":
      case "village":
      case "low":
      case "lowimpact":
      case "low-impact":
      case "staging":
      case "stage":
        mode = NetworkSenseMode.Town;
        return true;
      default:
        mode = NetworkSenseMode.Solo;
        return false;
    }
  }

  public static void LogInfo(object message) {
    _logger?.LogInfo($"[{DateTime.Now.ToString(DateTimeFormatInfo.InvariantInfo)}] {message}");
  }

  public static void LogWarning(object message) {
    _logger?.LogWarning($"[{DateTime.Now.ToString(DateTimeFormatInfo.InvariantInfo)}] {message}");
  }

  sealed class RouteStop {
    public string Id;
    public float X;
    public float Z;
    public float SettleSeconds;
    public float BenchmarkSeconds;
    public float? Y;
  }
}
