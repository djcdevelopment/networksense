namespace ComfyNetworkSense;

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using BepInEx;
using BepInEx.Logging;

using HarmonyLib;
using Lumberjacks.Contracts.Valheim;

using UnityEngine;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class ComfyNetworkSense : BaseUnityPlugin {
  public const string PluginGuid = "djcdevelopment.valheim.comfynetworksense";
  public const string PluginName = "ComfyNetworkSense";
  public const string PluginVersion = "0.5.80";

  // The release this build belongs to, as named by the release manifest (e.g. "m1-clean-20260717-r1").
  // The handshake sends it so the Gateway can refuse to hand a strict verdict to a mod too old to
  // enforce one (M1 risk 9): a stale mod fails OPEN on a reject, so an authority that believes it is
  // rejecting while the mod waves players through is worse than no gate at all.
  //
  // It is a COMPATIBILITY signal, not an authenticated one - a volunteer can edit this constant and
  // claim anything, and nothing here stops that. What it catches is drift, and it catches the case
  // that matters without any cooperation from the mod: a build predating this field sends no release
  // identity, so absence IS the staleness signal.
  //
  // Hand-set at the release cut, exactly like PluginVersion above, and deliberately NOT computed at
  // runtime from the DLL's own hash: the code doing the hashing is the DLL, so it would buy no
  // assurance for its cost. "dev" means an uncut local build, which is never a release.
  public const string ReleaseId = "m7-c10a-20260802-r41";

  public static ComfyNetworkSense Instance { get; private set; }

  static ManualLogSource _logger;
  static readonly ConcurrentQueue<string> _mainThreadMessages = new();
  TelemetryCoordinator _coordinator;
  LumberjacksBridgeProbe _lumberjacksBridgeProbe;
  LumberjacksProjectionRunner _lumberjacksProjectionRunner;
  LumberjacksShadowAuthorityRunner _lumberjacksShadowAuthorityRunner;
  LumberjacksPriorityProbeRunner _lumberjacksPriorityProbeRunner;
  LumberjacksPriorityMirrorRunner _lumberjacksPriorityMirrorRunner;
  LumberjacksPriorityManifestListener _lumberjacksPriorityManifestListener;
  LumberjacksGameSessionRunner _lumberjacksGameSessionRunner;
  LumberjacksMotionRunner _lumberjacksMotionRunner;
  MotionTestController _motionTestController;
  NativeCutoverScenarioController _nativeCutoverScenarioController;
  NetcodeProbeRunner _netcodeProbeRunner;
  ZdoRedirectRunner _zdoRedirectRunner;
  GameplayEventProducer _gameplayEventProducer;
  ZdoAuthoritativeConsumerRunner _zdoAuthoritativeConsumerRunner;
  HandshakeResponderRunner _handshakeResponderRunner;
  ServerRuntimeControlRunner _serverRuntimeControlRunner;
  NativeNetworkLedger _nativeNetworkLedger;
  DirectControlCutoverRunner _directControlCutoverRunner;
  RoutedRpcCutoverRunner _routedRpcCutoverRunner;
  ShipCutoverRunner _shipCutoverRunner;
  SaddleCutoverRunner _saddleCutoverRunner;
  CreatureAiCutoverRunner _creatureAiCutoverRunner;
  ContainerCutoverRunner _containerCutoverRunner;
  ZdoJournalCutoverRunner _zdoJournalCutoverRunner;
  OwnershipLeaseCutoverRunner _ownershipLeaseCutoverRunner;
  WorldZoneCutoverRunner _worldZoneCutoverRunner;
  LogicalPeerCutoverRunner _logicalPeerCutoverRunner;
  SocketQuarantineCutoverRunner _socketQuarantineCutoverRunner;
  readonly TransportStatusOverlay _transportStatusOverlay = new();
  Harmony _harmony;
  bool _routeRunning;
  float _nextPrimaryRedirectStartAt;
  string _lastPrimaryRedirectStartMessage = string.Empty;
  int _mcpProbeInFlight;
  float _nextMcpProbeAt;
  volatile bool _mcpReachable;

  // Auto-port test harness (autoPortOnJoinEnabled). Server-side: push the densest-region coordinate
  // to each newly-joined peer once. Client-side: HandleAutoPort receives it and, if opted in, runs
  // the delayed god/fly + teleport coroutine.
  public const string AutoPortRpc = ValheimRoutedRpcAdmissions.ModAutoPort;
  ZRoutedRpc _autoPortRegisteredRpc;
  readonly System.Collections.Generic.HashSet<long> _autoPortPushedPeers = new();

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
    AlphaTransportSwitches.Reset(
        PluginConfig.LumberjacksMotionApplyEnabled.Value,
        PluginConfig.McpEnabled.Value);
    _nativeNetworkLedger = new();
    _directControlCutoverRunner = new();

    _coordinator = new();
    _lumberjacksBridgeProbe = new();
    _lumberjacksProjectionRunner = new();
    _lumberjacksShadowAuthorityRunner = new();
    _lumberjacksPriorityProbeRunner = new();
    _lumberjacksPriorityMirrorRunner = new();
    _coordinator.SetLumberjacksPriorityMirror(_lumberjacksPriorityMirrorRunner);
    _coordinator.SetLumberjacksReplacementTelemetryProvider(GetLumberjacksReplacementTelemetry);
    _lumberjacksPriorityManifestListener = new();
    _lumberjacksGameSessionRunner = new();
    _routedRpcCutoverRunner = new(_lumberjacksGameSessionRunner);
    _shipCutoverRunner = new(_routedRpcCutoverRunner);
    _saddleCutoverRunner = new(
        _lumberjacksGameSessionRunner, _routedRpcCutoverRunner);
    _creatureAiCutoverRunner = new(_saddleCutoverRunner);
    _containerCutoverRunner = new(_routedRpcCutoverRunner);
    _zdoJournalCutoverRunner = new(_lumberjacksGameSessionRunner);
    _ownershipLeaseCutoverRunner =
        new(_lumberjacksGameSessionRunner);
    _worldZoneCutoverRunner = new(_lumberjacksGameSessionRunner);
    _logicalPeerCutoverRunner =
        new(_lumberjacksGameSessionRunner, _worldZoneCutoverRunner);
    _socketQuarantineCutoverRunner =
        new(_lumberjacksGameSessionRunner, _worldZoneCutoverRunner);
    _lumberjacksMotionRunner = new(_lumberjacksGameSessionRunner);
    _motionTestController = new(RecordTransportControl);
    _nativeCutoverScenarioController =
        new(
            _lumberjacksGameSessionRunner,
            _routedRpcCutoverRunner,
            _shipCutoverRunner,
            _saddleCutoverRunner,
            _creatureAiCutoverRunner,
            _containerCutoverRunner,
            _zdoJournalCutoverRunner,
            _ownershipLeaseCutoverRunner,
            _worldZoneCutoverRunner,
            _lumberjacksMotionRunner,
            _socketQuarantineCutoverRunner);
    _netcodeProbeRunner = new();
    _zdoRedirectRunner = new();
    _gameplayEventProducer = new();
    _handshakeResponderRunner = new();
    _serverRuntimeControlRunner = new(ApplyServerRuntimeControl);
    _zdoAuthoritativeConsumerRunner = new();
    InitializeAuthoritativeConsumer();

    _harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), harmonyInstanceId: PluginGuid);
    PanelInputPatches.Apply(_harmony);
    GameplayEventPatches.Apply(_harmony);
    // This is deliberately separate from the normal player path. It is an opt-in
    // selector for disposable profile-gated headless/rendered lab clients only.
    LabAutoJoinPatches.Apply(_harmony);
    RegisterConsoleCommands();
    LoadQuestView();

    LogInfo("Telemetry scaffold ready.");
    LogInfo("Lumberjacks contract release=" + ReleaseId
        + " schema_version=" + ZdoIntegrationContract.SchemaVersion
        + " operation=" + ZdoIntegrationContract.Operation);
  }

  // Loads the player's quest-view.json (quest-evaluator track). The file always loads; matching is
  // separately gated by questEvaluatorEnabled, so a bad file surfaces at startup, not first kill.
  // A missing file is normal (no quests tracked) and logs nothing alarming.
  internal static string QuestViewPath => Path.Combine(Paths.ConfigPath, "comfy-network-sense", "quest-view.json");

  void LoadQuestView() {
    string path = QuestViewPath;
    if (QuestViewLoader.Load(path)) {
      if (QuestViewLoader.Quests.Count > 0) {
        LogInfo("Quest view loaded: " + QuestViewLoader.Quests.Count + " tracked quest(s)"
            + (string.IsNullOrEmpty(QuestViewLoader.PlayerName) ? "" : " for " + QuestViewLoader.PlayerName)
            + " from " + path);
      }
    } else {
      LogWarning("Quest view failed to load (" + QuestViewLoader.LastError + "); no quests tracked. File: " + path);
    }
  }

  void InitializeAuthoritativeConsumer() {
    try {
      string authoritativeWindow = PluginConfig.LumberjacksAuthoritativeWindowId.Value;
      if (string.IsNullOrWhiteSpace(authoritativeWindow)) {
        authoritativeWindow = string.IsNullOrWhiteSpace(PluginConfig.LumberjacksEnrollmentManifestId.Value)
            ? Environment.GetEnvironmentVariable("COMFY_LUMBERJACKS_ENROLLMENT_MANIFEST_ID")
            : PluginConfig.LumberjacksEnrollmentManifestId.Value;
      }
      LogInfo("Authoritative consumer init: enabled=" + PluginConfig.ZdoAuthoritativeConsumerEnabled.Value
          + ", manifest=" + (authoritativeWindow ?? "")
          + ", gateway=" + PluginConfig.LumberjacksGatewayUrl.Value);
      if (!PluginConfig.ZdoAuthoritativeConsumerEnabled.Value || string.IsNullOrWhiteSpace(authoritativeWindow)) {
        LogInfo("Authoritative consumer init: disabled or unenrolled");
        return;
      }
      string endpoint = PluginConfig.LumberjacksGatewayUrl.Value.Replace("ws://", "http://").Replace("wss://", "https://");
      LogInfo("Authoritative consumer init: " + _zdoAuthoritativeConsumerRunner.Start(endpoint, authoritativeWindow));
    } catch (Exception exception) {
      LogWarning("Authoritative consumer init failed: " + exception.GetType().Name + ": " + exception.Message);
    }
  }

  Dictionary<string, object> GetLumberjacksReplacementTelemetry() {
    Dictionary<string, object> result = new();
    Dictionary<string, object> handshake = _handshakeResponderRunner?.GetTelemetrySnapshot();
    Dictionary<string, object> redirect = _zdoRedirectRunner?.BuildStatusRow("heartbeat") as Dictionary<string, object>;
    Dictionary<string, object> authoritative = _zdoAuthoritativeConsumerRunner?.Snapshot();
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
      bool allPrefabs = redirect.TryGetValue("all_prefabs", out object allPrefabsValue)
          && Convert.ToBoolean(allPrefabsValue);
      if (allPrefabs) {
        long total = Convert.ToInt64(suppressed ?? 0L);
        long routed = Convert.ToInt64(received ?? 0L);
        result["coverage_total"] = total;
        result["coverage_lumberjacks"] = routed;
        result["coverage_native_only"] = Math.Max(0L, total - routed);
        result["native_fallbacks"] = 0L;
      }
    }
    // injection_applied / _rendered / _rejected were dropped 2026-07-21 with the P5
    // synthetic-injection runner. The gateway's ValheimTelemetryHeartbeat still declares
    // those three fields as nullable, so they simply arrive unset rather than breaking the
    // contract; no gateway change is required for this removal.
    if (authoritative != null) foreach (var pair in authoritative) result["zdo_authoritative_" + (pair.Key == "authoritative_enabled" ? "enabled" : pair.Key)] = pair.Value;
    IDictionary<string, object> motion = _lumberjacksMotionRunner?.Snapshot();
    if (motion != null) foreach (var pair in motion) result["motion_" + pair.Key] = pair.Value;
    IDictionary<string, object> gameSession = _lumberjacksGameSessionRunner?.Snapshot();
    if (gameSession != null) foreach (var pair in gameSession) result["game_session_" + pair.Key] = pair.Value;
    if (netcode != null) {
      result["zdo_probe_running"] = netcode.TryGetValue("running", out object running) ? running : null;
      result["zdo_probe_recv_rows"] = netcode.TryGetValue("recv_zdo_rows", out object recv) ? recv : null;
      result["zdo_probe_send_rows"] = netcode.TryGetValue("send_zdo_rows", out object send) ? send : null;
      result["zdo_probe_recv_calls"] = netcode.TryGetValue("recv_funnel_calls", out object recvCalls) ? recvCalls : null;
      result["zdo_probe_create_sync_calls"] = netcode.TryGetValue("create_sync_list_calls", out object syncCalls) ? syncCalls : null;
    }
    Dictionary<string, object> nativeNetwork = _nativeNetworkLedger?.Snapshot();
    if (nativeNetwork != null) {
      foreach (var pair in nativeNetwork) result["native_network_" + pair.Key] = pair.Value;
    }
    IDictionary<string, object> directControl = _directControlCutoverRunner?.Snapshot();
    if (directControl != null) {
      foreach (var pair in directControl) result["direct_control_" + pair.Key] = pair.Value;
    }
    IDictionary<string, object> journal = _zdoJournalCutoverRunner?.Snapshot();
    if (journal != null) {
      foreach (var pair in journal) result["zdo_journal_" + pair.Key] = pair.Value;
    }
    Dictionary<string, object> sendCadence = ZdoSendCadenceOverride.Snapshot();
    foreach (var pair in sendCadence) result[pair.Key] = pair.Value;
    return result;
  }

  void Update() {
    if (!PluginConfig.IsModEnabled.Value) {
      return;
    }

    LabAutoJoinPatches.PollJoined();

    float deltaTime = Time.unscaledDeltaTime;
    float now = Time.unscaledTime;
    _nativeNetworkLedger?.Update(now);
    _directControlCutoverRunner?.Update(now);
    _serverRuntimeControlRunner?.Update(now, _coordinator);
    _zdoRedirectRunner?.MaintainPrimaryWindow(now);
    TryEnsurePrimaryRedirect(now);
    TickAutoPort(now);
    _zdoAuthoritativeConsumerRunner?.Update(Time.unscaledTime);
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

    // One section per runner, not one section for the group. This block used to open a single
    // section named "LumberjacksMotionRunner.Update" around all eight runners, so every
    // over-threshold row charged seven other runners' cost to the motion runner. That is not a
    // hypothetical: it sent the first pass of the C9 motion-quality analysis at the wrong
    // component, and a reproducible ~1.9s (OMEN) / ~2.3s (i5) once-per-session cost was written
    // into evidence as a motion defect. It was WorldGenerator.Initialize - Valheim's own world
    // pregeneration - called from LogicalPeerCutoverRunner.ConstructPeer, which vanilla runs at
    // the same point from ZNet.RPC_PeerInfo, so it was never even a regression. Per-runner
    // sections would have named the culprit on the first read. See
    // fieldlab/evidence/c9-motion-quality/README.md. The outer roll-up keeps the group total
    // answerable and is the honest name for what the old section actually measured.
    using (NetworkSensePerfProbe.Measure("ComfyNetworkSense.CutoverRunners.Update")) {
      using (NetworkSensePerfProbe.Measure("ComfyNetworkSense.LumberjacksGameSessionRunner.Update")) {
        _lumberjacksGameSessionRunner?.Update(now);
      }

      // Apply world descriptors, authenticated logical-peer controls, and
      // canonical reference motion before any routed gameplay semantic tries
      // to resolve a sender. Dedicated servers intentionally have no remote
      // Player presentation object; this peer state is their authority input.
      using (NetworkSensePerfProbe.Measure("ComfyNetworkSense.WorldZoneCutoverRunner.Update")) {
        _worldZoneCutoverRunner?.Update(now);
      }

      using (NetworkSensePerfProbe.Measure("ComfyNetworkSense.LogicalPeerCutoverRunner.Update")) {
        _logicalPeerCutoverRunner?.Update(now);
      }

      using (NetworkSensePerfProbe.Measure("ComfyNetworkSense.LumberjacksMotionRunner.Update")) {
        _lumberjacksMotionRunner?.Update(now);
      }

      // Register the typed C3 handler before the routed adapter drains a just-arrived request.
      using (NetworkSensePerfProbe.Measure("ComfyNetworkSense.ZdoJournalCutoverRunner.Update")) {
        _zdoJournalCutoverRunner?.Update(now);
      }

      // Register ship runtime handlers before the generic adapter drains
      // reliable deliveries, then publish owner snapshots into that adapter.
      using (NetworkSensePerfProbe.Measure("ComfyNetworkSense.ShipCutoverRunner.Update")) {
        _shipCutoverRunner?.Update(now);
      }

      using (NetworkSensePerfProbe.Measure("ComfyNetworkSense.SaddleCutoverRunner.Update")) {
        _saddleCutoverRunner?.Update(now);
      }

      using (NetworkSensePerfProbe.Measure("ComfyNetworkSense.CreatureAiCutoverRunner.Update")) {
        _creatureAiCutoverRunner?.Update(now);
      }

      using (NetworkSensePerfProbe.Measure("ComfyNetworkSense.ContainerCutoverRunner.Update")) {
        _containerCutoverRunner?.Update(now);
      }

      using (NetworkSensePerfProbe.Measure("ComfyNetworkSense.RoutedRpcCutoverRunner.Update")) {
        _routedRpcCutoverRunner?.Update(now);
      }

      using (NetworkSensePerfProbe.Measure("ComfyNetworkSense.OwnershipLeaseCutoverRunner.Update")) {
        _ownershipLeaseCutoverRunner?.Update(now);
      }

      using (NetworkSensePerfProbe.Measure("ComfyNetworkSense.SocketQuarantineCutoverRunner.Update")) {
        _socketQuarantineCutoverRunner?.Update(now);
      }
    }

    using (NetworkSensePerfProbe.Measure("ComfyNetworkSense.MotionTestController.Update")) {
      _motionTestController?.Update();
    }

    using (NetworkSensePerfProbe.Measure("ComfyNetworkSense.NativeCutoverScenarioController.Update")) {
      _nativeCutoverScenarioController?.Update(now);
    }

    using (NetworkSensePerfProbe.Measure("ComfyNetworkSense.ZdoInjectionRunner.Update")) {
    }

    // P6/I5 handshake responder self-arms on the server (the handshake fires at connect time,
    // before any netcode-probe window) and stays armed until config-off + restart.
    using (NetworkSensePerfProbe.Measure("ComfyNetworkSense.HandshakeResponderRunner.Update")) {
      _handshakeResponderRunner?.Update(deltaTime, _coordinator);
    }

    using (NetworkSensePerfProbe.Measure("ComfyNetworkSense.GameplayEventProducer.Update")) {
      _gameplayEventProducer?.Update(deltaTime, _coordinator);
    }

    UpdateMcpHealth(now);
  }

  void LateUpdate() {
    if (!PluginConfig.IsModEnabled.Value) return;
    _lumberjacksMotionRunner?.LateUpdate(Time.unscaledDeltaTime);
    _saddleCutoverRunner?.LateUpdate(Time.unscaledDeltaTime);
  }

  // Arms the outbound ZDO redirect for lumberjacks-primary: server-side, once peers are
  // present, retried on a backoff. This is the PRODUCTION arming path and is independent of
  // any probe window. (The comment that used to sit here described TryDriveNetcodeProbeAuto,
  // a different method that has since been removed with the P3/P5 lab experiments.)
  void TryEnsurePrimaryRedirect(float now) {
    if (_zdoRedirectRunner == null || !PluginConfig.ZdoRedirectEnabled.Value
        || !string.Equals(TelemetryCoordinator.EffectiveCutoverMode(), "lumberjacks-primary",
            StringComparison.OrdinalIgnoreCase)) return;

    ZNet znet = ZNet.instance;
    if (znet == null || !znet.IsServer()) return;
    if ((znet.GetPeers()?.Count ?? 0) == 0) {
      _nextPrimaryRedirectStartAt = 0;
      _lastPrimaryRedirectStartMessage = string.Empty;
      return;
    }
    if (_zdoRedirectRunner.IsRunning || now < _nextPrimaryRedirectStartAt) return;

    _nextPrimaryRedirectStartAt = now + 2.0f;
    string message = _zdoRedirectRunner.Start(_coordinator, PluginConfig.NetcodeProbeMaxDetailRows.Value);
    if (_zdoRedirectRunner.IsRunning) {
      _coordinator?.RecordDevMarker("zdo_redirect primary start (peer-ready, independent of probe delay)");
      LogInfo("ZDO primary redirect started at peer readiness: " + message);
      _lastPrimaryRedirectStartMessage = string.Empty;
    } else if (!string.Equals(message, _lastPrimaryRedirectStartMessage, StringComparison.Ordinal)) {
      LogWarning("ZDO primary redirect remains fail-safe native: " + message);
      _lastPrimaryRedirectStartMessage = message;
    }
  }

  RuntimeControlApplyResult ApplyServerRuntimeControl(string setting, string requestedValue) {
    if (ZNet.instance == null || !ZNet.instance.IsServer() || !ZNet.instance.IsDedicated()) {
      return RuntimeControlApplyResult.Refused("dedicated_server_only");
    }

    RuntimeControlApplyResult result;
    switch (setting) {
      case "zdoRedirectEnabled":
        if (!bool.TryParse(requestedValue, out bool redirectEnabled)) {
          return RuntimeControlApplyResult.Refused("value_must_be_boolean");
        }
        bool oldRedirect = PluginConfig.ZdoRedirectEnabled.Value;
        PluginConfig.ZdoRedirectEnabled.Value = redirectEnabled;
        string redirectEffect = "config_only";
        if (!redirectEnabled && _zdoRedirectRunner?.IsRunning == true) {
          redirectEffect = _zdoRedirectRunner.Stop();
        } else if (redirectEnabled) {
          redirectEffect = "eligible_for_primary_arm_on_next_update";
        }
        result = RuntimeControlApplyResult.Applied(
            Bool(oldRedirect), Bool(PluginConfig.ZdoRedirectEnabled.Value), redirectEffect);
        break;

      case "zdoCoPresenceShadowEnabled":
        if (!bool.TryParse(requestedValue, out bool shadowEnabled)) {
          return RuntimeControlApplyResult.Refused("value_must_be_boolean");
        }
        bool oldShadow = PluginConfig.ZdoCoPresenceShadowEnabled.Value;
        PluginConfig.ZdoCoPresenceShadowEnabled.Value = shadowEnabled;
        result = RuntimeControlApplyResult.Applied(
            Bool(oldShadow), Bool(PluginConfig.ZdoCoPresenceShadowEnabled.Value),
            "effective_on_next_redirect_candidate");
        break;

      case "zdoCoPresenceFanoutEnabled":
        if (!bool.TryParse(requestedValue, out bool fanoutEnabled)) {
          return RuntimeControlApplyResult.Refused("value_must_be_boolean");
        }
        bool oldFanout = PluginConfig.ZdoCoPresenceFanoutEnabled.Value;
        PluginConfig.ZdoCoPresenceFanoutEnabled.Value = fanoutEnabled;
        result = RuntimeControlApplyResult.Applied(
            Bool(oldFanout), Bool(PluginConfig.ZdoCoPresenceFanoutEnabled.Value),
            "effective_on_next_redirect_candidate");
        break;

      case "handshakeResponderStrictMode":
        if (!bool.TryParse(requestedValue, out bool strictMode)) {
          return RuntimeControlApplyResult.Refused("value_must_be_boolean");
        }
        bool oldStrict = PluginConfig.HandshakeResponderStrictMode.Value;
        PluginConfig.HandshakeResponderStrictMode.Value = strictMode;
        result = RuntimeControlApplyResult.Applied(
            Bool(oldStrict), Bool(PluginConfig.HandshakeResponderStrictMode.Value),
            "effective_on_next_deferred_peerinfo");
        break;

      case "handshakeResponderEnabled":
        if (!bool.TryParse(requestedValue, out bool responderEnabled)) {
          return RuntimeControlApplyResult.Refused("value_must_be_boolean");
        }
        bool oldResponder = PluginConfig.HandshakeResponderEnabled.Value;
        PluginConfig.HandshakeResponderEnabled.Value = responderEnabled;
        string responderEffect = responderEnabled
            ? _handshakeResponderRunner?.Start(_coordinator) ?? "runner_unavailable"
            : _handshakeResponderRunner?.Stop() ?? "runner_unavailable";
        result = RuntimeControlApplyResult.Applied(
            Bool(oldResponder), Bool(PluginConfig.HandshakeResponderEnabled.Value),
            responderEffect);
        break;

      case "handshakeResponderEndpoint":
        if (!Uri.TryCreate(requestedValue, UriKind.Absolute, out Uri endpoint)
            || !string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(endpoint.UserInfo)) {
          return RuntimeControlApplyResult.Refused("endpoint_must_be_plain_http_without_userinfo");
        }
        string oldEndpoint = PluginConfig.HandshakeResponderEndpoint.Value ?? string.Empty;
        bool restartForEndpoint = _handshakeResponderRunner?.IsRunning == true;
        if (restartForEndpoint) {
          _handshakeResponderRunner.Stop();
        }
        PluginConfig.HandshakeResponderEndpoint.Value = requestedValue.Trim().TrimEnd('/');
        string endpointEffect = restartForEndpoint
            ? _handshakeResponderRunner.Start(_coordinator)
            : "effective_on_next_responder_start";
        result = RuntimeControlApplyResult.Applied(
            oldEndpoint, PluginConfig.HandshakeResponderEndpoint.Value, endpointEffect);
        break;

      case "handshakeResponderWindowId":
        if (!IsSafeRuntimeToken(requestedValue)) {
          return RuntimeControlApplyResult.Refused("window_id_must_be_safe_token");
        }
        string oldWindow = PluginConfig.HandshakeResponderWindowId.Value ?? string.Empty;
        bool restartForWindow = _handshakeResponderRunner?.IsRunning == true;
        if (restartForWindow) {
          _handshakeResponderRunner.Stop();
        }
        PluginConfig.HandshakeResponderWindowId.Value = requestedValue.Trim();
        string windowEffect = restartForWindow
            ? _handshakeResponderRunner.Start(_coordinator)
            : "effective_on_next_responder_start";
        result = RuntimeControlApplyResult.Applied(
            oldWindow, PluginConfig.HandshakeResponderWindowId.Value, windowEffect);
        break;

      case "nativeNetworkPoisonEnabled":
        if (!bool.TryParse(requestedValue, out bool poisonEnabled)) {
          return RuntimeControlApplyResult.Refused("value_must_be_boolean");
        }
        bool oldPoison = PluginConfig.NativeNetworkPoisonEnabled.Value;
        PluginConfig.NativeNetworkPoisonEnabled.Value = poisonEnabled;
        result = RuntimeControlApplyResult.Applied(
            Bool(oldPoison), Bool(PluginConfig.NativeNetworkPoisonEnabled.Value),
            "effective_on_next_native_funnel_call");
        break;

      case "nativeNetworkEvidenceRunId":
        if (!IsSafeRuntimeToken(requestedValue)) {
          return RuntimeControlApplyResult.Refused("run_id_must_be_safe_token");
        }
        string oldRunId = PluginConfig.NativeNetworkEvidenceRunId.Value ?? string.Empty;
        PluginConfig.NativeNetworkEvidenceRunId.Value = requestedValue.Trim();
        NativeNetworkLedger.SetRunContext(PluginConfig.NativeNetworkEvidenceRunId.Value, "server");
        result = RuntimeControlApplyResult.Applied(
            oldRunId, PluginConfig.NativeNetworkEvidenceRunId.Value,
            "effective_on_next_native_ledger_row");
        break;

      case "directControlCutoverEnabled":
        if (!bool.TryParse(requestedValue, out bool directControlEnabled)) {
          return RuntimeControlApplyResult.Refused("value_must_be_boolean");
        }
        bool oldDirectControl = PluginConfig.DirectControlCutoverEnabled.Value;
        PluginConfig.DirectControlCutoverEnabled.Value = directControlEnabled;
        result = RuntimeControlApplyResult.Applied(
            Bool(oldDirectControl), Bool(PluginConfig.DirectControlCutoverEnabled.Value),
            directControlEnabled
                ? "selected_native_direct_pulse_suppressed"
                : "native_direct_pulse_generator_stopped");
        break;

      case "lumberjacksGatewayUrl":
        if (!Uri.TryCreate(requestedValue, UriKind.Absolute, out Uri gatewayEndpoint)
            || (gatewayEndpoint.Scheme != Uri.UriSchemeHttp
                && gatewayEndpoint.Scheme != Uri.UriSchemeHttps
                && gatewayEndpoint.Scheme != "ws"
                && gatewayEndpoint.Scheme != "wss")
            || !string.IsNullOrEmpty(gatewayEndpoint.UserInfo)) {
          return RuntimeControlApplyResult.Refused(
              "gateway_url_must_be_http_or_websocket_without_userinfo");
        }
        string oldGatewayUrl = PluginConfig.LumberjacksGatewayUrl.Value ?? string.Empty;
        PluginConfig.LumberjacksGatewayUrl.Value = requestedValue.Trim().TrimEnd('/');
        result = RuntimeControlApplyResult.Applied(
            oldGatewayUrl, PluginConfig.LumberjacksGatewayUrl.Value,
            "effective_on_next_canonical_session_connect");
        break;

      case "routedRpcCutoverEnabled":
        if (!bool.TryParse(requestedValue, out bool routedRpcEnabled)) {
          return RuntimeControlApplyResult.Refused("value_must_be_boolean");
        }
        bool oldRoutedRpc = PluginConfig.RoutedRpcCutoverEnabled.Value;
        PluginConfig.RoutedRpcCutoverEnabled.Value = routedRpcEnabled;
        result = RuntimeControlApplyResult.Applied(
            Bool(oldRoutedRpc), Bool(PluginConfig.RoutedRpcCutoverEnabled.Value),
            routedRpcEnabled
                ? "selected_routed_rpc_methods_fail_closed_to_lumberjacks"
                : "selected_routed_rpc_native_path_restored");
        break;

      case "zdoJournalCutoverEnabled":
        if (!bool.TryParse(requestedValue, out bool zdoJournalEnabled)) {
          return RuntimeControlApplyResult.Refused("value_must_be_boolean");
        }
        bool oldZdoJournal = PluginConfig.ZdoJournalCutoverEnabled.Value;
        PluginConfig.ZdoJournalCutoverEnabled.Value = zdoJournalEnabled;
        result = RuntimeControlApplyResult.Applied(
            Bool(oldZdoJournal), Bool(PluginConfig.ZdoJournalCutoverEnabled.Value),
            zdoJournalEnabled
                ? "mutation_journal_and_typed_apply_armed"
                : "mutation_journal_capture_stopped");
        break;

      case "zdoJournalCanonicalSessionEnabled":
        if (!bool.TryParse(requestedValue, out bool zdoCanonicalEnabled)) {
          return RuntimeControlApplyResult.Refused("value_must_be_boolean");
        }
        bool oldZdoCanonical = PluginConfig.ZdoJournalCanonicalSessionEnabled.Value;
        PluginConfig.ZdoJournalCanonicalSessionEnabled.Value = zdoCanonicalEnabled;
        result = RuntimeControlApplyResult.Applied(
            Bool(oldZdoCanonical),
            Bool(PluginConfig.ZdoJournalCanonicalSessionEnabled.Value),
            zdoCanonicalEnabled
                ? "zdo_semantics_bound_to_canonical_session"
                : "zdo_semantics_restored_to_http_lab_seam");
        break;

      case "ownershipLeaseCutoverEnabled":
        if (!bool.TryParse(requestedValue, out bool ownershipLeaseEnabled)) {
          return RuntimeControlApplyResult.Refused("value_must_be_boolean");
        }
        bool oldOwnershipLease =
            PluginConfig.OwnershipLeaseCutoverEnabled.Value;
        PluginConfig.OwnershipLeaseCutoverEnabled.Value =
            ownershipLeaseEnabled;
        result = RuntimeControlApplyResult.Applied(
            Bool(oldOwnershipLease),
            Bool(PluginConfig.OwnershipLeaseCutoverEnabled.Value),
            ownershipLeaseEnabled
                ? "logical_peer_ownership_leases_armed"
                : "selected_ownership_native_path_restored");
        break;

      case "worldZoneCutoverEnabled":
        if (!bool.TryParse(requestedValue, out bool worldZoneEnabled)) {
          return RuntimeControlApplyResult.Refused("value_must_be_boolean");
        }
        bool oldWorldZone = PluginConfig.WorldZoneCutoverEnabled.Value;
        PluginConfig.WorldZoneCutoverEnabled.Value = worldZoneEnabled;
        result = RuntimeControlApplyResult.Applied(
            Bool(oldWorldZone),
            Bool(PluginConfig.WorldZoneCutoverEnabled.Value),
            worldZoneEnabled
                ? "lumberjacks_world_descriptor_and_zone_membership_armed"
                : "native_world_and_zone_paths_restored");
        break;

      case "portalTraversalEnabled":
        if (!bool.TryParse(requestedValue, out bool portalTraversalEnabled)) {
          return RuntimeControlApplyResult.Refused("value_must_be_boolean");
        }
        if (!WorldZoneCutoverRunner.TrySetPortalTraversal(
                portalTraversalEnabled,
                out bool oldPortalTraversal,
                out bool effectivePortalTraversal,
                out string portalTraversalDetail)) {
          return RuntimeControlApplyResult.Refused(portalTraversalDetail);
        }
        result = RuntimeControlApplyResult.Applied(
            Bool(oldPortalTraversal),
            Bool(effectivePortalTraversal),
            "runtime_only_" + portalTraversalDetail
            + "_descriptor_republish_requested");
        break;

      case "motionAuthorityCutoverEnabled":
        if (!bool.TryParse(requestedValue, out bool motionAuthorityEnabled)) {
          return RuntimeControlApplyResult.Refused("value_must_be_boolean");
        }
        bool oldMotionAuthority = PluginConfig.MotionAuthorityCutoverEnabled.Value;
        PluginConfig.MotionAuthorityCutoverEnabled.Value = motionAuthorityEnabled;
        result = RuntimeControlApplyResult.Applied(
            Bool(oldMotionAuthority),
            Bool(PluginConfig.MotionAuthorityCutoverEnabled.Value),
            motionAuthorityEnabled
                ? "canonical_motion_authority_armed"
                : "canonical_motion_authority_disarmed");
        break;

      case "logicalPeerCutoverEnabled":
        if (!bool.TryParse(requestedValue, out bool logicalPeerEnabled)) {
          return RuntimeControlApplyResult.Refused("value_must_be_boolean");
        }
        if (logicalPeerEnabled &&
            (!PluginConfig.DirectControlCutoverEnabled.Value ||
             !PluginConfig.RoutedRpcCutoverEnabled.Value ||
             !PluginConfig.ZdoJournalCutoverEnabled.Value ||
             !PluginConfig.ZdoJournalCanonicalSessionEnabled.Value ||
             !PluginConfig.OwnershipLeaseCutoverEnabled.Value ||
             !PluginConfig.WorldZoneCutoverEnabled.Value ||
             !PluginConfig.MotionAuthorityCutoverEnabled.Value ||
             PluginConfig.SocketQuarantineCutoverEnabled.Value)) {
          return RuntimeControlApplyResult.Refused(
              "logical_peer_requires_coherent_c2a_through_c6_cutover");
        }
        bool oldLogicalPeer = PluginConfig.LogicalPeerCutoverEnabled.Value;
        PluginConfig.LogicalPeerCutoverEnabled.Value = logicalPeerEnabled;
        result = RuntimeControlApplyResult.Applied(
            Bool(oldLogicalPeer),
            Bool(PluginConfig.LogicalPeerCutoverEnabled.Value),
            logicalPeerEnabled
                ? "steam_free_logical_peer_adapter_armed"
                : "logical_peer_adapter_disarmed");
        break;

      case "cutoverResidueCleanup":
        if (!string.Equals(requestedValue, CutoverResidueSweeper.AllCutoverTaggedMode,
                StringComparison.Ordinal)
            && !IsSafeRuntimeToken(requestedValue)) {
          return RuntimeControlApplyResult.Refused("value_must_be_run_id_or_all-cutover-tagged");
        }
        if (!CutoverResidueSweeper.TrySweep(requestedValue.Trim(),
                out string sweepBefore, out string sweepAfter,
                out string sweepEffect, out string sweepRefusal)) {
          return RuntimeControlApplyResult.Refused(sweepRefusal);
        }
        result = RuntimeControlApplyResult.Applied(sweepBefore, sweepAfter, sweepEffect);
        break;

      default:
        return RuntimeControlApplyResult.Refused("setting_not_allowlisted");
    }

    Config.Save();
    return result;
  }

  static string Bool(bool value) => value ? "true" : "false";

  static bool IsSafeRuntimeToken(string value) {
    if (string.IsNullOrWhiteSpace(value) || value.Length > 80) {
      return false;
    }
    foreach (char c in value) {
      if (!char.IsLetterOrDigit(c) && c != '-' && c != '_' && c != '.') {
        return false;
      }
    }
    return true;
  }

  // Auto-port harness: register the RPC handler once ZRoutedRpc is up, then (server only) push the
  // densest-ZDO coordinate to each peer exactly once. The client acts on it in HandleAutoPort, gated
  // on its own autoPortOnJoinEnabled, so only an opted-in operator is ever moved. The density scan
  // is cached (AutoPortDensity), so a join costs at most one scan every few minutes.
  void TickAutoPort(float now) {
    ZRoutedRpc rpc = ZRoutedRpc.instance;
    if (rpc == null) {
      return;
    }
    if (_autoPortRegisteredRpc != rpc) {
      rpc.Register(AutoPortRpc, new Action<long, ZPackage>(HandleAutoPort));
      _autoPortRegisteredRpc = rpc;
    }

    ZNet znet = ZNet.instance;
    if (znet == null || !znet.IsServer()) {
      return;
    }
    List<ZNetPeer> peers = znet.GetPeers();
    if (peers == null) {
      return;
    }

    _autoPortPushedPeers.RemoveWhere(uid => !peers.Exists(p => p != null && p.m_uid == uid));
    foreach (ZNetPeer peer in peers) {
      if (peer == null || _autoPortPushedPeers.Contains(peer.m_uid)) {
        continue;
      }
      _autoPortPushedPeers.Add(peer.m_uid);
      if (!AutoPortDensity.TryDensestCenter(now, out Vector3 center, out int count)) {
        continue;
      }
      ZPackage package = new();
      package.Write(center.x);
      package.Write(center.y);
      package.Write(center.z);
      rpc.InvokeRoutedRPC(peer.m_uid, AutoPortRpc, new object[] { package });
      _coordinator?.RecordDevMarker(
          $"autoport push peer={peer.m_uid} densest=({center.x:0},{center.y:0},{center.z:0}) zdos={count}");
    }
  }

  // Client receipt of the densest coordinate. Opt-in only; kicks the delayed god/fly + teleport.
  public static void HandleAutoPort(long sender, ZPackage package) {
    if (!PluginConfig.AutoPortOnJoinEnabled.Value || package == null || Instance == null) {
      return;
    }
    Vector3 center = new(package.ReadSingle(), package.ReadSingle(), package.ReadSingle());
    Instance.StartCoroutine(Instance.AutoPortCoroutine(center));
  }

  IEnumerator AutoPortCoroutine(Vector3 densestCenter) {
    yield return new WaitForSeconds(Mathf.Max(0.0f, PluginConfig.AutoPortDelaySeconds.Value));

    // The join may still be settling; wait (bounded) for the local player before teleporting.
    float waited = 0.0f;
    while (Player.m_localPlayer == null && waited < 30.0f) {
      waited += 0.5f;
      yield return new WaitForSeconds(0.5f);
    }
    Player player = Player.m_localPlayer;
    if (player == null) {
      LogWarning("autoport: no local player after wait; skipping.");
      yield break;
    }

    try {
      if (!player.InGodMode()) {
        player.SetGodMode(true);
      }
      if (!player.InDebugFlyMode()) {
        player.ToggleDebugFly();
      }
    } catch (Exception exception) {
      LogWarning($"autoport god/fly failed: {exception.Message}");
    }

    Vector3 target = new(
        densestCenter.x,
        densestCenter.y + PluginConfig.AutoPortHeightMeters.Value,
        densestCenter.z);
    bool moved = TryTeleport(player, target);
    string message =
        $"autoport {(moved ? "->" : "FAILED ->")} ({target.x:0},{target.y:0},{target.z:0}) god={player.InGodMode()} fly={player.InDebugFlyMode()}";
    _coordinator?.RecordDevMarker(message);
    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
    LogInfo(message);
  }

  void OnGUI() {
    if (!PluginConfig.IsModEnabled.Value) {
      return;
    }

    _coordinator?.DrawHud();
    _transportStatusOverlay.Draw(
        BuildTransportStatus(),
        ToggleLumberjacksHttp,
        ToggleLumberjacksWebSocket,
        ToggleLumberjacksUdp,
        ToggleMotionApply,
        ToggleMcp,
        DisconnectValheim,
        OpenDashboard,
        OpenDashboardSetup);
  }

  TransportStatusSnapshot BuildTransportStatus() {
    ZNet znet = ZNet.instance;
    bool valheimConnected = znet != null && !znet.IsServer() && (znet.GetPeers()?.Count ?? 0) > 0;
    bool lumberjacksArmed = valheimConnected && _zdoAuthoritativeConsumerRunner?.IsRunning == true;
    return new() {
        ValheimConnected = valheimConnected,
        LumberjacksArmed = lumberjacksArmed,
        LumberjacksHttpEnabled = AlphaTransportSwitches.LumberjacksHttpEnabled,
        LumberjacksWebSocketEnabled = AlphaTransportSwitches.LumberjacksWebSocketEnabled,
        LumberjacksUdpEnabled = AlphaTransportSwitches.LumberjacksUdpEnabled,
        LumberjacksWebSocketConnected =
            _lumberjacksGameSessionRunner?.WebSocketConnected == true
            || _lumberjacksMotionRunner?.WebSocketConnected == true,
        LumberjacksUdpReady =
            _lumberjacksGameSessionRunner?.UdpReady == true
            || _lumberjacksMotionRunner?.UdpReady == true,
        MotionState = _lumberjacksMotionRunner?.State ?? "not-created",
        MotionLastError = _lumberjacksMotionRunner?.LastError,
        MotionApplyEnabled = AlphaTransportSwitches.MotionApplyEnabled,
        MotionSent = (_lumberjacksMotionRunner?.SentUdp ?? 0) + (_lumberjacksMotionRunner?.SentWebSocket ?? 0),
        MotionReceived = (_lumberjacksMotionRunner?.ReceivedUdp ?? 0) + (_lumberjacksMotionRunner?.ReceivedWebSocket ?? 0),
        MotionApplied = _lumberjacksMotionRunner?.Applied ?? 0,
        McpEnabled = AlphaTransportSwitches.McpEnabled,
        McpReachable = _mcpReachable,
        LumberjacksState = _zdoAuthoritativeConsumerRunner?.State ?? "not-armed",
        DashboardUrl = PluginConfig.DashboardUrl.Value,
        SetupUrl = PluginConfig.DashboardSetupUrl.Value
    };
  }

  void ToggleLumberjacksHttp() {
    bool enabled = AlphaTransportSwitches.SetLumberjacksHttpEnabled(
        !AlphaTransportSwitches.LumberjacksHttpEnabled);
    RecordTransportControl("lumberjacks_http", enabled, "native_valheim_rpc");
  }

  void ToggleMcp() {
    bool requestedEnabled = !AlphaTransportSwitches.McpEnabled;
    if (requestedEnabled && !PluginConfig.McpEnabled.Value) {
      const string blocked = "local_mcp remains OFF; set [MCP] mcpEnabled=true for Dev/Lab first";
      MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, blocked);
      LogInfo("Alpha transport control: " + blocked);
      return;
    }

    bool enabled = AlphaTransportSwitches.SetMcpEnabled(requestedEnabled);
    if (!enabled) _mcpReachable = false;
    else _nextMcpProbeAt = 0.0f;
    RecordTransportControl("local_mcp", enabled, "process_local");
  }

  void ToggleLumberjacksWebSocket() {
    bool enabled = AlphaTransportSwitches.SetLumberjacksWebSocketEnabled(
        !AlphaTransportSwitches.LumberjacksWebSocketEnabled);
    if (!enabled) _lumberjacksMotionRunner?.Stop();
    RecordTransportControl("lumberjacks_websocket", enabled, "motion_control");
  }

  void ToggleLumberjacksUdp() {
    bool enabled = AlphaTransportSwitches.SetLumberjacksUdpEnabled(
        !AlphaTransportSwitches.LumberjacksUdpEnabled);
    RecordTransportControl("lumberjacks_udp", enabled, enabled ? "motion_datagram" : "websocket_fallback");
  }

  void ToggleMotionApply() {
    bool enabled = AlphaTransportSwitches.SetMotionApplyEnabled(!AlphaTransportSwitches.MotionApplyEnabled);
    RecordTransportControl("lumberjacks_motion_apply", enabled, enabled ? "remote_presentation" : "native_fallback");
  }

  void DisconnectValheim() {
    if (ZNet.instance == null || ZNet.instance.IsServer()) return;
    RecordTransportControl("native_valheim_peer", false, "disconnect_requested");
    StartCoroutine(DisconnectAfterTelemetry());
  }

  IEnumerator DisconnectAfterTelemetry() {
    yield return new WaitForSecondsRealtime(0.25f);
    Game.instance?.Logout();
  }

  void OpenDashboard() {
    string url = PluginConfig.DashboardUrl.Value;
    if (!string.IsNullOrWhiteSpace(url)) Application.OpenURL(url);
    _coordinator?.RecordDevMarker("transport strip opened local dashboard");
  }

  void OpenDashboardSetup() {
    string url = PluginConfig.DashboardSetupUrl.Value;
    if (!string.IsNullOrWhiteSpace(url)) Application.OpenURL(url);
    _coordinator?.RecordDevMarker("transport strip opened dashboard setup");
  }

  void RecordTransportControl(string component, bool enabled, string observedPath) {
    _coordinator?.RecordTransportControl(component, enabled, observedPath);
    _gameplayEventProducer?.RecordTransportControl(component, enabled, observedPath);
    string message = component + " " + (enabled ? "ON" : "OFF");
    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
    LogInfo("Alpha transport control: " + message + " via " + observedPath);
  }

  void UpdateMcpHealth(float now) {
    if (ZNet.instance != null && ZNet.instance.IsServer() && ZNet.instance.IsDedicated()) return;
    if (!AlphaTransportSwitches.McpEnabled) {
      _mcpReachable = false;
      return;
    }
    if (!McpGatewayEndpoint.TryCreate(
        PluginConfig.McpGatewayUrl.Value, "/healthz", out Uri endpoint, out _)) {
      _mcpReachable = false;
      return;
    }
    if (now < _nextMcpProbeAt || Interlocked.CompareExchange(ref _mcpProbeInFlight, 1, 0) != 0) return;
    _nextMcpProbeAt = now + 5.0f;
    _ = Task.Run(async () => {
      try {
        HttpWebRequest request = (HttpWebRequest) WebRequest.Create(endpoint);
        request.Method = "GET";
        request.Timeout = 1500;
        request.ReadWriteTimeout = 1500;
        request.Headers.Add("X-Comfy-Key", "valheim-mod-local");
        using WebResponse response = await request.GetResponseAsync().ConfigureAwait(false);
        _mcpReachable = response is HttpWebResponse http && (int) http.StatusCode >= 200 && (int) http.StatusCode < 300;
      } catch {
        _mcpReachable = false;
      } finally {
        Interlocked.Exchange(ref _mcpProbeInFlight, 0);
      }
    });
  }

  void OnDestroy() {
    AlphaTransportSwitches.Reset();
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
    _lumberjacksGameSessionRunner?.Dispose();
    _lumberjacksGameSessionRunner = null;
    _routedRpcCutoverRunner?.Dispose();
    _routedRpcCutoverRunner = null;
    _shipCutoverRunner?.Dispose();
    _shipCutoverRunner = null;
    _creatureAiCutoverRunner?.Dispose();
    _creatureAiCutoverRunner = null;
    _saddleCutoverRunner?.Dispose();
    _saddleCutoverRunner = null;
    _containerCutoverRunner?.Dispose();
    _containerCutoverRunner = null;
    _zdoJournalCutoverRunner?.Dispose();
    _zdoJournalCutoverRunner = null;
    _ownershipLeaseCutoverRunner?.Dispose();
    _ownershipLeaseCutoverRunner = null;
    _worldZoneCutoverRunner?.Dispose();
    _worldZoneCutoverRunner = null;
    _logicalPeerCutoverRunner?.Dispose();
    _logicalPeerCutoverRunner = null;
    _socketQuarantineCutoverRunner?.Dispose();
    _socketQuarantineCutoverRunner = null;
    _lumberjacksMotionRunner?.Dispose();
    _lumberjacksMotionRunner = null;
    _motionTestController?.Dispose();
    _motionTestController = null;
    _nativeCutoverScenarioController?.Dispose();
    _nativeCutoverScenarioController = null;
    _netcodeProbeRunner?.Dispose();
    _netcodeProbeRunner = null;
    _zdoRedirectRunner?.Dispose();
    _zdoRedirectRunner = null;
    _gameplayEventProducer?.Dispose();
    _gameplayEventProducer = null;
    _handshakeResponderRunner?.Dispose();
    _handshakeResponderRunner = null;
    _serverRuntimeControlRunner?.Dispose();
    _serverRuntimeControlRunner = null;
    _coordinator?.Dispose();
    _coordinator = null;
    _harmony?.UnpatchSelf();
    _harmony = null;
    _nativeNetworkLedger?.Dispose();
    _nativeNetworkLedger = null;
    _directControlCutoverRunner?.Dispose();
    _directControlCutoverRunner = null;

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
    ApplyMcpConfiguration();
    string message = "NetworkSense config reloaded.";
    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
    LogInfo(message);
    return message;
  }

  internal void ApplyMcpConfiguration() {
    bool enabled = AlphaTransportSwitches.SetMcpEnabled(PluginConfig.McpEnabled.Value);
    _mcpReachable = false;
    _nextMcpProbeAt = enabled ? 0.0f : float.MaxValue;
  }

  object CheckMcpGateway() {
    if (!AlphaTransportSwitches.McpEnabled) {
      return "Comfy MCP is switched off by the alpha transport control.";
    }

    if (!McpGatewayEndpoint.TryCreate(
        PluginConfig.McpGatewayUrl.Value, "/healthz", out Uri endpoint, out string endpointError)) {
      return "Comfy MCP configuration rejected: " + endpointError;
    }
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
    if (!TryStartRehearsal(fileName, profile, out string message)) {
      MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
      LogWarning(message);
      return false;
    }

    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
    LogInfo(message);
    return message;
  }

  bool TryStartRehearsal(string fileName, string profile, out string message) {
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

    RunCommand(_coordinator.ReloadConfig);
    CheckMcpGateway();
    _coordinator.RecordDevMarker($"route_rehearsal {profile} start");

    StartCoroutine(RunTeleportRoute(stops, routePath, profile, exportOnComplete: true));
    message = $"NetworkSense rehearsal started: {stops.Count} stops from {fileName}, profile={profile}.";
    return true;
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
  // into the loop--the exact KVM regression the hands-free rig exists to prevent). We call the
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
      bool exportOnComplete = false) {
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
