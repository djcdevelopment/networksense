namespace ComfyNetworkSense;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;

using HarmonyLib;
using Lumberjacks.Contracts.Valheim;

using UnityEngine;

/// <summary>
/// C2b's fixed routed-RPC adapter. Selected complete RoutedRPCData shapes leave through the
/// canonical Lumberjacks session, are banked by its worker, and dispatch through
/// ZRoutedRpc.HandleRoutedRPC only from Unity Update. A selected enqueue failure fails closed.
/// </summary>
public sealed class RoutedRpcCutoverRunner : IDisposable {
  public const string RequestMethod = ValheimRoutedRpcAdmissions.CutoverRequest;
  public const string ResponseMethod = ValheimRoutedRpcAdmissions.CutoverResponse;
  public const string BroadcastRequestMethod =
      ValheimRoutedRpcAdmissions.CutoverBroadcastRequest;
  public const string BroadcastMethod = ValheimRoutedRpcAdmissions.CutoverBroadcast;
  public const string TargetReceiptMethod =
      ValheimRoutedRpcAdmissions.CutoverTargetReceipt;
  public const string ResetClothMethod = ValheimRoutedRpcAdmissions.CutoverResetCloth;
  public const string RemoteStaminaMethod = "UseStamina";
  public const string JournalRequestMethod =
      ValheimRoutedRpcAdmissions.CutoverZdoJournalRequest;

  const string ReceiptFileName = "routed-rpc-cutover.jsonl";
  const int MaxSeenRoutes = 1024;
  const float RemoteStaminaProbeAmount = 1.25f;

  static readonly MethodInfo HandleRoutedRpcMethod =
      AccessTools.Method(typeof(ZRoutedRpc), "HandleRoutedRPC");
  static RoutedRpcCutoverRunner _active;
  [ThreadStatic] static OutboundContext _outboundContext;

  readonly LumberjacksGameSessionRunner _gameSession;
  readonly ConcurrentQueue<InboundRoute> _inbound = new();
  readonly TelemetryLogWriter _writer = new();
  readonly HashSet<string> _seenRoutes = new(StringComparer.Ordinal);
  readonly Queue<string> _seenOrder = new();

  ZRoutedRpc _registeredRpc;
  RoutedProbe _probe;
  bool _disposed;

  public RoutedRpcCutoverRunner(LumberjacksGameSessionRunner gameSession) {
    _gameSession = gameSession;
    RoutedRpcCutoverRunner previous = Interlocked.Exchange(ref _active, this);
    previous?.Dispose();
  }

  public void Update(float now) {
    if (_disposed) return;
    EnsureHandlers();
    DrainInbound();
    EvaluateDeadline(now);
  }

  public bool BeginProbe(
      string actionId,
      string mode,
      float deadlineSeconds,
      out string detail) {
    detail = string.Empty;
    if (!SafeToken(actionId, 80)
        || mode is not (
            "request" or "broadcast" or "target_zdo" or "remote_stamina"
            or "withhold")) {
      detail = "routed_probe_parameters_invalid";
      return false;
    }
    if (!CutoverEnabled()) {
      detail = "routed_rpc_cutover_not_enabled";
      return false;
    }
    if (_gameSession?.WebSocketConnected != true) {
      detail = "lumberjacks_session_not_connected";
      return false;
    }
    if (ZNet.instance == null || ZNet.instance.IsServer()
        || Player.m_localPlayer == null || ZRoutedRpc.instance == null) {
      detail = "routed_probe_client_not_ready";
      return false;
    }
    string runId = CurrentRunId();
    if (!SafeToken(runId, 80)) {
      detail = "native_autotest_run_missing";
      return false;
    }
    if (_probe != null && !_probe.Terminal) {
      detail = "another_routed_probe_active";
      return false;
    }

    Player remoteStaminaTarget = null;
    if (mode == "remote_stamina"
        && !TryFindRemotePlayer(out remoteStaminaTarget)) {
      detail = "routed_remote_player_not_ready";
      return false;
    }

    _probe = new RoutedProbe {
        RunId = runId,
        ActionId = actionId,
        Mode = mode,
        DeadlineAt = Time.unscaledTime + Mathf.Clamp(deadlineSeconds, 1.0f, 30.0f)
    };
    bool queued;
    switch (mode) {
      case "request":
        queued = SendRequest(_probe, "respond");
        break;
      case "broadcast":
        queued = SendBroadcastRequest(_probe);
        break;
      case "target_zdo":
        queued = SendResetCloth(_probe);
        break;
      case "remote_stamina":
        queued = SendRemoteStamina(_probe, remoteStaminaTarget);
        break;
      default:
        queued = SendRequest(_probe, "withhold_response");
        break;
    }
    _probe.OutboundQueued = queued;
    Write(
        queued ? "probe_started" : "probe_start_failed",
        runId, actionId, string.Empty, 0, 0, 0, ZDOID.None,
        "mode=" + mode);
    if (!queued) {
      _probe.Terminal = true;
      _probe.Detail = "lumberjacks_routed_enqueue_failed";
      detail = _probe.Detail;
      return false;
    }
    return true;
  }

