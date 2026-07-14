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
  public const string PluginVersion = "0.5.19";

  public static ComfyNetworkSense Instance { get; private set; }

  static ManualLogSource _logger;
  static readonly ConcurrentQueue<string> _mainThreadMessages = new();
  TelemetryCoordinator _coordinator;
  MatrixCheckinRunner _matrixCheckinRunner;
  LumberjacksBridgeProbe _lumberjacksBridgeProbe;
  LumberjacksProjectionRunner _lumberjacksProjectionRunner;
  LumberjacksShadowAuthorityRunner _lumberjacksShadowAuthorityRunner;
  LumberjacksPriorityProbeRunner _lumberjacksPriorityProbeRunner;
  LumberjacksPriorityMirrorRunner _lumberjacksPriorityMirrorRunner;
  LumberjacksPriorityManifestListener _lumberjacksPriorityManifestListener;
  NetcodeProbeRunner _netcodeProbeRunner;
  OwnershipObserveRunner _ownershipObserveRunner;
  OwnershipPinRunner _ownershipPinRunner;
  ZdoRedirectRunner _zdoRedirectRunner;
  ZdoInjectionRunner _zdoInjectionRunner;
  HandshakeResponderRunner _handshakeResponderRunner;
  Harmony _harmony;
  bool _routeRunning;
  bool _autoRehearsalArmed;
  bool _autoRehearsalStarted;
  float _autoRehearsalStartAt = -1.0f;
  bool _netcodeProbeAutoArmed;
  bool _netcodeProbeAutoStarted;
  float _netcodeProbeAutoStartAt = -1.0f;
  float _netcodeProbeAutoStopAt = -1.0f;

  enum ShadowRouteMovementKind {
    Stationary,
    Circle,
    AxisNorth,
    AxisEast,
    AxisSouth,
    AxisWest
  }

  void Awake() {
    Instance = this;
    _logger = Logger;

    PluginConfig.Bind(Config);

    _coordinator = new();
    _lumberjacksBridgeProbe = new();
    _lumberjacksProjectionRunner = new();
    _lumberjacksShadowAuthorityRunner = new();
    _lumberjacksPriorityProbeRunner = new();
    _lumberjacksPriorityMirrorRunner = new();
    _coordinator.SetLumberjacksPriorityMirror(_lumberjacksPriorityMirrorRunner);
    _coordinator.SetLumberjacksReplacementTelemetryProvider(GetLumberjacksReplacementTelemetry);
    _lumberjacksPriorityManifestListener = new();
    _netcodeProbeRunner = new();
    _ownershipObserveRunner = new();
    _ownershipPinRunner = new();
    _zdoRedirectRunner = new();
    _zdoInjectionRunner = new();
    _handshakeResponderRunner = new();

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

  Dictionary<string, object> GetLumberjacksReplacementTelemetry() {
    Dictionary<string, object> result = new();
    Dictionary<string, object> handshake = _handshakeResponderRunner?.GetTelemetrySnapshot();
    Dictionary<string, object> redirect = _zdoRedirectRunner?.BuildStatusRow("heartbeat") as Dictionary<string, object>;
    Dictionary<string, object> injection = _zdoInjectionRunner?.BuildStatusRow("heartbeat") as Dictionary<string, object>;
    Dictionary<string, object> netcode = _netcodeProbeRunner?.BuildStatusRow("heartbeat") as Dictionary<string, object>;

    if (handshake != null) {
      result["handshake_accepted"] = handshake["handshake_accepted"];
      result["handshake_rejected"] = handshake["handshake_rejected"];
    }
    if (redirect != null) {
      result["redirect_suppressed"] = redirect.TryGetValue("suppressed", out object suppressed) ? suppressed : null;
      result["redirect_received"] = redirect.TryGetValue("posted_ok", out object received) ? received : null;
      result["redirect_missing"] = null;
      result["redirect_duplicates"] = null;
    }
    if (injection != null) {
      result["injection_applied"] = injection.TryGetValue("applied", out object applied) ? applied : null;
      result["injection_rendered"] = injection.TryGetValue("rendered", out object rendered) ? rendered : null;
      result["injection_rejected"] = injection.TryGetValue("rejected", out object rejected) ? rejected : null;
    }
    if (netcode != null) {
      result["zdo_probe_running"] = netcode.TryGetValue("running", out object running) ? running : null;
      result["zdo_probe_recv_rows"] = netcode.TryGetValue("recv_zdo_rows", out object recv) ? recv : null;
      result["zdo_probe_send_rows"] = netcode.TryGetValue("send_zdo_rows", out object send) ? send : null;
      result["zdo_probe_recv_calls"] = netcode.TryGetValue("recv_funnel_calls", out object recvCalls) ? recvCalls : null;
      result["zdo_probe_create_sync_calls"] = netcode.TryGetValue("create_sync_list_calls", out object syncCalls) ? syncCalls : null;
    }
    return result;
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

    using (NetworkSensePerfProbe.Measure("ComfyNetworkSense.LumberjacksPriorityProbeRunner.Update")) {
      _lumberjacksPriorityProbeRunner?.Update(deltaTime, _coordinator);
    }

    using (NetworkSensePerfProbe.Measure("ComfyNetworkSense.ZdoInjectionRunner.Update")) {
      _zdoInjectionRunner?.Update(deltaTime, _coordinator);
    }

    // P6/I5 handshake responder self-arms on the server (the handshake fires at connect time,
    // before any netcode-probe window) and stays armed until config-off + restart.
    using (NetworkSensePerfProbe.Measure("ComfyNetworkSense.HandshakeResponderRunner.Update")) {
      _handshakeResponderRunner?.Update(deltaTime, _coordinator);
    }

    using (NetworkSensePerfProbe.Measure("ComfyNetworkSense.TryStartAutoRehearsal")) {
      TryStartAutoRehearsal();
    }

    using (NetworkSensePerfProbe.Measure("ComfyNetworkSense.TryDriveNetcodeProbeAuto")) {
      TryDriveNetcodeProbeAuto();
    }
  }

  // Config-driven auto-start for the rung I1 netcode probe. Unlike the auto-rehearsal, this
  // does NOT wait for a local player — it fires as soon as a network peer is connected, so it
  // works headless on the dedicated server as well as on clients. Intended for private lab
  // runs (e.g. run-autonomous-valheim-lab.ps1) where no console is available to type the
  // command. Observe-only, so it is safe to leave armed.
  void TryDriveNetcodeProbeAuto() {
    if (!PluginConfig.NetcodeProbeAutoStartEnabled.Value || _coordinator == null || _netcodeProbeRunner == null) {
      return;
    }

    if (_netcodeProbeAutoStarted) {
      // Already ran this session; only remaining job is the timed auto-stop.
      if (_netcodeProbeRunner.IsRunning
          && _netcodeProbeAutoStopAt >= 0.0f
          && Time.time >= _netcodeProbeAutoStopAt) {
        string stopMessage = _netcodeProbeRunner.Stop();
        _coordinator.RecordNetcodeProbe(_netcodeProbeRunner.BuildStatusRow("auto_stop"));
        _coordinator.RecordDevMarker("netcode_probe_auto stop");
        LogInfo("Netcode probe auto-stopped: " + stopMessage);

        // Ownership observe is coupled to the probe window: stop it in lockstep.
        if (_ownershipObserveRunner != null && _ownershipObserveRunner.IsRunning) {
          string ownershipStop = _ownershipObserveRunner.Stop();
          _coordinator.RecordDevMarker("ownership_observe stop");
          LogInfo("Ownership observe auto-stopped: " + ownershipStop);
        }

        // Ownership pin (behaviour-changing) is coupled to the same window: disarm in lockstep.
        if (_ownershipPinRunner != null && _ownershipPinRunner.IsRunning) {
          string pinStop = _ownershipPinRunner.Stop();
          _coordinator.RecordDevMarker("ownership_pin stop");
          LogInfo("Ownership pin auto-stopped: " + pinStop);
        }

        // P4/I3 ZDO redirect (behaviour-changing) is coupled to the same window: disarm in
        // lockstep (it usually auto-disarmed already at zdoRedirectActiveSeconds — the in-window
        // rollback rehearsal).
        if (_zdoRedirectRunner != null && _zdoRedirectRunner.IsRunning) {
          string redirectStop = _zdoRedirectRunner.Stop();
          _coordinator.RecordDevMarker("zdo_redirect stop");
          LogInfo("ZDO redirect auto-stopped: " + redirectStop);
        }

        if (_zdoInjectionRunner != null && _zdoInjectionRunner.IsRunning) {
          string injectionStop = _zdoInjectionRunner.Stop();
          _coordinator.RecordDevMarker("zdo_injection stop");
          LogInfo("ZDO injection auto-stopped: " + injectionStop);
        }
      }
      return;
    }

    ZNet znet = ZNet.instance;
    if (znet == null || znet.GetPeerConnections() <= 0) {
      _netcodeProbeAutoArmed = false;
      _netcodeProbeAutoStartAt = -1.0f;
      return;
    }

    if (!_netcodeProbeAutoArmed) {
      float delay = Math.Max(0.0f, PluginConfig.NetcodeProbeAutoStartDelaySeconds.Value);
      _netcodeProbeAutoStartAt = Time.time + delay;
      _netcodeProbeAutoArmed = true;
      _coordinator.RecordDevMarker(
          $"netcode_probe_auto armed delay={delay:0.##} peers={znet.GetPeerConnections()}");
      return;
    }

    if (_netcodeProbeAutoStartAt >= 0.0f && Time.time < _netcodeProbeAutoStartAt) {
      return;
    }

    _netcodeProbeAutoStarted = true;
    TryCoupleAutoRehearsalToNetcodeProbe();
    int maxDetailRows = PluginConfig.NetcodeProbeMaxDetailRows.Value;
    string message = _netcodeProbeRunner.Start(_coordinator, maxDetailRows);
    float autoStopSeconds = PluginConfig.NetcodeProbeAutoStopSeconds.Value;
    _netcodeProbeAutoStopAt = autoStopSeconds > 0.0f ? Time.time + autoStopSeconds : -1.0f;
    _coordinator.RecordDevMarker(
        $"netcode_probe_auto start maxDetailRows={maxDetailRows} autoStopSeconds={autoStopSeconds:0.##}");
    LogInfo("Netcode probe auto-started: " + message);

    // P3/I2 ownership-churn observer, coupled to the probe capture window. Rollback-gated and
    // observe-only (server-side postfix on ZDO.SetOwner/SetOwnerInternal); off by default. Started
    // BEFORE the pin so its prefix is registered first — a pin-blocked change then reads as a no-op
    // to the observer (correct) rather than a spurious transition.
    if (PluginConfig.OwnershipObserveEnabled.Value && _ownershipObserveRunner != null) {
      string ownershipMessage = _ownershipObserveRunner.Start(_coordinator, maxDetailRows);
      _coordinator.RecordDevMarker("ownership_observe start (coupled to netcode probe)");
      LogInfo("Ownership observe auto-started: " + ownershipMessage);
    }

    // P3/I2 ownership PIN (behaviour-changing), coupled to the same window. Rollback-gated
    // (ownershipPinEnabled, default off). Server-side prefixes on ZDO.SetOwner/SetOwnerInternal
    // skip the vanilla transfer on auto-captured ZDOs so ownership stays pinned across zone entry.
    if (PluginConfig.OwnershipPinEnabled.Value && _ownershipPinRunner != null) {
      string pinMessage = _ownershipPinRunner.Start(_coordinator, maxDetailRows);
      _coordinator.RecordDevMarker("ownership_pin start (coupled to netcode probe)");
      LogInfo("Ownership pin auto-started: " + pinMessage);
    }

    // P4/I3 outbound REDIRECT (behaviour-changing), coupled to the same window. Rollback-gated
    // (zdoRedirectEnabled, default off; empty prefab allowlist refuses). Server-side postfix on
    // ZDOMan.CreateSyncList suppresses native send for tagged prefabs and posts the
    // wire-equivalent to the Lumberjacks gateway; auto-disarms at zdoRedirectActiveSeconds for
    // the in-window rollback rehearsal.
    if (PluginConfig.ZdoRedirectEnabled.Value && _zdoRedirectRunner != null) {
      string redirectMessage = _zdoRedirectRunner.Start(_coordinator, maxDetailRows);
      _coordinator.RecordDevMarker("zdo_redirect start (coupled to netcode probe)");
      LogInfo("ZDO redirect auto-started: " + redirectMessage);
    }


    // P5/I4 inbound injection, coupled to the same finite probe window. Client-only,
    // synthetic-authority + prefab allowlist scoped, and off by default.
    if (PluginConfig.ZdoInjectionEnabled.Value && _zdoInjectionRunner != null) {
      string injectionMessage = _zdoInjectionRunner.Start(_coordinator);
      _coordinator.RecordDevMarker("zdo_injection start (coupled to netcode probe)");
      LogInfo("ZDO injection auto-started: " + injectionMessage);
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
    _lumberjacksPriorityProbeRunner?.Dispose();
    _lumberjacksPriorityProbeRunner = null;
    _lumberjacksPriorityMirrorRunner?.Dispose();
    _lumberjacksPriorityMirrorRunner = null;
    _lumberjacksPriorityManifestListener?.Dispose();
    _lumberjacksPriorityManifestListener = null;
    _netcodeProbeRunner?.Dispose();
    _netcodeProbeRunner = null;
    _ownershipObserveRunner?.Dispose();
    _ownershipObserveRunner = null;
    _ownershipPinRunner?.Dispose();
    _ownershipPinRunner = null;
    _zdoRedirectRunner?.Dispose();
    _zdoRedirectRunner = null;
    _zdoInjectionRunner?.Dispose();
    _zdoInjectionRunner = null;
    _handshakeResponderRunner?.Dispose();
    _handshakeResponderRunner = null;
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
        "network_sense_godfly",
        "toggle god mode + debug-fly on the local player (client-safe; the vanilla 'god'/'fly' console commands are cheat-gated on a dedicated-server client): network_sense_godfly [on|off]",
        GodFlyCommand);
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
        "compare Lumberjacks authoritative movement against local Valheim motion without corrections: network_sense_lumberjacks_shadow [start|stop|status] [ws-url] [region-id] [input-hz]",
        LumberjacksShadowCommand);
    new Terminal.ConsoleCommand(
        "network_sense_lumberjacks_shadow_route",
        "run a teleport route with per-stop Lumberjacks shadow movement: network_sense_lumberjacks_shadow_route [teleport-route.tsv] [movement_only|stationary|axis_north|axis_east|axis_south|axis_west] [ws-url] [region-id] [input-hz]",
        LumberjacksShadowRouteCommand);
    new Terminal.ConsoleCommand(
        "network_sense_lumberjacks_priority_probe",
        "observe loaded Valheim objects and emit a Lumberjacks-ready priority manifest: network_sense_lumberjacks_priority_probe [start|stop|status] [radius] [scan-interval] [max-objects]",
        LumberjacksPriorityProbeCommand);
    new Terminal.ConsoleCommand(
        "network_sense_lumberjacks_priority_route",
        "run a teleport route with per-stop priority/load-order scans: network_sense_lumberjacks_priority_route [teleport-route.tsv] [radius] [scan-interval] [max-objects]",
        LumberjacksPriorityRouteCommand);
    new Terminal.ConsoleCommand(
        "network_sense_lumberjacks_priority_mirror",
        "mirror priority/load-order rows to Lumberjacks EventLog: network_sense_lumberjacks_priority_mirror [start|stop|status] [eventlog-url]",
        LumberjacksPriorityMirrorCommand);
    new Terminal.ConsoleCommand(
        "network_sense_lumberjacks_priority_route_mirror",
        "run a priority route and live-mirror per-stop batches to Lumberjacks EventLog: network_sense_lumberjacks_priority_route_mirror [teleport-route.tsv] [radius] [scan-interval] [max-objects] [eventlog-url]",
        LumberjacksPriorityRouteMirrorCommand);
    new Terminal.ConsoleCommand(
        "network_sense_lumberjacks_priority_manifest_listen",
        "listen for the reliable Lumberjacks priority_manifest broadcast: network_sense_lumberjacks_priority_manifest_listen [start|stop|status] [ws-url] [region-id]",
        LumberjacksPriorityManifestListenCommand);
    new Terminal.ConsoleCommand(
        "network_sense_lumberjacks_netcode_probe",
        "observe the live ZDO send/receive funnels to prove interception reachability (I1): network_sense_lumberjacks_netcode_probe [start|stop|status] [max-detail-rows]",
        LumberjacksNetcodeProbeCommand);
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

  // On-demand god+fly toggle for a client joined to a dedicated server, where the vanilla
  // 'god'/'fly' console commands are cheat-gated (Terminal.IsCheatsEnabled() == ZNet.IsServer(),
  // false on a client) and 'fly' is onlyServer. Drives the Player API directly, the same path the
  // route-walk safeguard (EnableRouteMovementSafeguards) uses. Client-only: Player.m_localPlayer is
  // null headless on the dedicated server, so this is a no-op warning there. Idempotent.
  object GodFlyCommand(Terminal.ConsoleEventArgs args) {
    Player player = Player.m_localPlayer;
    if (player == null) {
      string warn = "network_sense_godfly: no local player (join a world first).";
      MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, warn);
      LogWarning(warn);
      return warn;
    }

    string arg = args.Length >= 2 ? args[1].Trim().ToLowerInvariant() : "toggle";
    bool enable;
    switch (arg) {
      case "on": case "true": case "1": enable = true; break;
      case "off": case "false": case "0": enable = false; break;
      case "toggle": case "": enable = !player.InGodMode(); break;
      default:
        string usage = "Usage: network_sense_godfly [on|off]";
        MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, usage);
        LogWarning(usage);
        return usage;
    }

    string message;
    try {
      if (player.InGodMode() != enable) {
        player.SetGodMode(enable);
      }
      if (player.InDebugFlyMode() != enable) {
        player.ToggleDebugFly();
      }
      message = $"network_sense_godfly {(enable ? "ON" : "OFF")}: god={player.InGodMode()} fly={player.InDebugFlyMode()}";
      _coordinator?.RecordDevMarker($"godfly {(enable ? "on" : "off")} god={player.InGodMode()} fly={player.InDebugFlyMode()}");
    } catch (Exception ex) {
      message = $"network_sense_godfly failed: {ex.Message}";
      LogWarning(message);
    }
    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
    LogInfo(message);
    return message;
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

  object LumberjacksPriorityManifestListenCommand(Terminal.ConsoleEventArgs args) {
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
        string message = _lumberjacksPriorityManifestListener.Start(gatewayUrl, regionId, _coordinator);
        MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
        LogInfo(message);
        return message;
      }

      case "stop":
      case "off": {
        string message = _lumberjacksPriorityManifestListener.Stop(_coordinator);
        MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
        LogInfo(message);
        return message;
      }

      case "status": {
        string message = _lumberjacksPriorityManifestListener.GetStatus();
        _coordinator.RecordLumberjacksPriorityManifestListen(_lumberjacksPriorityManifestListener.BuildStatusRow("status"));
        MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
        LogInfo(message);
        return message;
      }

      default: {
        string message = "Usage: network_sense_lumberjacks_priority_manifest_listen [start|stop|status] [ws-url] [region-id]";
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
        float? inputHzOverride = TryParseOptionalInputHz(args, 4, out float inputHz) ? inputHz : (float?) null;
        string message = _lumberjacksShadowAuthorityRunner.Start(gatewayUrl, regionId, _coordinator, inputHzOverride);
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
        string message = "Usage: network_sense_lumberjacks_shadow [start|stop|status] [ws-url] [region-id] [input-hz]";
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
    float? inputHzOverride = TryParseOptionalInputHz(args, 5, out float inputHz) ? inputHz : (float?) null;

    fileName = NormalizeRouteFileName(fileName);
    profile = string.IsNullOrWhiteSpace(profile) ? "movement_only" : profile.Trim();
    string routePath = Path.Combine(Paths.ConfigPath, "comfy-network-sense", fileName);
    if (!TryLoadRouteStops(routePath, out List<RouteStop> stops, out string error)) {
      MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, error);
      LogWarning(error);
      return false;
    }

    StartCoroutine(RunLumberjacksShadowRoute(stops, routePath, profile, gatewayUrl, regionId, inputHzOverride));
    string inputHzText = inputHzOverride.HasValue ? $", inputHz={inputHzOverride.Value:0.##}" : string.Empty;
    string message = $"Lumberjacks shadow route started: {stops.Count} stops from {fileName}, profile={profile}{inputHzText}.";
    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
    LogInfo(message);
    return message;
  }

  object LumberjacksPriorityProbeCommand(Terminal.ConsoleEventArgs args) {
    string action = args.Length >= 2 ? args[1].Trim().ToLowerInvariant() : "start";

    switch (action) {
      case "start":
      case "run":
      case "on": {
        float? radiusOverride = TryParseOptionalPriorityRadius(args, 2, out float radius) ? radius : (float?) null;
        float? intervalOverride = TryParseOptionalPriorityInterval(args, 3, out float interval) ? interval : (float?) null;
        int? maxObjectsOverride = TryParseOptionalPriorityMaxObjects(args, 4, out int maxObjects) ? maxObjects : (int?) null;
        string message = _lumberjacksPriorityProbeRunner.Start(_coordinator, radiusOverride, intervalOverride, maxObjectsOverride);
        MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
        LogInfo(message);
        return message;
      }

      case "stop":
      case "off": {
        string message = _lumberjacksPriorityProbeRunner.Stop(_coordinator);
        MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
        LogInfo(message);
        return message;
      }

      case "status": {
        string message = _lumberjacksPriorityProbeRunner.GetStatus();
        _coordinator.RecordLumberjacksPriority(_lumberjacksPriorityProbeRunner.BuildStatusRow("status"));
        MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
        LogInfo(message);
        return message;
      }

      default: {
        string message = "Usage: network_sense_lumberjacks_priority_probe [start|stop|status] [radius] [scan-interval] [max-objects]";
        MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
        LogWarning(message);
        return false;
      }
    }
  }

  object LumberjacksNetcodeProbeCommand(Terminal.ConsoleEventArgs args) {
    string action = args.Length >= 2 ? args[1].Trim().ToLowerInvariant() : "start";

    switch (action) {
      case "start":
      case "run":
      case "on": {
        int? maxDetailRows =
            args.Length >= 3 && int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : (int?) null;
        string message = _netcodeProbeRunner.Start(_coordinator, maxDetailRows);
        MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
        LogInfo(message);
        return message;
      }

      case "stop":
      case "off": {
        string message = _netcodeProbeRunner.Stop();
        MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
        LogInfo(message);
        return message;
      }

      case "status": {
        string message = _netcodeProbeRunner.GetStatus();
        _coordinator.RecordNetcodeProbe(_netcodeProbeRunner.BuildStatusRow("status"));
        MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
        LogInfo(message);
        return message;
      }

      default: {
        string message = "Usage: network_sense_lumberjacks_netcode_probe [start|stop|status] [max-detail-rows]";
        MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
        LogWarning(message);
        return false;
      }
    }
  }

  object LumberjacksPriorityRouteCommand(Terminal.ConsoleEventArgs args) {
    if (_routeRunning) {
      string busyMessage = "NetworkSense route is already running.";
      MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, busyMessage);
      LogWarning(busyMessage);
      return false;
    }

    string fileName = args.Length >= 2 ? args[1] : "teleport-route.tsv";
    float? radiusOverride = TryParseOptionalPriorityRadius(args, 2, out float radius) ? radius : (float?) null;
    float? intervalOverride = TryParseOptionalPriorityInterval(args, 3, out float interval) ? interval : (float?) null;
    int? maxObjectsOverride = TryParseOptionalPriorityMaxObjects(args, 4, out int maxObjects) ? maxObjects : (int?) null;

    fileName = NormalizeRouteFileName(fileName);
    string routePath = Path.Combine(Paths.ConfigPath, "comfy-network-sense", fileName);
    if (!TryLoadRouteStops(routePath, out List<RouteStop> stops, out string error)) {
      MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, error);
      LogWarning(error);
      return false;
    }

    StartCoroutine(RunLumberjacksPriorityRoute(stops, routePath, radiusOverride, intervalOverride, maxObjectsOverride, null));
    string routeRadiusText = radiusOverride.HasValue
        ? radiusOverride.Value.ToString("0.#", CultureInfo.InvariantCulture) + "m"
        : "config";
    string message =
        $"Lumberjacks priority route started: {stops.Count} stops from {fileName}, radius={routeRadiusText}.";
    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
    LogInfo(message);
    return message;
  }

  object LumberjacksPriorityMirrorCommand(Terminal.ConsoleEventArgs args) {
    string action = args.Length >= 2 ? args[1].Trim().ToLowerInvariant() : "start";

    switch (action) {
      case "start":
      case "run":
      case "on": {
        string eventLogUrl = args.Length >= 3 ? args[2] : PluginConfig.LumberjacksEventLogUrl.Value;
        string message = _lumberjacksPriorityMirrorRunner.Start(eventLogUrl, _coordinator);
        MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
        LogInfo(message);
        return message;
      }

      case "stop":
      case "off": {
        string message = _lumberjacksPriorityMirrorRunner.Stop(_coordinator);
        MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
        LogInfo(message);
        return message;
      }

      case "status": {
        string message = _lumberjacksPriorityMirrorRunner.GetStatus();
        _coordinator.RecordLumberjacksPriorityMirror(_lumberjacksPriorityMirrorRunner.BuildStatusRow("status"));
        MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
        LogInfo(message);
        return message;
      }

      default: {
        string message = "Usage: network_sense_lumberjacks_priority_mirror [start|stop|status] [eventlog-url]";
        MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
        LogWarning(message);
        return false;
      }
    }
  }

  object LumberjacksPriorityRouteMirrorCommand(Terminal.ConsoleEventArgs args) {
    if (_routeRunning) {
      string busyMessage = "NetworkSense route is already running.";
      MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, busyMessage);
      LogWarning(busyMessage);
      return false;
    }

    string fileName = args.Length >= 2 ? args[1] : "teleport-route.tsv";
    float? radiusOverride = TryParseOptionalPriorityRadius(args, 2, out float radius) ? radius : (float?) null;
    float? intervalOverride = TryParseOptionalPriorityInterval(args, 3, out float interval) ? interval : (float?) null;
    int? maxObjectsOverride = TryParseOptionalPriorityMaxObjects(args, 4, out int maxObjects) ? maxObjects : (int?) null;
    string eventLogUrl = args.Length >= 6 ? args[5] : PluginConfig.LumberjacksEventLogUrl.Value;

    fileName = NormalizeRouteFileName(fileName);
    string routePath = Path.Combine(Paths.ConfigPath, "comfy-network-sense", fileName);
    if (!TryLoadRouteStops(routePath, out List<RouteStop> stops, out string error)) {
      MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, error);
      LogWarning(error);
      return false;
    }

    StartCoroutine(RunLumberjacksPriorityRoute(stops, routePath, radiusOverride, intervalOverride, maxObjectsOverride, eventLogUrl));
    string message =
        $"Lumberjacks priority route mirror started: {stops.Count} stops from {fileName}, eventLog={eventLogUrl}.";
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

  // P1 step 7: optionally couple the auto-rehearsal route walk to the netcode probe auto-start,
  // so captured ZDO traffic exists without a human hand-walking the route. Client-side only —
  // RunTeleportRoute needs a local player, so this is skipped headless (the dedicated server keeps
  // the probe running solo). Reuses the same TryStartRehearsal kickoff and _autoRehearsalStarted
  // run-once guard as TryStartAutoRehearsal. Off by default.
  void TryCoupleAutoRehearsalToNetcodeProbe() {
    if (!PluginConfig.CoupleAutoRehearsalToNetcodeProbe.Value) {
      return;
    }

    if (_autoRehearsalStarted && PluginConfig.AutoRehearsalRunOncePerSession.Value) {
      return;
    }

    Player player = Player.m_localPlayer;
    if (player == null) {
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

    _coordinator.RecordDevMarker("auto_rehearsal coupled_to_netcode_probe");
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

  // Enable god mode + debug-fly on the local player before a teleport walk begins, so a fall after
  // any teleport can't kill the character mid-route (a death aborts the walk and drags Derek back
  // into the loop — the exact KVM regression the hands-free rig exists to prevent). We call the
  // Player API directly rather than issuing the 'god'/'fly' console commands: those are cheat-gated
  // (Terminal.IsCheatsEnabled() returns ZNet.IsServer(), which is false on a client joined to the
  // dedicated am4 server) and 'fly' is onlyServer, so the console strings would be rejected
  // client-side and the character would still fall. Direct calls also avoid devcommands' RemoteCommand
  // side effect on the authoritative server during baseline capture. Idempotent; try/caught so a
  // not-yet-networked player (null m_nview in ToggleDebugFly) degrades to a warning, never a route
  // abort. No-op headless (Player.m_localPlayer is null on the dedicated server).
  void EnableRouteMovementSafeguards() {
    if (!PluginConfig.RouteGodFlySafeguard.Value) {
      return;
    }

    Player player = Player.m_localPlayer;
    if (player == null) {
      return;
    }

    try {
      if (!player.InGodMode()) {
        player.SetGodMode(true);
      }
      if (!player.InDebugFlyMode()) {
        player.ToggleDebugFly();
      }
      string state = $"god={player.InGodMode()} fly={player.InDebugFlyMode()}";
      _coordinator?.RecordDevMarker($"route_safeguards {state}");
      LogInfo($"route movement safeguards on: {state}");
    } catch (Exception ex) {
      LogWarning($"route movement safeguards failed: {ex.Message}");
    }
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

    EnableRouteMovementSafeguards();

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
      string regionId,
      float? inputHzOverride) {
    _routeRunning = true;
    bool aborted = false;
    string activeStopId = string.Empty;
    ShadowRouteMovementKind movementKind = ResolveShadowRouteMovementKind(profile);
    bool moveDuringBenchmark = movementKind != ShadowRouteMovementKind.Stationary;

    NetworkSensePerfProbe.SetRouteState("shadow_route", "", "start");
    string inputHzText = inputHzOverride.HasValue ? $" inputHz={inputHzOverride.Value:0.##}" : string.Empty;
    _coordinator.RecordDevMarker(
        $"lumberjacks_shadow_route {profile} start stops={stops.Count} file={Path.GetFileName(routePath)} movement={movementKind}{inputHzText}");

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
          string startMessage = _lumberjacksShadowAuthorityRunner.Start(gatewayUrl, regionId, _coordinator, inputHzOverride);
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
                StepRouteMovementPattern(currentPlayer, origin, Time.realtimeSinceStartup - benchmarkStartedAt, movementKind);
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

  IEnumerator RunLumberjacksPriorityRoute(
      List<RouteStop> stops,
      string routePath,
      float? radiusOverride,
      float? intervalOverride,
      int? maxObjectsOverride,
      string priorityMirrorEventLogUrl) {
    _routeRunning = true;
    bool aborted = false;
    bool mirrorStarted = false;
    string activeStopId = string.Empty;

    NetworkSensePerfProbe.SetRouteState("priority_route", "", "start");
    string radiusText = radiusOverride.HasValue ? $" radius={radiusOverride.Value:0.#}m" : string.Empty;
    string intervalText = intervalOverride.HasValue ? $" interval={intervalOverride.Value:0.#}s" : string.Empty;
    string maxText = maxObjectsOverride.HasValue ? $" maxObjects={maxObjectsOverride.Value}" : string.Empty;
    _coordinator.RecordDevMarker(
        $"lumberjacks_priority_route start stops={stops.Count} file={Path.GetFileName(routePath)}{radiusText}{intervalText}{maxText}");
    if (!string.IsNullOrWhiteSpace(priorityMirrorEventLogUrl)) {
      string mirrorMessage = _lumberjacksPriorityMirrorRunner.Start(priorityMirrorEventLogUrl, _coordinator);
      mirrorStarted = _lumberjacksPriorityMirrorRunner.IsRunning;
      LogInfo(mirrorMessage);
      _coordinator.RecordDevMarker($"lumberjacks_priority_route mirror_start {mirrorMessage}");
    }

    try {
      foreach (RouteStop stop in stops) {
        activeStopId = stop.Id;
        if (!TryGetUsableLocalPlayer(out Player player)) {
          NetworkSensePerfProbe.SetRouteState("priority_route", stop.Id, "abort_missing_player");
          _coordinator.RecordDevMarker($"lumberjacks_priority_route abort {stop.Id} missing_player");
          aborted = true;
          break;
        }

        NetworkSensePerfProbe.SetRouteState("priority_route", stop.Id, "resolve_target");
        Vector3 target;
        using (NetworkSensePerfProbe.Measure("RunLumberjacksPriorityRoute.ResolveRouteTarget")) {
          target = ResolveRouteTarget(stop);
        }

        _coordinator.RecordDevMarker($"{stop.Id} priority_route_start");

        NetworkSensePerfProbe.SetRouteState("priority_route", stop.Id, "teleport");
        bool moved;
        using (NetworkSensePerfProbe.Measure("RunLumberjacksPriorityRoute.TryTeleport")) {
          moved = TryTeleport(player, target);
        }
        _coordinator.RecordDevMarker($"{stop.Id} priority_route_teleport moved={moved} target={FormatVector(target)}");

        if (stop.SettleSeconds > 0.0f) {
          NetworkSensePerfProbe.SetRouteState("priority_route", stop.Id, "settle");
          yield return new WaitForSeconds(stop.SettleSeconds);
        }

        NetworkSensePerfProbe.SetRouteState("priority_route", stop.Id, "wait_local_player");
        yield return WaitForUsableLocalPlayer("priority_route", stop.Id, "wait_local_player", 45.0f);
        if (!TryGetUsableLocalPlayer(out _)) {
          NetworkSensePerfProbe.SetRouteState("priority_route", stop.Id, "abort_local_player_not_ready");
          _coordinator.RecordDevMarker($"lumberjacks_priority_route abort {stop.Id} local_player_not_ready_after_teleport");
          aborted = true;
          break;
        }

        bool probeStarted = false;
        bool durationOverridden = false;
        float previousDuration = PluginConfig.BenchmarkDurationSeconds.Value;

        try {
          _lumberjacksPriorityProbeRunner.SetRouteContext("priority_route", stop.Id, "start");
          string startMessage =
              _lumberjacksPriorityProbeRunner.Start(_coordinator, radiusOverride, intervalOverride, maxObjectsOverride);
          probeStarted = _lumberjacksPriorityProbeRunner.IsRunning;
          _lumberjacksPriorityProbeRunner.SetRouteContext("priority_route", stop.Id, "scan_window");
          _coordinator.RecordLumberjacksPriority(_lumberjacksPriorityProbeRunner.BuildStatusRow("route_start"));
          LogInfo(startMessage);
          _coordinator.RecordDevMarker($"{stop.Id} priority_probe_start {startMessage}");

          float benchmarkSeconds = Mathf.Max(1.0f, stop.BenchmarkSeconds);
          PluginConfig.BenchmarkDurationSeconds.Value = benchmarkSeconds;
          durationOverridden = true;

          NetworkSensePerfProbe.SetRouteState("priority_route", stop.Id, "benchmark_start");
          if (!_coordinator.BenchmarkRunning) {
            _coordinator.StartBenchmark();
          }

          float benchmarkStartedAt = Time.realtimeSinceStartup;
          float maxWaitSeconds = benchmarkSeconds + 15.0f;
          NetworkSensePerfProbe.SetRouteState("priority_route", stop.Id, "scan_window");
          while (_coordinator.BenchmarkRunning && Time.realtimeSinceStartup - benchmarkStartedAt < maxWaitSeconds) {
            _lumberjacksPriorityProbeRunner.SetRouteContext("priority_route", stop.Id, "scan_window");
            yield return null;
          }

          if (_coordinator.BenchmarkRunning) {
            NetworkSensePerfProbe.SetRouteState("priority_route", stop.Id, "benchmark_timeout_cancel");
            _coordinator.CancelBenchmark();
            _coordinator.RecordDevMarker($"{stop.Id} priority_route_benchmark_cancelled_after_timeout");
          }

          _coordinator.ConsumeLatestBenchmarkResult();
        } finally {
          if (durationOverridden) {
            PluginConfig.BenchmarkDurationSeconds.Value = previousDuration;
          }

          if (probeStarted && _lumberjacksPriorityProbeRunner.IsRunning) {
            NetworkSensePerfProbe.SetRouteState("priority_route", stop.Id, "priority_stop");
            _lumberjacksPriorityProbeRunner.SetRouteContext("priority_route", stop.Id, aborted ? "route_abort" : "route_stop");
            _coordinator.RecordLumberjacksPriority(_lumberjacksPriorityProbeRunner.BuildStatusRow(aborted ? "route_abort" : "route_stop"));
            string stopMessage = _lumberjacksPriorityProbeRunner.Stop(_coordinator);
            LogInfo(stopMessage);
            _coordinator.RecordDevMarker($"{stop.Id} priority_probe_end {stopMessage}");
          }
        }

        if (aborted) {
          break;
        }

        _coordinator.RecordDevMarker($"{stop.Id} priority_route_end");
        yield return new WaitForSeconds(0.5f);
      }
    } finally {
      if (_lumberjacksPriorityProbeRunner.IsRunning) {
        _lumberjacksPriorityProbeRunner.SetRouteContext("priority_route", activeStopId, "route_abort");
        _coordinator.RecordLumberjacksPriority(_lumberjacksPriorityProbeRunner.BuildStatusRow("route_abort"));
        string stopMessage = _lumberjacksPriorityProbeRunner.Stop(_coordinator);
        LogInfo(stopMessage);
      }

      NetworkSensePerfProbe.SetRouteState("priority_route", "", aborted ? "abort" : "end");
      _coordinator.RecordDevMarker(aborted ? "lumberjacks_priority_route abort" : "lumberjacks_priority_route end");
      if (mirrorStarted && _lumberjacksPriorityMirrorRunner.IsRunning) {
        string mirrorStopMessage = _lumberjacksPriorityMirrorRunner.Stop(_coordinator);
        LogInfo(mirrorStopMessage);
        _coordinator.RecordDevMarker($"lumberjacks_priority_route mirror_stop {mirrorStopMessage}");
      }

      NetworkSensePerfProbe.SetRouteState("priority_route", "", "export");
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

  static void StepRouteMovementPattern(
      Player player,
      Vector3 origin,
      float elapsedSeconds,
      ShadowRouteMovementKind movementKind) {
    using NetworkSensePerfProbe.Section section = NetworkSensePerfProbe.Measure("ComfyNetworkSense.StepRouteMovementPattern");

    try {
      Vector3 offset = ResolveRouteMovementOffset(elapsedSeconds, movementKind);
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

  static Vector3 ResolveRouteMovementOffset(float elapsedSeconds, ShadowRouteMovementKind movementKind) {
    switch (movementKind) {
      case ShadowRouteMovementKind.AxisNorth:
        return new Vector3(0.0f, 0.0f, TriangleWave(elapsedSeconds, radius: 8.0f, speedMetersPerSecond: 4.0f));
      case ShadowRouteMovementKind.AxisEast:
        return new Vector3(TriangleWave(elapsedSeconds, radius: 8.0f, speedMetersPerSecond: 4.0f), 0.0f, 0.0f);
      case ShadowRouteMovementKind.AxisSouth:
        return new Vector3(0.0f, 0.0f, -TriangleWave(elapsedSeconds, radius: 8.0f, speedMetersPerSecond: 4.0f));
      case ShadowRouteMovementKind.AxisWest:
        return new Vector3(-TriangleWave(elapsedSeconds, radius: 8.0f, speedMetersPerSecond: 4.0f), 0.0f, 0.0f);
      case ShadowRouteMovementKind.Circle:
      default:
        const float radius = 4.0f;
        float angle = elapsedSeconds * 1.5f;
        return new Vector3(Mathf.Cos(angle) * radius, 0.0f, Mathf.Sin(angle) * radius);
    }
  }

  static float TriangleWave(float elapsedSeconds, float radius, float speedMetersPerSecond) {
    float speed = Mathf.Max(0.1f, speedMetersPerSecond);
    float segmentSeconds = Mathf.Max(0.1f, radius / speed);
    float periodSeconds = segmentSeconds * 4.0f;
    float phase = elapsedSeconds % periodSeconds;

    if (phase < segmentSeconds) {
      return phase * speed;
    }

    if (phase < segmentSeconds * 3.0f) {
      return radius - (phase - segmentSeconds) * speed;
    }

    return -radius + (phase - segmentSeconds * 3.0f) * speed;
  }

  static ShadowRouteMovementKind ResolveShadowRouteMovementKind(string profile) {
    string normalized = (profile ?? string.Empty).Trim().ToLowerInvariant();
    switch (normalized) {
      case "stationary":
      case "static":
      case "idle":
        return ShadowRouteMovementKind.Stationary;
      case "axis_north":
      case "north":
      case "cardinal_north":
      case "line_north":
        return ShadowRouteMovementKind.AxisNorth;
      case "axis_east":
      case "east":
      case "cardinal_east":
      case "line_east":
        return ShadowRouteMovementKind.AxisEast;
      case "axis_south":
      case "south":
      case "cardinal_south":
      case "line_south":
        return ShadowRouteMovementKind.AxisSouth;
      case "axis_west":
      case "west":
      case "cardinal_west":
      case "line_west":
        return ShadowRouteMovementKind.AxisWest;
      case "movement_only":
      case "shadow_movement":
      case "circle":
      case "circle_movement":
        return ShadowRouteMovementKind.Circle;
      default:
        return ShadowRouteMovementKind.Stationary;
    }
  }

  static bool TryParseRouteFloat(string value, out float result) {
    return float.TryParse((value ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
  }

  static bool TryParseOptionalInputHz(Terminal.ConsoleEventArgs args, int index, out float inputHz) {
    inputHz = 0.0f;
    if (args == null || args.Length <= index) {
      return false;
    }

    if (!float.TryParse(args[index], NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)) {
      return false;
    }

    inputHz = Mathf.Clamp(parsed, 1.0f, 60.0f);
    return true;
  }

  static bool TryParseOptionalPriorityRadius(Terminal.ConsoleEventArgs args, int index, out float radius) {
    radius = 0.0f;
    if (args == null || args.Length <= index) {
      return false;
    }

    if (!float.TryParse(args[index], NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)) {
      return false;
    }

    radius = Mathf.Clamp(parsed, 8.0f, 256.0f);
    return true;
  }

  static bool TryParseOptionalPriorityInterval(Terminal.ConsoleEventArgs args, int index, out float intervalSeconds) {
    intervalSeconds = 0.0f;
    if (args == null || args.Length <= index) {
      return false;
    }

    if (!float.TryParse(args[index], NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)) {
      return false;
    }

    intervalSeconds = Mathf.Clamp(parsed, 0.5f, 30.0f);
    return true;
  }

  static bool TryParseOptionalPriorityMaxObjects(Terminal.ConsoleEventArgs args, int index, out int maxObjects) {
    maxObjects = 0;
    if (args == null || args.Length <= index) {
      return false;
    }

    if (!int.TryParse(args[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)) {
      return false;
    }

    maxObjects = Mathf.Clamp(parsed, 1, 512);
    return true;
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