  public bool TryGetProbeResult(
      string actionId,
      out bool terminal,
      out bool success,
      out string detail) {
    if (_probe == null
        || !string.Equals(_probe.ActionId, actionId, StringComparison.Ordinal)) {
      terminal = false;
      success = false;
      detail = "routed_probe_not_found";
      return false;
    }
    terminal = _probe.Terminal;
    success = _probe.Success;
    detail = _probe.Detail ?? string.Empty;
    return true;
  }

  public static bool AllowNativeRoute(ZRoutedRpc.RoutedRPCData data) {
    if (!CutoverEnabled() || data == null)
      return true;

    RoutedRpcCutoverRunner active = Volatile.Read(ref _active);
    if (!ValheimRoutedRpcAdmissions.TryGetByHash(
            data.m_methodHash, out ValheimRoutedRpcAdmission admission)) {
      bool blocked = NativeNetworkLedger.Observe(
          "routed_rpc_unadmitted_send", "outbound", "RoutedRPC");
      active?.Write(
          blocked ? "unadmitted_native_route_blocked" : "unadmitted_native_route_observed",
          CurrentRunId(), "unscoped", string.Empty,
          data.m_msgID, data.m_senderPeerID, data.m_targetPeerID, data.m_targetZDO,
          "method_hash="
              + data.m_methodHash.ToString(CultureInfo.InvariantCulture));
      return !blocked;
    }

    string methodName = admission.Name;
    string targetKind = ResolveOutboundTargetKind(admission, data.m_targetZDO);
    byte[] parameters = data.m_parameters?.GetArray();
    if (parameters == null
        || !ValheimRoutedRpcAdmissions.AllowsEnvelope(
            methodName,
            data.m_methodHash,
            data.m_targetZDO.UserID,
            data.m_targetZDO.ID,
            targetKind,
            parameters)) {
      active?.Write(
          "route_contract_rejected", CurrentRunId(), "unscoped", methodName,
          data.m_msgID, data.m_senderPeerID, data.m_targetPeerID, data.m_targetZDO,
          parameters == null
              ? "parameters_missing"
              : "scope_size_or_payload_invalid;bytes="
                  + parameters.Length.ToString(CultureInfo.InvariantCulture));
      return false;
    }

    if (admission.Disposition == ValheimRoutedRpcDisposition.Supersede) {
      active?.Write(
          "superseded_native_route_suppressed", CurrentRunId(), "unscoped", methodName,
          data.m_msgID, data.m_senderPeerID, data.m_targetPeerID, data.m_targetZDO,
          "replacement_lane=" + admission.ReplacementLane);
      return false;
    }

    if (active == null) return false;
    OutboundContext context = _outboundContext;
    string runId = CurrentRunId();
    string actionId =
        SafeToken(context?.ActionId, 80) ? context.ActionId : "unscoped";
    string deliveryMode = context?.DeliveryMode == "withhold" ? "withhold" : "deliver";
    string routeId =
        "r-" + data.m_senderPeerID.ToString("x16", CultureInfo.InvariantCulture)
        + "-" + data.m_msgID.ToString("x16", CultureInfo.InvariantCulture)
        + "-" + unchecked((uint)data.m_methodHash).ToString("x8", CultureInfo.InvariantCulture);
    ZDOID targetZdo = data.m_targetZDO;
    active.Write(
        "native_route_attempted", runId, actionId, methodName,
        data.m_msgID, data.m_senderPeerID, data.m_targetPeerID, targetZdo,
        "delivery_mode=" + deliveryMode);

    bool queued = active._gameSession.TryQueueRoutedRpc(
        runId,
        actionId,
        routeId,
        data.m_msgID,
        data.m_senderPeerID,
        data.m_targetPeerID,
        targetZdo.UserID,
        targetZdo.ID,
        targetKind,
        methodName,
        data.m_methodHash,
        Convert.ToBase64String(parameters),
        deliveryMode,
        out string queueDetail);
    if (context != null) context.Queued = queued;
    active.Write(
        queued ? "lumberjacks_route_queued" : "lumberjacks_route_rejected",
        runId, actionId, methodName,
        data.m_msgID, data.m_senderPeerID, data.m_targetPeerID, targetZdo,
        queueDetail);
    active.Write(
        "native_route_suppressed", runId, actionId, methodName,
        data.m_msgID, data.m_senderPeerID, data.m_targetPeerID, targetZdo,
        queued ? "after_lumberjacks_enqueue" : "fail_closed_no_native_fallback");
    return false;
  }

  public static bool SuppressNativeInbound(ZPackage package) {
    if (!CutoverEnabled() || package == null)
      return false;
    try {
      ZRoutedRpc.RoutedRPCData data = new();
      data.Deserialize(new ZPackage(package.GetArray()));
      if (!ValheimRoutedRpcAdmissions.TryGetByHash(
              data.m_methodHash, out ValheimRoutedRpcAdmission admission))
        return false;
      string methodName = admission.Name;
      byte[] parameters = data.m_parameters?.GetArray();
      RoutedRpcCutoverRunner active = Volatile.Read(ref _active);
      if (parameters == null
          || !ValheimRoutedRpcAdmissions.AllowsEnvelope(
              methodName,
              data.m_methodHash,
              data.m_targetZDO.UserID,
              data.m_targetZDO.ID,
              parameters)) {
        active?.Write(
            "native_route_contract_rejected", CurrentRunId(), "unscoped", methodName,
            data.m_msgID, data.m_senderPeerID, data.m_targetPeerID, data.m_targetZDO,
            "admitted_hash_with_invalid_scope_size_or_payload");
        return true;
      }
      if (active != null) {
        active.Write(
            "native_route_received", CurrentRunId(),
            active._probe?.ActionId ?? "unscoped", methodName,
            data.m_msgID, data.m_senderPeerID, data.m_targetPeerID, data.m_targetZDO,
            "unexpected_native_fallback_suppressed");
        if (active._probe != null && !active._probe.Terminal) {
          active._probe.NativeReceived++;
          active._probe.Terminal = true;
          active._probe.Success = false;
          active._probe.Detail = "unexpected_native_routed_rpc";
        }
      }
      return true;
    } catch (Exception exception) {
      Volatile.Read(ref _active)?.Write(
          "native_route_parse_failed", CurrentRunId(), "unscoped", string.Empty,
          0, 0, 0, ZDOID.None, exception.GetType().Name);
      return true;
    }
  }

  public static void EnqueueLumberjacksInbound(
      long reliableSequence,
      string runId,
      string actionId,
      string routeId,
      long messageId,
      long senderPeerId,
      long targetPeerId,
      long targetZdoUserId,
      uint targetZdoId,
      string targetKind,
      string methodName,
      int methodHash,
      string parametersBase64) {
    RoutedRpcCutoverRunner active = Volatile.Read(ref _active);
    active?._inbound.Enqueue(new InboundRoute {
        ReliableSequence = reliableSequence,
        RunId = runId,
        ActionId = actionId,
        RouteId = routeId,
        MessageId = messageId,
        SenderPeerId = senderPeerId,
        TargetPeerId = targetPeerId,
        TargetZdo = new ZDOID(targetZdoUserId, targetZdoId),
        TargetKind = targetKind,
        MethodName = methodName,
        MethodHash = methodHash,
        ParametersBase64 = parametersBase64
    });
  }

  void DrainInbound() {
    while (_inbound.TryDequeue(out InboundRoute item)) {
      if (!_seenRoutes.Add(item.RouteId)) {
        _gameSession.QueueReliableAck(item.ReliableSequence);
        Write(
            "lumberjacks_route_duplicate", item.RunId, item.ActionId, item.MethodName,
            item.MessageId, item.SenderPeerId, item.TargetPeerId, item.TargetZdo,
            "handler_not_repeated");
        continue;
      }
      _seenOrder.Enqueue(item.RouteId);
      while (_seenOrder.Count > MaxSeenRoutes)
        _seenRoutes.Remove(_seenOrder.Dequeue());

      try {
        byte[] parameters = Convert.FromBase64String(item.ParametersBase64);
        if (!ValheimRoutedRpcAdmissions.AllowsRoutedEnvelope(
                item.MethodName,
                item.MethodHash,
                item.TargetZdo.UserID,
                item.TargetZdo.ID,
                item.TargetKind,
                parameters)
            || HandleRoutedRpcMethod == null || ZRoutedRpc.instance == null)
          throw new InvalidOperationException("routed_dispatch_contract_invalid");

        parameters = ValidateAndNormalizeTypedInbound(item, parameters);
        ZRoutedRpc.RoutedRPCData data = new() {
            m_msgID = item.MessageId,
            m_senderPeerID = item.SenderPeerId,
            m_targetPeerID = item.TargetPeerId,
            m_targetZDO = item.TargetZdo,
            m_methodHash = item.MethodHash,
            m_parameters = new ZPackage(parameters)
        };
        bool remoteStaminaProbe = IsRemoteStaminaProbe(item);
        float staminaBefore = 0.0f;
        float requestedStamina = 0.0f;
        if (remoteStaminaProbe
            && !TryBeginRemoteStaminaObservation(
                item, parameters, out staminaBefore, out requestedStamina))
          throw new InvalidOperationException(
              "remote_stamina_probe_target_or_payload_invalid");

        HandleRoutedRpcMethod.Invoke(ZRoutedRpc.instance, new object[] { data });
        Write(
            "lumberjacks_handler_dispatched", item.RunId, item.ActionId, item.MethodName,
            item.MessageId, item.SenderPeerId, item.TargetPeerId, item.TargetZdo,
            "unity_update");

        if (remoteStaminaProbe) {
          float staminaAfter = Player.m_localPlayer.GetStamina();
          float expectedAfter = Mathf.Max(0.0f, staminaBefore - requestedStamina);
          if (Mathf.Abs(staminaAfter - expectedAfter) > 0.001f)
            throw new InvalidOperationException(
                "remote_stamina_probe_debit_mismatch");
          Write(
              "remote_stamina_debit_applied", item.RunId, item.ActionId,
              item.MethodName, item.MessageId, item.SenderPeerId,
              item.TargetPeerId, item.TargetZdo,
              "before=" + staminaBefore.ToString("R", CultureInfo.InvariantCulture)
                  + ";requested="
                  + requestedStamina.ToString("R", CultureInfo.InvariantCulture)
                  + ";after="
                  + staminaAfter.ToString("R", CultureInfo.InvariantCulture));
        }
        if (!_gameSession.QueueReliableAck(item.ReliableSequence))
          throw new InvalidOperationException("reliable_ack_queue_full");
        if (remoteStaminaProbe)
          SendTargetReceipt(item, "remote_stamina_receipt");
        else if (IsServer() && item.MethodName == ResetClothMethod)
          SendTargetReceipt(item, "target_receipt");
      } catch (Exception exception) {
        Exception cause = exception.InnerException ?? exception;
        Write(
            "lumberjacks_dispatch_failed", item.RunId, item.ActionId, item.MethodName,
            item.MessageId, item.SenderPeerId, item.TargetPeerId, item.TargetZdo,
            cause.GetType().Name + ":" + cause.Message);
        if (_probe != null && !_probe.Terminal) {
          _probe.Terminal = true;
          _probe.Success = false;
          _probe.Detail = "lumberjacks_routed_dispatch_failed";
        }
      }
    }
  }

  void EnsureHandlers() {
    ZRoutedRpc rpc = ZRoutedRpc.instance;
    if (rpc == null || ReferenceEquals(rpc, _registeredRpc)) return;
    rpc.Register<ZPackage>(RequestMethod, HandleRequest);
    rpc.Register<ZPackage>(ResponseMethod, HandleResponse);
    rpc.Register<ZPackage>(BroadcastRequestMethod, HandleBroadcastRequest);
    rpc.Register<ZPackage>(BroadcastMethod, HandleBroadcast);
    rpc.Register<ZPackage>(TargetReceiptMethod, HandleTargetReceipt);
    _registeredRpc = rpc;
    Write(
        "typed_handlers_registered", CurrentRunId(), "unscoped", string.Empty,
        0, ZNet.instance != null ? ZNet.GetUID() : 0, 0, ZDOID.None, Role());
  }

  static void HandleRequest(long senderPeerId, ZPackage package) {
    RoutedRpcCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !IsServer()) return;
    if (!ReadProbePackage(package, out string runId, out string actionId, out string mode))
      throw new InvalidOperationException("routed_request_payload_invalid");
    ZPackage response = BuildProbePackage(runId, actionId, "response");
    active.InvokeWithContext(
        actionId,
        mode == "withhold_response" ? "withhold" : "deliver",
        () => ZRoutedRpc.instance.InvokeRoutedRPC(
            senderPeerId, ResponseMethod, new object[] { response }));
  }

  static void HandleResponse(long senderPeerId, ZPackage package) {
    RoutedRpcCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || IsServer()) return;
    if (!ReadProbePackage(package, out string runId, out string actionId, out _))
      throw new InvalidOperationException("routed_response_payload_invalid");
    active.CompleteProbe(runId, actionId, "request", "routed_response_applied");
  }

  static void HandleBroadcastRequest(long senderPeerId, ZPackage package) {
    RoutedRpcCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !IsServer()) return;
    if (!ReadProbePackage(package, out string runId, out string actionId, out _))
      throw new InvalidOperationException("routed_broadcast_request_payload_invalid");
    ZPackage broadcast = BuildProbePackage(runId, actionId, "broadcast");
    active.InvokeWithContext(
        actionId,
        "deliver",
        () => ZRoutedRpc.instance.InvokeRoutedRPC(
            ZRoutedRpc.Everybody, BroadcastMethod, new object[] { broadcast }));
  }

  static void HandleBroadcast(long senderPeerId, ZPackage package) {
    RoutedRpcCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || IsServer()) return;
    if (!ReadProbePackage(package, out string runId, out string actionId, out _))
      throw new InvalidOperationException("routed_broadcast_payload_invalid");
    active.CompleteProbe(runId, actionId, "broadcast", "routed_broadcast_applied");
  }

  static void HandleTargetReceipt(long senderPeerId, ZPackage package) {
    RoutedRpcCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || IsServer()) return;
    if (!ReadProbePackage(
            package, out string runId, out string actionId,
            out string receiptMode))
      throw new InvalidOperationException("routed_target_receipt_payload_invalid");
    string expectedMode;
    string detail;
    switch (receiptMode) {
      case "target_receipt":
        expectedMode = "target_zdo";
        detail = "reset_cloth_dispatched_on_server";
        break;
      case "remote_stamina_receipt":
        expectedMode = "remote_stamina";
        detail = "remote_player_stamina_debit_applied";
        break;
      default:
        throw new InvalidOperationException(
            "routed_target_receipt_mode_invalid");
    }
    active.CompleteProbe(
        runId, actionId, expectedMode, detail);
  }

  bool SendRequest(RoutedProbe probe, string mode) {
    ZNetPeer server = ZNet.instance?.GetServerPeer();
    if (server == null || server.m_uid == 0) return false;
    ZPackage package = BuildProbePackage(probe.RunId, probe.ActionId, mode);
    return InvokeWithContext(
        probe.ActionId,
        "deliver",
        () => ZRoutedRpc.instance.InvokeRoutedRPC(
            server.m_uid, RequestMethod, new object[] { package }));
  }

  bool SendBroadcastRequest(RoutedProbe probe) {
    ZNetPeer server = ZNet.instance?.GetServerPeer();
    if (server == null || server.m_uid == 0) return false;
    ZPackage package = BuildProbePackage(probe.RunId, probe.ActionId, "broadcast_request");
    return InvokeWithContext(
        probe.ActionId,
        "deliver",
        () => ZRoutedRpc.instance.InvokeRoutedRPC(
            server.m_uid, BroadcastRequestMethod, new object[] { package }));
  }

  bool SendResetCloth(RoutedProbe probe) {
    ZNetView view = ((Component)Player.m_localPlayer).GetComponent<ZNetView>();
    if (view == null || view.GetZDO() == null) return false;
    return InvokeWithContext(
        probe.ActionId,
        "deliver",
        () => view.InvokeRPC(ZNetView.Everybody, ResetClothMethod));
  }

  bool SendRemoteStamina(RoutedProbe probe, Player remotePlayer) {
    if (remotePlayer == null) return false;
    ZNetView view = ((Component)remotePlayer).GetComponent<ZNetView>();
    if (view == null || !view.IsValid() || view.IsOwner()
        || view.GetZDO() == null || view.GetZDO().GetOwner() == 0)
      return false;
    ZDOID targetId = remotePlayer.GetZDOID();
    long targetOwner = view.GetZDO().GetOwner();
    if (!LivePlayerTargetPolicy.MatchesCurrentOwner(
            targetId.UserID, targetOwner))
      return false;
    Write(
        "remote_stamina_target_selected", probe.RunId, probe.ActionId,
        RemoteStaminaMethod, 0, 0, targetOwner, targetId,
        "live_player_user_matches_owner");
    return InvokeWithContext(
        probe.ActionId,
        "deliver",
        () => remotePlayer.UseStamina(RemoteStaminaProbeAmount));
  }

  void SendTargetReceipt(InboundRoute item, string receiptMode) {
    ZPackage receipt = BuildProbePackage(
        item.RunId, item.ActionId, receiptMode);
    InvokeWithContext(
        item.ActionId,
        "deliver",
        () => ZRoutedRpc.instance.InvokeRoutedRPC(
            item.SenderPeerId, TargetReceiptMethod, new object[] { receipt }));
  }

  static bool TryFindRemotePlayer(out Player remotePlayer) {
    remotePlayer = null;
    Player localPlayer = Player.m_localPlayer;
    if (localPlayer == null) return false;
    float nearestDistance = float.PositiveInfinity;
    foreach (Player candidate in Player.GetAllPlayers()) {
      if (candidate == null || ReferenceEquals(candidate, localPlayer))
        continue;
      ZNetView view = ((Component)candidate).GetComponent<ZNetView>();
      if (view == null || !view.IsValid() || view.IsOwner()
          || view.GetZDO() == null || view.GetZDO().GetOwner() == 0)
        continue;
      ZDOID candidateId = candidate.GetZDOID();
      if (!LivePlayerTargetPolicy.MatchesCurrentOwner(
              candidateId.UserID, view.GetZDO().GetOwner()))
        continue;
      float distance =
          (candidate.transform.position - localPlayer.transform.position)
              .sqrMagnitude;
      if (distance >= nearestDistance) continue;
      nearestDistance = distance;
      remotePlayer = candidate;
    }
    return remotePlayer != null;
  }

  static bool IsRemoteStaminaProbe(InboundRoute item) =>
      !IsServer()
      && string.Equals(
          item.MethodName, RemoteStaminaMethod, StringComparison.Ordinal)
      && (item.ActionId?.EndsWith(
              "-remote-stamina", StringComparison.Ordinal) ?? false);

  static bool TryBeginRemoteStaminaObservation(
      InboundRoute item,
      byte[] parameters,
      out float staminaBefore,
      out float requestedStamina) {
    staminaBefore = 0.0f;
    requestedStamina = 0.0f;
    Player localPlayer = Player.m_localPlayer;
    if (localPlayer == null || parameters == null) return false;
    ZDOID localId = localPlayer.GetZDOID();
    if (localId.UserID != item.TargetZdo.UserID
        || localId.ID != item.TargetZdo.ID)
      return false;
    try {
      ZPackage package = new(parameters);
      requestedStamina = package.ReadSingle();
      staminaBefore = localPlayer.GetStamina();
      return requestedStamina > 0.0f
          && !float.IsNaN(requestedStamina)
          && !float.IsInfinity(requestedStamina)
          && staminaBefore >= requestedStamina;
    } catch {
      return false;
    }
  }

  static string ResolveOutboundTargetKind(
      ValheimRoutedRpcAdmission admission,
      ZDOID targetZdo) {
    if (admission == null || admission.RequiredTargetKind.Length == 0)
      return string.Empty;
    if (admission.RequiredTargetKind == ValheimRoutedRpcAdmissions.ShipTargetKind
        && TryResolveShipTarget(targetZdo, admission.Name, out _, out _))
      return ValheimRoutedRpcAdmissions.ShipTargetKind;
    return string.Empty;
  }

  static byte[] ValidateAndNormalizeTypedInbound(
      InboundRoute item,
      byte[] parameters) {
    if (string.IsNullOrEmpty(item.TargetKind)) return parameters;
    if (item.TargetKind != ValheimRoutedRpcAdmissions.ShipTargetKind
        || !TryResolveShipTarget(
            item.TargetZdo, item.MethodName, out Ship ship,
            out ShipControlls controls))
      throw new InvalidOperationException("typed_ship_target_not_resolved");

    ZDO zdo = ship.GetComponent<ZNetView>()?.GetZDO();
    if (zdo == null || zdo.m_uid != item.TargetZdo)
      throw new InvalidOperationException("typed_ship_zdo_mismatch");

    if (item.MethodName is "RequestControl" or "ReleaseControl") {
      Player sender = FindPlayerByPeer(item.SenderPeerId);
      if (sender == null)
        throw new InvalidOperationException("typed_ship_sender_player_missing");
      long playerId = sender.GetPlayerID();
      if (playerId == 0)
        throw new InvalidOperationException("typed_ship_sender_profile_missing");
      ZPackage normalized = new();
      normalized.Write(playerId);
      return normalized.GetArray();
    }

    if (item.MethodName == "RequestRespons") {
      if (zdo.GetOwner() != item.SenderPeerId)
        throw new InvalidOperationException("typed_ship_response_not_from_owner");
      return parameters;
    }

    Player controller = FindPlayerByPeer(item.SenderPeerId);
    if (controller == null ||
        zdo.GetLong(ZDOVars.s_user, 0L) != controller.GetPlayerID())
      throw new InvalidOperationException("typed_ship_input_not_from_helm_user");
    return parameters;
  }

  static bool TryResolveShipTarget(
      ZDOID targetZdo,
      string methodName,
      out Ship ship,
      out ShipControlls controls) {
    ship = null;
    controls = null;
    if (targetZdo.IsNone() || ZNetScene.instance == null) return false;
    GameObject target = ZNetScene.instance.FindInstance(targetZdo);
    if (target == null) return false;
    ship = target.GetComponent<Ship>()
        ?? target.GetComponentInChildren<Ship>(includeInactive: true);
    if (ship == null) return false;
    controls = target.GetComponent<ShipControlls>()
        ?? target.GetComponentInChildren<ShipControlls>(includeInactive: true);
    return methodName is not (
        "RequestControl" or "ReleaseControl" or "RequestRespons")
        || controls != null;
  }

  static Player FindPlayerByPeer(long peerId) {
    foreach (Player candidate in Player.GetAllPlayers()) {
      if (candidate == null) continue;
      ZNetView view = candidate.GetComponent<ZNetView>();
      ZDO zdo = view?.GetZDO();
      if (zdo != null &&
          (zdo.GetOwner() == peerId || candidate.GetZDOID().UserID == peerId))
        return candidate;
    }
    return null;
  }

  bool InvokeWithContext(string actionId, string deliveryMode, Action invoke) {
    OutboundContext previous = _outboundContext;
    OutboundContext current = new() {
        ActionId = actionId,
        DeliveryMode = deliveryMode
    };
    _outboundContext = current;
    try {
      invoke();
      return current.Queued;
    } finally {
      _outboundContext = previous;
    }
  }

  public bool InvokeTyped(
      string actionId,
      Action invoke,
      string deliveryMode = "deliver") {
    if (!SafeToken(actionId, 80) || invoke == null
        || deliveryMode is not ("deliver" or "withhold"))
      return false;
    return InvokeWithContext(actionId, deliveryMode, invoke);
  }

  void CompleteProbe(string runId, string actionId, string expectedMode, string detail) {
    if (_probe == null || _probe.Terminal
        || !string.Equals(_probe.RunId, runId, StringComparison.Ordinal)
        || !string.Equals(_probe.ActionId, actionId, StringComparison.Ordinal)
        || !string.Equals(_probe.Mode, expectedMode, StringComparison.Ordinal))
      return;
    _probe.LumberjacksReceived++;
    _probe.Terminal = true;
    _probe.Success = true;
    _probe.Detail = detail;
    Write(
        "probe_passed", runId, actionId, string.Empty,
        0, 0, 0, ZDOID.None, detail);
  }

  void EvaluateDeadline(float now) {
    if (_probe == null || _probe.Terminal || now <= _probe.DeadlineAt) return;
    _probe.Terminal = true;
    if (_probe.Mode == "withhold" && _probe.OutboundQueued
        && _probe.NativeReceived == 0 && _probe.LumberjacksReceived == 0) {
      _probe.Success = true;
      _probe.Detail = "routed_response_stale_no_native_fallback";
      Write(
          "routed_expected_stale", _probe.RunId, _probe.ActionId, ResponseMethod,
          0, 0, 0, ZDOID.None, _probe.Detail);
    } else {
      _probe.Success = false;
      _probe.Detail = "routed_probe_deadline_exceeded";
      Write(
          "routed_probe_failed", _probe.RunId, _probe.ActionId, string.Empty,
          0, 0, 0, ZDOID.None, _probe.Detail);
    }
  }

  static ZPackage BuildProbePackage(string runId, string actionId, string mode) {
    ZPackage package = new();
    package.Write(runId);
    package.Write(actionId);
    package.Write(mode);
    package.SetPos(0);
    return package;
  }

  static bool ReadProbePackage(
      ZPackage package,
      out string runId,
      out string actionId,
      out string mode) {
    runId = string.Empty;
    actionId = string.Empty;
    mode = string.Empty;
    try {
      package.SetPos(0);
      runId = package.ReadString();
      actionId = package.ReadString();
      mode = package.ReadString();
      return SafeToken(runId, 80) && SafeToken(actionId, 80)
          && SafeToken(mode, 80);
    } catch {
      return false;
    }
  }

  void Write(
      string state,
      string runId,
      string actionId,
      string methodName,
      long messageId,
      long senderPeerId,
      long targetPeerId,
      ZDOID targetZdo,
      string detail) {
    _writer.Write(
        ReceiptFileName,
        new Dictionary<string, object> {
            ["schema_version"] = 1,
            ["timestamp_utc"] =
                DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["state"] = state,
            ["run_id"] = SafeToken(runId, 80) ? runId : "unscoped",
            ["role"] = Role(),
            ["action_id"] = SafeToken(actionId, 80) ? actionId : "unscoped",
            ["method"] = methodName ?? string.Empty,
            ["message_id"] = messageId,
            ["sender_peer_id"] = senderPeerId,
            ["target_peer_id"] = targetPeerId,
            ["target_zdo"] = targetZdo.ToString(),
            ["detail"] = detail ?? string.Empty
        });
  }

  static string CurrentRunId() {
    string request = NativeAutotestRequest.ActiveRunId;
    if (SafeToken(request, 80)) return request;
    string configured = PluginConfig.NativeNetworkEvidenceRunId?.Value;
    return SafeToken(configured, 80) ? configured.Trim() : "unscoped";
  }

  static bool CutoverEnabled() =>
      PluginConfig.RoutedRpcCutoverEnabled?.Value == true
      || NativeAutotestRequest.ActiveRoutedRpcCutover;

  static bool IsServer() {
    try { return ZNet.instance != null && ZNet.instance.IsServer(); }
    catch { return false; }
  }

  static string Role() {
    try {
      if (ZNet.instance == null) return "starting";
      return ZNet.instance.IsServer() ? "server" : "client";
    } catch {
      return "unknown";
    }
  }

  static bool SafeToken(string value, int maxLength) {
    if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength) return false;
    foreach (char character in value)
      if (!char.IsLetterOrDigit(character)
          && character is not '-' and not '_' and not '.')
        return false;
    return true;
  }

  public void Dispose() {
    if (_disposed) return;
    _disposed = true;
    Interlocked.CompareExchange(ref _active, null, this);
    _writer.Dispose();
  }

  sealed class RoutedProbe {
    public string RunId;
    public string ActionId;
    public string Mode;
    public float DeadlineAt;
    public int NativeReceived;
    public int LumberjacksReceived;
    public bool OutboundQueued;
    public bool Terminal;
    public bool Success;
    public string Detail = string.Empty;
  }

  sealed class InboundRoute {
    public long ReliableSequence;
    public string RunId;
    public string ActionId;
    public string RouteId;
    public long MessageId;
    public long SenderPeerId;
    public long TargetPeerId;
    public ZDOID TargetZdo;
    public string TargetKind;
    public string MethodName;
    public int MethodHash;
    public string ParametersBase64;
  }

  sealed class OutboundContext {
    public string ActionId;
    public string DeliveryMode;
    public bool Queued;
  }
}
