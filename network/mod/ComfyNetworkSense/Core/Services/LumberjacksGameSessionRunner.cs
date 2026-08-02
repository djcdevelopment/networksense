namespace ComfyNetworkSense;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using UnityEngine;

/// <summary>
/// Canonical authenticated Lumberjacks game-session connection. Reliable control and, later,
/// semantic RPC/ZDO traffic share this one WebSocket and UDP binding instead of opening a socket
/// per subsystem. Worker results are banked and become visible only from <see cref="Update"/>.
/// </summary>
public sealed class LumberjacksGameSessionRunner : IDisposable {
  const int ConnectTimeoutMs = 5000;
  const int ReceiveBufferBytes = 8192;
  const int MaxOutboundFrames = 256;
  const int MaxMotionFrames = 64;
  const float HeartbeatIntervalSeconds = 2.0f;
  const string ReceiptFileName = "lumberjacks-game-session.jsonl";
  static LumberjacksGameSessionRunner _active;

  readonly object _gate = new();
  readonly ConcurrentQueue<SessionEvent> _events = new();
  readonly ConcurrentQueue<string> _outbound = new();
  readonly ConcurrentQueue<MotionOutbound> _motionOutbound = new();
  readonly object _outboundGate = new();
  readonly TelemetryLogWriter _writer = new();

  CancellationTokenSource _cts;
  Task _connectionTask;
  ClientWebSocket _socket;
  UdpClient _udp;
  ulong _udpToken;
  SessionProbe _probe;
  DirectPulseProbe _directProbe;
  string _state = "idle";
  string _lastError = string.Empty;
  string _serverInstanceId = string.Empty;
  string _worldId = string.Empty;
  string _connectionId = string.Empty;
  string _logicalPeerId = string.Empty;
  string _resumeToken = string.Empty;
  long _resumeEpoch = -1;
  long _lastServerSequence;
  long _nextClientSequence;
  int _outboundCount;
  int _motionOutboundCount;
  long _motionSentUdp;
  long _motionSentWebSocket;
  long _motionReceivedUdp;
  long _motionReceivedWebSocket;
  float _nextConnectAt;
  float _nextHeartbeatAt;
  bool _webSocketConnected;
  bool _udpReady;
  bool _disposed;
  string _localRole = "client";
  long _localPeerUid;
  long _localCharacterZdoUserId;
  uint _localCharacterZdoId;

  public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;
  public bool WebSocketConnected { get { lock (_gate) return _webSocketConnected; } }
  public bool UdpReady { get { lock (_gate) return _udpReady; } }
  public string State { get { lock (_gate) return _state; } }
  public string LastError { get { lock (_gate) return _lastError; } }
  public string ConnectionId { get { lock (_gate) return _connectionId; } }
  public string LogicalPeerId { get { lock (_gate) return _logicalPeerId; } }
  public long ResumeEpoch { get { lock (_gate) return _resumeEpoch; } }

  public LumberjacksGameSessionRunner() {
    LumberjacksGameSessionRunner previous = Interlocked.Exchange(ref _active, this);
    previous?.Dispose();
  }

  public void Update(float now) {
    if (_disposed) return;
    if (!ShouldRun()) {
      if (IsRunning) Stop();
      return;
    }
    if (!IsRunning && now >= _nextConnectAt) Start(now);
    if (WebSocketConnected && now >= _nextHeartbeatAt) {
      if (TryQueueEnvelope(
              "valheim_session_heartbeat",
              "\"role\":\"" + _localRole + "\""))
        _nextHeartbeatAt = now + HeartbeatIntervalSeconds;
      else
        _nextHeartbeatAt = now + 0.5f;
    }
    DrainEvents();
    EvaluateProbeDeadline(now);
    EvaluateDirectProbeDeadline(now);
  }

  public bool BeginDirectPulseProbe(
      string actionId,
      string mode,
      float deadlineSeconds,
      out string detail) {
    detail = string.Empty;
    if (!SafeToken(actionId, 80) || mode is not ("deliver" or "withhold")) {
      detail = "direct_pulse_parameters_invalid";
      return false;
    }
    string runId = NativeAutotestRequest.ActiveRunId;
    if (!SafeToken(runId, 80)) {
      detail = "native_autotest_run_missing";
      return false;
    }
    string receiptLog;
    lock (_gate) {
      if (!_webSocketConnected || string.IsNullOrEmpty(_connectionId)) {
        detail = "lumberjacks_session_not_connected";
        return false;
      }
      if (_directProbe != null && !_directProbe.Terminal) {
        detail = "another_direct_pulse_probe_active";
        return false;
      }
      _directProbe = new DirectPulseProbe {
          ActionId = actionId,
          RunId = runId,
          Mode = mode,
          DeadlineAt = Time.unscaledTime + Mathf.Clamp(deadlineSeconds, 1.0f, 30.0f)
      };
      if (!TryQueueEnvelope(
              "valheim_direct_pulse_probe",
              "\"run_id\":\"" + Escape(runId)
              + "\",\"action_id\":\"" + Escape(actionId)
              + "\",\"mode\":\"" + Escape(mode) + "\"")) {
        _directProbe = null;
        detail = "client_send_queue_full";
        return false;
      }
      receiptLog = WriteReceipt(
          "direct_pulse_probe_started", actionId,
          "mode=" + mode + " connection_id=" + _connectionId);
    }
    ComfyNetworkSense.LogInfo(receiptLog);
    return true;
  }

  public bool TryQueueLogicalPeerControl(string payloadFields) =>
      TryQueueSemantic("valheim_logical_peer_control", payloadFields);

  public bool TryGetDirectPulseProbeResult(
      string actionId,
      out bool terminal,
      out bool success,
      out string detail) {
    lock (_gate) {
      if (_directProbe == null
          || !string.Equals(_directProbe.ActionId, actionId, StringComparison.Ordinal)) {
        terminal = false;
        success = false;
        detail = "direct_pulse_probe_not_found";
        return false;
      }
      terminal = _directProbe.Terminal;
      success = _directProbe.Success;
      detail = _directProbe.Detail ?? string.Empty;
      return true;
    }
  }

  public static void NotifyNativeDirectPulse(string runId, long sequence) {
    LumberjacksGameSessionRunner active = Volatile.Read(ref _active);
    active?._events.Enqueue(new SessionEvent(
        "native_direct_pulse_received", sequence, string.Empty,
        "run_id=" + (runId ?? string.Empty) + " unexpected_native_fallback"));
  }

  public bool TryQueueRoutedRpc(
      string runId,
      string actionId,
      string routeId,
      long messageId,
      long senderPeerId,
      long targetPeerId,
      long targetZdoUserId,
      uint targetZdoId,
      string methodName,
      int methodHash,
      string parametersBase64,
      string deliveryMode,
      out string detail) {
    detail = string.Empty;
    lock (_gate) {
      if (!_webSocketConnected || string.IsNullOrEmpty(_connectionId)) {
        detail = "lumberjacks_session_not_connected";
        return false;
      }
    }
    if (!SafeToken(runId, 80) || !SafeToken(actionId, 80)
        || !SafeToken(routeId, 192) || !SafeToken(methodName, 96)
        || deliveryMode is not ("deliver" or "withhold")
        || parametersBase64 == null || parametersBase64.Length > 48000) {
      detail = "routed_rpc_parameters_invalid";
      return false;
    }
    string fields =
        "\"run_id\":\"" + Escape(runId)
        + "\",\"action_id\":\"" + Escape(actionId)
        + "\",\"route_id\":\"" + Escape(routeId)
        + "\",\"message_id\":" + messageId.ToString(CultureInfo.InvariantCulture)
        + ",\"sender_peer_id\":" + senderPeerId.ToString(CultureInfo.InvariantCulture)
        + ",\"target_peer_id\":" + targetPeerId.ToString(CultureInfo.InvariantCulture)
        + ",\"target_zdo_user_id\":"
        + targetZdoUserId.ToString(CultureInfo.InvariantCulture)
        + ",\"target_zdo_id\":" + targetZdoId.ToString(CultureInfo.InvariantCulture)
        + ",\"method_name\":\"" + Escape(methodName)
        + "\",\"method_hash\":" + methodHash.ToString(CultureInfo.InvariantCulture)
        + ",\"parameters_base64\":\"" + Escape(parametersBase64)
        + "\",\"delivery_mode\":\"" + deliveryMode + "\"";
    if (!TryQueueEnvelope("valheim_routed_rpc_send", fields)) {
      detail = "client_send_queue_full";
      return false;
    }
    detail = "lumberjacks_routed_rpc_queued";
    return true;
  }

  public bool QueueReliableAck(long serverSequence) =>
      serverSequence > 0 && TryQueueEnvelope(
          "reliable_ack",
          "\"through_sequence\":"
          + serverSequence.ToString(CultureInfo.InvariantCulture));

  public bool TryQueueZdoJournalMutation(string payloadFields) =>
      TryQueueSemantic("valheim_zdo_mutation", payloadFields);

  public bool TryQueueZdoJournalInterest(string payloadFields) =>
      TryQueueSemantic("valheim_zdo_interest", payloadFields);

  public bool TryQueueOwnershipLeaseRequest(string payloadFields) =>
      TryQueueSemantic("valheim_ownership_lease_request", payloadFields);

  public bool TryQueueOwnershipLeaseIssue(string payloadFields) =>
      TryQueueSemantic("valheim_ownership_lease_issue", payloadFields);

  public bool TryQueueOwnershipAction(string payloadFields) =>
      TryQueueSemantic("valheim_ownership_action", payloadFields);

  public bool TryQueueOwnershipActionResult(string payloadFields) =>
      TryQueueSemantic("valheim_ownership_action_result", payloadFields);

  public bool TryQueueWorldDescriptorPublish(string payloadFields) =>
      TryQueueSemantic("valheim_world_descriptor_publish", payloadFields);

  public bool TryQueueWorldDescriptorRequest(string payloadFields) =>
      TryQueueSemantic("valheim_world_descriptor_request", payloadFields);

  public bool TryQueueZoneMembershipEnter(string payloadFields) =>
      TryQueueSemantic("valheim_zone_membership_enter", payloadFields);

  public bool TryQueueZoneSnapshotPublish(string payloadFields) =>
      TryQueueSemantic("valheim_zone_snapshot_publish", payloadFields);

  public bool TryQueueZoneSnapshotAck(string payloadFields) =>
      TryQueueSemantic("valheim_zone_snapshot_ack", payloadFields);

  public bool TryQueueZoneMembershipLeave(string payloadFields) =>
      TryQueueSemantic("valheim_zone_membership_leave", payloadFields);

  public bool TryQueueMotionResync(string payloadFields) =>
      TryQueueSemantic("valheim_motion_resync_publish", payloadFields);

  public bool TryQueueMotion(
      ushort sequence, ValheimMotionSnapshot snapshot, out string detail) {
    detail = string.Empty;
    lock (_gate) {
      if (!_webSocketConnected || string.IsNullOrEmpty(_connectionId)) {
        detail = "lumberjacks_session_not_connected";
        return false;
      }
    }
    int depth = Interlocked.Increment(ref _motionOutboundCount);
    if (depth > MaxMotionFrames) {
      Interlocked.Decrement(ref _motionOutboundCount);
      detail = "motion_send_queue_full";
      return false;
    }
    _motionOutbound.Enqueue(new MotionOutbound(sequence, snapshot));
    detail = "canonical_session_motion_queued";
    return true;
  }

  public bool AbortForOwnershipProbe() {
    ClientWebSocket socket;
    lock (_gate) socket = _socket;
    if (socket == null || socket.State != WebSocketState.Open) return false;
    socket.Abort();
    return true;
  }

  public bool QueueZdoJournalAck(
      string worldEpoch, long journalSequence, long reliableSequence) {
    if (!SafeToken(worldEpoch, 96) ||
        journalSequence <= 0 || reliableSequence <= 0) return false;
    bool journal = TryQueueSemantic(
        "valheim_zdo_ack",
        "\"world_epoch\":\"" + Escape(worldEpoch)
        + "\",\"sequences\":[" + journalSequence.ToString(CultureInfo.InvariantCulture)
        + "]");
    bool reliable = QueueReliableAck(reliableSequence);
    return journal && reliable;
  }

  public bool BeginProbe(
      string actionId,
      string mode,
      float deadlineSeconds,
      out string detail) {
    detail = string.Empty;
    if (!SafeToken(actionId, 80) ||
        mode is not ("resume" or "withhold_receipt" or "external_resume")) {
      detail = "probe_parameters_invalid";
      return false;
    }

    string runId = NativeAutotestRequest.ActiveRunId;
    if (!SafeToken(runId, 80)) {
      detail = "native_autotest_run_missing";
      return false;
    }

    string receiptLog;
    lock (_gate) {
      if (!_webSocketConnected || string.IsNullOrEmpty(_connectionId)) {
        detail = "lumberjacks_session_not_connected";
        return false;
      }
      if (_probe != null && !_probe.Terminal) {
        detail = "another_session_probe_active";
        return false;
      }
      _probe = new SessionProbe {
          ActionId = actionId,
          ProbeId = runId + "." + NativeAutotestRequest.ActiveClient + "." + actionId,
          RunId = runId,
          Mode = mode,
          StartedAt = Time.unscaledTime,
          DeadlineAt = Time.unscaledTime + Mathf.Clamp(deadlineSeconds, 1.0f, 60.0f),
          InitialConnectionId = _connectionId,
          InitialLogicalPeerId = _logicalPeerId,
          InitialResumeEpoch = _resumeEpoch
      };
      if (mode == "external_resume") {
        receiptLog = WriteReceipt("probe_started", actionId,
            "mode=" + mode + " connection_id=" + _connectionId
            + " resume_epoch=" + _resumeEpoch);
      } else {
        if (!TryQueueEnvelope(
                "valheim_session_probe",
                "\"run_id\":\"" + Escape(runId)
                + "\",\"probe_id\":\"" + Escape(_probe.ProbeId)
                + "\",\"mode\":\"" + Escape(mode) + "\"")) {
          _probe = null;
          detail = "client_send_queue_full";
          return false;
        }
        receiptLog = WriteReceipt("probe_started", actionId,
            "mode=" + mode + " connection_id=" + _connectionId
            + " resume_epoch=" + _resumeEpoch);
      }
    }
    ComfyNetworkSense.LogInfo(receiptLog);
    return true;
  }

  public bool TryGetProbeResult(
      string actionId,
      out bool terminal,
      out bool success,
      out string detail) {
    lock (_gate) {
      if (_probe == null || !string.Equals(_probe.ActionId, actionId, StringComparison.Ordinal)) {
        terminal = false;
        success = false;
        detail = "probe_not_found";
        return false;
      }
      terminal = _probe.Terminal;
      success = _probe.Success;
      detail = _probe.Detail ?? string.Empty;
      return true;
    }
  }

  public IDictionary<string, object> Snapshot() {
    lock (_gate) {
      return new Dictionary<string, object> {
          ["state"] = _state,
          ["websocket_connected"] = _webSocketConnected,
          ["udp_ready"] = _udpReady,
          ["server_instance_id"] = _serverInstanceId,
          ["world_id"] = _worldId,
          ["connection_id"] = _connectionId,
          ["logical_peer_id"] = _logicalPeerId,
          ["resume_epoch"] = _resumeEpoch,
          ["last_server_sequence"] = _lastServerSequence,
          ["next_client_sequence"] = Interlocked.Read(ref _nextClientSequence),
          ["outbound_queue_depth"] = Volatile.Read(ref _outboundCount),
          ["motion_outbound_queue_depth"] =
              Volatile.Read(ref _motionOutboundCount),
          ["motion_sent_udp"] = Interlocked.Read(ref _motionSentUdp),
          ["motion_sent_websocket"] =
              Interlocked.Read(ref _motionSentWebSocket),
          ["motion_received_udp"] = Interlocked.Read(ref _motionReceivedUdp),
          ["motion_received_websocket"] =
              Interlocked.Read(ref _motionReceivedWebSocket),
          ["probe_state"] = _probe == null
              ? "none"
              : (_probe.Terminal ? (_probe.Success ? "passed" : "failed") : "running"),
          ["probe_detail"] = _probe?.Detail ?? string.Empty,
          ["direct_probe_state"] = _directProbe == null
              ? "none"
              : (_directProbe.Terminal
                  ? (_directProbe.Success ? "passed" : "failed")
                  : "running"),
          ["direct_probe_detail"] = _directProbe?.Detail ?? string.Empty,
          ["last_error"] = _lastError
      };
    }
  }

  void Start(float now) {
    Stop();
    try {
      _localRole = ZNet.instance != null && ZNet.instance.IsServer() ? "server" : "client";
      _localPeerUid = ZNet.instance != null ? ZNet.GetUID() : 0;
      _localCharacterZdoUserId = 0;
      _localCharacterZdoId = 0;
      if (_localRole == "client") {
        ZDO localCharacter = Player.m_localPlayer?
            .GetComponent<ZNetView>()?.GetZDO();
        if (localCharacter != null) {
          _localCharacterZdoUserId = localCharacter.m_uid.UserID;
          _localCharacterZdoId = localCharacter.m_uid.ID;
        }
      }
    } catch {
      _localRole = "client";
      _localPeerUid = 0;
      _localCharacterZdoUserId = 0;
      _localCharacterZdoId = 0;
    }
    _nextConnectAt = now + 2.0f;
    _nextHeartbeatAt = now + HeartbeatIntervalSeconds;
    _cts = new CancellationTokenSource();
    SetState("connecting", false, false, string.Empty);
    _connectionTask = Task.Run(() => RunConnections(_cts.Token));
  }

  public void Stop() {
    try { _cts?.Cancel(); _socket?.Abort(); _udp?.Close(); } catch { }
    _cts?.Dispose();
    _cts = null;
    _socket = null;
    _udp = null;
    _udpToken = 0;
    SetState("idle", false, false, string.Empty);
  }

  async Task RunConnections(CancellationToken token) {
    while (!token.IsCancellationRequested) {
      try {
        await RunOneConnection(token).ConfigureAwait(false);
      } catch (OperationCanceledException) when (token.IsCancellationRequested) {
        break;
      } catch (Exception exception) {
        string fault = NativeAutotestRequest.ActiveColdJoinFault;
        _events.Enqueue(new SessionEvent(
            "connection_error", 0, string.Empty,
            (string.IsNullOrEmpty(fault)
                ? string.Empty : "fault_mode=" + fault + " ")
            + exception.GetType().Name + ":" + exception.Message));
      } finally {
        try { _udp?.Close(); } catch { }
        _udp = null;
        _udpToken = 0;
        _socket = null;
        _events.Enqueue(new SessionEvent("socket_closed", 0, string.Empty, string.Empty));
      }

      if (!token.IsCancellationRequested) {
        try { await Task.Delay(500, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { break; }
      }
    }
  }

  async Task RunOneConnection(CancellationToken token) {
    using ClientWebSocket socket = new();
    LumberjacksClientAuth.Apply(socket);
    _socket = socket;
    string resume;
    lock (_gate) resume = _resumeToken;
    using (CancellationTokenSource timeout =
        CancellationTokenSource.CreateLinkedTokenSource(token)) {
      timeout.CancelAfter(ConnectTimeoutMs);
      await socket.ConnectAsync(new Uri(GatewayUrl(resume)), timeout.Token)
          .ConfigureAwait(false);
    }

    using CancellationTokenSource connectionCts =
        CancellationTokenSource.CreateLinkedTokenSource(token);
    Task sender = RunOutgoing(socket, connectionCts.Token);
    Task udpReceiver = null;
    byte[] buffer = new byte[ReceiveBufferBytes];
    try {
      while (!token.IsCancellationRequested && socket.State == WebSocketState.Open) {
        ReceivedFrame frame = await ReceiveFrame(socket, buffer, token).ConfigureAwait(false);
        if (frame == null) break;
        if (frame.Type == WebSocketMessageType.Binary) {
          if (ValheimMotionCodec.TryRead(
                  frame.Data, tokenPrefixed: false,
                  out ushort motionSequence,
                  out ValheimMotionSnapshot motion)) {
            Interlocked.Increment(ref _motionReceivedWebSocket);
            LumberjacksMotionRunner.EnqueueCanonicalMotion(
                motionSequence, motion, udp: false);
            LogicalPeerCutoverRunner.EnqueueCanonicalMotion(
                motionSequence, motion);
          }
          continue;
        }
        if (frame.Type != WebSocketMessageType.Text) continue;
        string text = Encoding.UTF8.GetString(frame.Data);
        string type = ExtractJsonString(text, "type");
        if (string.Equals(type, "session_started", StringComparison.OrdinalIgnoreCase)) {
          HandleSessionStartedWorker(text);
          if (_udp != null && udpReceiver == null)
            udpReceiver = RunUdpReceive(_udp, connectionCts.Token);
          continue;
        }
        if (string.Equals(type, "valheim_control_request", StringComparison.OrdinalIgnoreCase)) {
          if (HandleControlRequestWorker(text, socket)) break;
          continue;
        }
        if (string.Equals(type, "valheim_control_receipt", StringComparison.OrdinalIgnoreCase)) {
          HandleControlReceiptWorker(text);
          continue;
        }
        if (string.Equals(type, "valheim_direct_pulse", StringComparison.OrdinalIgnoreCase)) {
          HandleDirectPulseWorker(text);
          continue;
        }
        if (string.Equals(type, "valheim_routed_rpc", StringComparison.OrdinalIgnoreCase)) {
          HandleRoutedRpcWorker(text);
          continue;
        }
        if (type is
            "valheim_logical_peer_attached" or
            "valheim_logical_peer_detached" or
            "valheim_logical_peer_control") {
          LogicalPeerCutoverRunner.EnqueueCanonicalFrame(type, text);
          continue;
        }
        if (type is
            "valheim_remote_player_descriptor" or
            "valheim_remote_player_tombstone") {
          LumberjacksMotionRunner.EnqueueCanonicalFrame(type, text);
          continue;
        }
        if (string.Equals(type, "valheim_zdo_delivery", StringComparison.OrdinalIgnoreCase)) {
          ZdoJournalCutoverRunner.EnqueueCanonicalDelivery(text);
          continue;
        }
        if (string.Equals(type, "valheim_zdo_mutation_receipt",
                StringComparison.OrdinalIgnoreCase)) {
          HandleZdoMutationReceiptWorker(text);
          continue;
        }
        if (string.Equals(type, "valheim_zdo_interest_receipt",
                StringComparison.OrdinalIgnoreCase)) {
          HandleZdoInterestReceiptWorker(text);
          continue;
        }
        if (string.Equals(type, "valheim_zdo_interest_status",
                StringComparison.OrdinalIgnoreCase)) {
          HandleZdoInterestStatusWorker(text);
          continue;
        }
        if (type is
            "valheim_ownership_lease_command" or
            "valheim_ownership_lease_granted" or
            "valheim_ownership_lease_receipt" or
            "valheim_ownership_action_rejected" or
            "valheim_ownership_action_authorized" or
            "valheim_ownership_action_completed" or
            "valheim_ownership_result_receipt") {
          OwnershipLeaseCutoverRunner.EnqueueCanonicalFrame(type, text);
          continue;
        }
        if (type is
            "valheim_world_descriptor" or
            "valheim_world_descriptor_receipt" or
            "valheim_world_descriptor_status" or
            "valheim_zone_snapshot_build" or
            "valheim_zone_snapshot_chunk" or
            "valheim_zone_snapshot_complete" or
            "valheim_zone_snapshot_release" or
            "valheim_zone_membership_left") {
          WorldZoneCutoverRunner.EnqueueCanonicalFrame(type, text);
          continue;
        }
        if (type is
            "valheim_motion_resync" or
            "valheim_motion_resync_receipt") {
          LumberjacksMotionRunner.EnqueueCanonicalFrame(type, text);
        }
      }
    } finally {
      connectionCts.Cancel();
      try { await sender.ConfigureAwait(false); } catch { }
      if (udpReceiver != null) {
        try { await udpReceiver.ConfigureAwait(false); } catch { }
      }
    }
  }

  async Task RunOutgoing(ClientWebSocket socket, CancellationToken token) {
    while (!token.IsCancellationRequested && socket.State == WebSocketState.Open) {
      if (_outbound.TryDequeue(out string text)) {
        Interlocked.Decrement(ref _outboundCount);
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        await socket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            true,
            token).ConfigureAwait(false);
        string type = ExtractJsonString(text, "type");
        if (IsOwnershipType(type)) {
          _events.Enqueue(new SessionEvent(
              "ownership_frame_sent",
              ExtractJsonLong(text, "seq"),
              ConnectionId,
              "type=" + type));
        }
        continue;
      }

      if (_motionOutbound.TryDequeue(out MotionOutbound motion)) {
        Interlocked.Decrement(ref _motionOutboundCount);
        bool sentUdp = false;
        UdpClient udp = _udp;
        ulong udpToken = _udpToken;
        if (AlphaTransportSwitches.LumberjacksUdpEnabled &&
            udp != null && udpToken != 0) {
          try {
            byte[] packet = ValheimMotionCodec.BuildUdpPacket(
                udpToken, motion.Sequence, motion.Snapshot);
            await udp.SendAsync(packet, packet.Length).ConfigureAwait(false);
            Interlocked.Increment(ref _motionSentUdp);
            sentUdp = true;
          } catch (SocketException exception) when (!token.IsCancellationRequested) {
            // A TCP-only gateway tunnel can advertise a UDP port that is unreachable from
            // this client. Connected UDP reports that ICMP failure on a later send. Retain
            // the numbered frame, retire UDP for this socket incarnation, and immediately
            // use the canonical WebSocket binary lane instead of faulting the sender task.
            RetireUdp(
                udp, motion.Sequence, "send", exception.SocketErrorCode);
          }
        }
        if (!sentUdp) {
          byte[] frame = ValheimMotionCodec.BuildWebSocketFrame(
              motion.Sequence, motion.Snapshot);
          await socket.SendAsync(
              new ArraySegment<byte>(frame),
              WebSocketMessageType.Binary,
              true,
              token).ConfigureAwait(false);
          Interlocked.Increment(ref _motionSentWebSocket);
        }
        LumberjacksMotionRunner.NotifyCanonicalMotionSent(
            motion.Sequence, sentUdp);
        continue;
      }

      await Task.Delay(10, token).ConfigureAwait(false);
    }
  }

  async Task RunUdpReceive(UdpClient udp, CancellationToken token) {
    using (token.Register(() => {
      try { udp.Close(); } catch { }
    })) {
      while (!token.IsCancellationRequested) {
        try {
          UdpReceiveResult result = await udp.ReceiveAsync().ConfigureAwait(false);
          if (ValheimMotionCodec.TryRead(
                  result.Buffer, tokenPrefixed: true,
                  out ushort sequence,
                  out ValheimMotionSnapshot motion)) {
            Interlocked.Increment(ref _motionReceivedUdp);
            LumberjacksMotionRunner.EnqueueCanonicalMotion(
                sequence, motion, udp: true);
            LogicalPeerCutoverRunner.EnqueueCanonicalMotion(
                sequence, motion);
          }
        } catch (ObjectDisposedException) {
          break;
        } catch (SocketException exception) {
          if (token.IsCancellationRequested) break;
          // Windows can surface ICMP port-unreachable on ReceiveAsync before the
          // sender observes it. Retire once and break; retrying the connected socket
          // here is an immediate ConnectionReset spin.
          RetireUdp(udp, 0, "receive", exception.SocketErrorCode);
          break;
        }
      }
    }
  }

  bool RetireUdp(
      UdpClient udp, long sequence, string source, SocketError error) {
    bool retired = false;
    lock (_gate) {
      if (ReferenceEquals(_udp, udp)) {
        _udp = null;
        _udpToken = 0;
        _udpReady = false;
        retired = true;
      }
    }
    try { udp.Close(); } catch { }
    if (retired) {
      _events.Enqueue(new SessionEvent(
          "motion_transport_fallback",
          sequence,
          ConnectionId,
          "udp_" + source + "_failed=" + error
          + " fallback=binary_websocket"));
    }
    return retired;
  }

  void HandleSessionStartedWorker(string text) {
    string serverInstance = ExtractJsonString(text, "server_instance_id");
    string world = ExtractJsonString(text, "world_id");
    string connection = ExtractJsonString(text, "client_connection_id");
    string logicalPeer = ExtractJsonString(text, "logical_peer_id");
    string resumeToken = ExtractJsonString(text, "resume_token");
    long epoch = ExtractJsonLong(text, "resume_epoch");
    int udpPort = (int)ExtractJsonLong(text, "udp_port");
    string udpToken = ExtractJsonString(text, "udp_token");
    bool resumed = ExtractJsonBool(text, "resumed");
    bool resumeRejected = ExtractJsonBool(text, "resume_rejected");
    string role = ExtractJsonString(text, "valheim_role");
    if (!SafeToken(connection, 80) || !SafeToken(logicalPeer, 80)
        || string.IsNullOrWhiteSpace(resumeToken)
        || string.IsNullOrWhiteSpace(serverInstance) || string.IsNullOrWhiteSpace(world)
        || !string.Equals(role, _localRole, StringComparison.Ordinal)) {
      throw new InvalidDataException("session_started missing durable game-session fields");
    }

    string previousConnection;
    string previousLogicalPeer;
    long previousEpoch;
    lock (_gate) {
      previousConnection = _connectionId;
      previousLogicalPeer = _logicalPeerId;
      previousEpoch = _resumeEpoch;
      _serverInstanceId = serverInstance;
      _worldId = world;
      _connectionId = connection;
      _logicalPeerId = logicalPeer;
      _resumeToken = resumeToken;
      _resumeEpoch = epoch;
    }
    if (!string.IsNullOrEmpty(previousLogicalPeer) &&
        !string.Equals(previousLogicalPeer, logicalPeer, StringComparison.Ordinal)) {
      throw new InvalidDataException("logical peer identity changed across reconnect");
    }
    bool reincarnated = !string.IsNullOrEmpty(previousConnection) && !resumed;
    if (!string.IsNullOrEmpty(previousConnection) && resumed &&
        (!string.Equals(previousConnection, connection, StringComparison.Ordinal) ||
         epoch <= previousEpoch)) {
      throw new InvalidDataException("socket resume did not preserve connection id and advance epoch");
    }
    if (reincarnated) {
      if (!resumeRejected)
        throw new InvalidDataException("new transport incarnation was not caused by rejected resume");
      ClearOutbound();
      lock (_gate) {
        _lastServerSequence = 0;
        Interlocked.Exchange(ref _nextClientSequence, 0);
      }
      ZdoJournalCutoverRunner.NotifySessionReincarnated();
      WorldZoneCutoverRunner.NotifySessionReincarnated();
    }

    try {
      _udp?.Close();
      _udp = null;
      _udpToken = 0;
      if (udpPort > 0 && ulong.TryParse(udpToken, out ulong parsedUdpToken)) {
        UdpClient udp = new();
        udp.Connect(new Uri(NormalizeGatewayUrl()).Host, udpPort);
        _udp = udp;
        _udpToken = parsedUdpToken;
      }
    } catch {
      _udp = null;
      _udpToken = 0;
    }
    _events.Enqueue(new SessionEvent(
        "session_started", epoch, connection,
        "resumed=" + (resumed ? "true" : "false")
        + " reincarnated=" + (reincarnated ? "true" : "false")
        + " logical_peer_id=" + logicalPeer
        + " server_instance_id=" + serverInstance
        + " world_id=" + world
        + " udp_ready=" + (_udp != null ? "true" : "false")));
    string peerBinding =
        "\"role\":\"" + _localRole
        + "\",\"peer_uid\":"
        + _localPeerUid.ToString(CultureInfo.InvariantCulture);
    if (_localRole == "client" &&
        _localCharacterZdoUserId != 0 && _localCharacterZdoId != 0) {
      peerBinding +=
          ",\"character_zdo_user_id\":"
          + _localCharacterZdoUserId.ToString(CultureInfo.InvariantCulture)
          + ",\"character_zdo_id\":"
          + _localCharacterZdoId.ToString(CultureInfo.InvariantCulture);
    }
    if (_localPeerUid == 0 || !TryQueueEnvelope(
            "valheim_peer_bind", peerBinding)) {
      throw new InvalidDataException("failed to queue Valheim peer binding");
    }
    _events.Enqueue(new SessionEvent(
        "peer_bind_queued", 0, connection,
        "role=" + _localRole
        + " peer_uid=" + _localPeerUid.ToString(CultureInfo.InvariantCulture)
        + (_localCharacterZdoId == 0
            ? string.Empty
            : " character_id="
              + _localCharacterZdoUserId.ToString(CultureInfo.InvariantCulture)
              + ":"
              + _localCharacterZdoId.ToString(CultureInfo.InvariantCulture))));
    if (_localRole == "client" &&
        NativeAutotestRequest.ActiveMotionAuthorityCutover) {
      if (!TryQueueEnvelope(
              "join_region",
              "\"region_id\":\""
              + Escape(PluginConfig.LumberjacksRegionId.Value) + "\""))
        throw new InvalidDataException("failed to queue motion region binding");
      _events.Enqueue(new SessionEvent(
          "motion_region_join_queued", 0, connection,
          "region_id=" + PluginConfig.LumberjacksRegionId.Value));
    }
  }

  bool HandleControlRequestWorker(string text, ClientWebSocket socket) {
    string probeId = ExtractJsonString(text, "probe_id");
    string connectionId = ExtractJsonString(text, "connection_id");
    string mode = ExtractJsonString(text, "mode");
    long sequence = ExtractJsonLong(text, "seq");
    SessionProbe probe;
    lock (_gate) probe = _probe;
    if (probe == null || probe.Terminal
        || !string.Equals(probe.ProbeId, probeId, StringComparison.Ordinal)
        || !string.Equals(probe.InitialConnectionId, connectionId, StringComparison.Ordinal)
        || sequence < 1) {
      throw new InvalidDataException("control request did not match active probe");
    }

    _events.Enqueue(new SessionEvent(
        "control_request", sequence, connectionId,
        "probe_id=" + probeId + " mode=" + mode));

    if (mode == "resume" && !probe.DropPerformed) {
      lock (_gate) {
        if (_probe != null) {
          _probe.DropPerformed = true;
          _probe.RequestSequence = sequence;
        }
      }
      _events.Enqueue(new SessionEvent(
          "forced_socket_drop", sequence, connectionId, "before_ack_and_response"));
      socket.Abort();
      return true;
    }

    bool shouldRespond = false;
    lock (_gate) {
      if (_probe != null) {
        if (_probe.RequestSequence == 0) _probe.RequestSequence = sequence;
        if (_probe.RequestSequence != sequence)
          throw new InvalidDataException("replayed control request changed sequence");
        if (sequence > _lastServerSequence) _lastServerSequence = sequence;
        if (!_probe.ResponseSent) {
          _probe.ResponseSent = true;
          shouldRespond = true;
        }
      }
    }

    TryQueueEnvelope(
        "reliable_ack",
        "\"through_sequence\":" + sequence.ToString(CultureInfo.InvariantCulture));
    if (shouldRespond) {
      TryQueueEnvelope(
          "valheim_control_response",
          clientSequence =>
              "\"probe_id\":\"" + Escape(probeId)
              + "\",\"request_sequence\":"
              + sequence.ToString(CultureInfo.InvariantCulture)
              + ",\"client_sequence\":"
              + clientSequence.ToString(CultureInfo.InvariantCulture),
          out long clientSequence);
      _events.Enqueue(new SessionEvent(
          "control_response_sent", sequence, connectionId,
          "client_sequence=" + clientSequence));
    } else {
      _events.Enqueue(new SessionEvent(
          "control_request_deduplicated", sequence, connectionId, string.Empty));
    }
    return false;
  }

  void HandleControlReceiptWorker(string text) {
    string probeId = ExtractJsonString(text, "probe_id");
    string connectionId = ExtractJsonString(text, "connection_id");
    long sequence = ExtractJsonLong(text, "seq");
    long requestSequence = ExtractJsonLong(text, "request_sequence");
    long responseCount = ExtractJsonLong(text, "response_count");
    long epoch = ExtractJsonLong(text, "resume_epoch");
    SessionProbe probe;
    lock (_gate) probe = _probe;
    if (probe == null || probe.Terminal
        || !string.Equals(probe.ProbeId, probeId, StringComparison.Ordinal)
        || !string.Equals(probe.InitialConnectionId, connectionId, StringComparison.Ordinal)
        || probe.RequestSequence != requestSequence
        || responseCount != 1
        || (probe.Mode == "resume" && epoch <= probe.InitialResumeEpoch)) {
      throw new InvalidDataException("control receipt failed durable-session invariants");
    }
    lock (_gate) {
      if (sequence <= _lastServerSequence)
        throw new InvalidDataException("control receipt was not ordered after its request");
      _lastServerSequence = sequence;
    }
    TryQueueEnvelope(
        "reliable_ack",
        "\"through_sequence\":" + sequence.ToString(CultureInfo.InvariantCulture));
    _events.Enqueue(new SessionEvent(
        "probe_passed", sequence, connectionId,
        "request_sequence=" + requestSequence
        + " response_count=" + responseCount
        + " resume_epoch=" + epoch));
  }

  void HandleDirectPulseWorker(string text) {
    string runId = ExtractJsonString(text, "run_id");
    string actionId = ExtractJsonString(text, "action_id");
    string connectionId = ExtractJsonString(text, "connection_id");
    long sequence = ExtractJsonLong(text, "seq");
    DirectPulseProbe probe;
    lock (_gate) probe = _directProbe;
    if (probe == null || probe.Terminal
        || probe.Mode != "deliver"
        || !string.Equals(probe.RunId, runId, StringComparison.Ordinal)
        || !string.Equals(probe.ActionId, actionId, StringComparison.Ordinal)
        || !string.Equals(_connectionId, connectionId, StringComparison.Ordinal)
        || sequence <= 0) {
      throw new InvalidDataException("direct pulse did not match the active typed control probe");
    }
    _events.Enqueue(new SessionEvent(
        "lumberjacks_direct_pulse_received", sequence, connectionId,
        "run_id=" + runId + " action_id=" + actionId, actionId));
  }

  void HandleRoutedRpcWorker(string text) {
    string runId = ExtractJsonString(text, "run_id");
    string actionId = ExtractJsonString(text, "action_id");
    string routeId = ExtractJsonString(text, "route_id");
    string methodName = ExtractJsonString(text, "method_name");
    string parameters = ExtractJsonString(text, "parameters_base64");
    long reliableSequence = ExtractJsonLong(text, "seq");
    long messageId = ExtractJsonLong(text, "message_id");
    long senderPeerId = ExtractJsonLong(text, "sender_peer_id");
    long targetPeerId = ExtractJsonLong(text, "target_peer_id");
    long targetZdoUserId = ExtractJsonLong(text, "target_zdo_user_id");
    long targetZdoId = ExtractJsonLong(text, "target_zdo_id");
    long methodHash = ExtractJsonLong(text, "method_hash");
    if (!SafeToken(runId, 80) || !SafeToken(actionId, 80)
        || !SafeToken(routeId, 192) || !SafeToken(methodName, 96)
        || reliableSequence <= 0 || messageId == 0
        || targetZdoId is < 0 or > uint.MaxValue
        || methodHash is < int.MinValue or > int.MaxValue
        || parameters == null || parameters.Length > 48000) {
      throw new InvalidDataException("invalid routed RPC delivery");
    }
    RoutedRpcCutoverRunner.EnqueueLumberjacksInbound(
        reliableSequence, runId, actionId, routeId, messageId,
        senderPeerId, targetPeerId, targetZdoUserId, (uint)targetZdoId,
        methodName, (int)methodHash, parameters);
  }

  void HandleZdoMutationReceiptWorker(string text) {
    long sequence = ExtractJsonLong(text, "seq");
    string runId = ExtractJsonString(text, "run_id");
    string worldEpoch = ExtractJsonString(text, "world_epoch");
    long sourceSequence = ExtractJsonLong(text, "source_sequence");
    bool accepted = ExtractJsonBool(text, "accepted");
    string result = ExtractJsonString(text, "result");
    long recipients = ExtractJsonLong(text, "recipient_count");
    string logicalPeer = ExtractJsonString(text, "logical_peer_id");
    if (sequence <= 0 || sourceSequence <= 0 ||
        !SafeToken(runId, 80) || !SafeToken(worldEpoch, 96) ||
        !SafeToken(logicalPeer, 80) ||
        !string.Equals(logicalPeer, _logicalPeerId, StringComparison.Ordinal))
      throw new InvalidDataException("invalid canonical ZDO mutation receipt");
    ZdoJournalCutoverRunner.EnqueueCanonicalMutationReceipt(
        sequence, runId, worldEpoch, sourceSequence, accepted, result, recipients);
  }

  void HandleZdoInterestReceiptWorker(string text) {
    long sequence = ExtractJsonLong(text, "seq");
    string runId = ExtractJsonString(text, "run_id");
    string worldEpoch = ExtractJsonString(text, "world_epoch");
    string logicalPeer = ExtractJsonString(text, "logical_peer_id");
    bool accepted = ExtractJsonBool(text, "accepted");
    string result = ExtractJsonString(text, "result");
    long snapshots = ExtractJsonLong(text, "snapshot_count");
    long pending = ExtractJsonLong(text, "pending");
    if (sequence <= 0 || !SafeToken(runId, 80) ||
        !SafeToken(worldEpoch, 96) || !SafeToken(logicalPeer, 80) ||
        !SafeToken(result, 80) ||
        !string.Equals(logicalPeer, _logicalPeerId, StringComparison.Ordinal))
      throw new InvalidDataException("invalid canonical ZDO interest receipt");
    ZdoJournalCutoverRunner.EnqueueCanonicalInterestReceipt(
        sequence, runId, worldEpoch, logicalPeer,
        accepted, result, snapshots, pending);
  }

  void HandleZdoInterestStatusWorker(string text) {
    long sequence = ExtractJsonLong(text, "seq");
    string runId = ExtractJsonString(text, "run_id");
    string worldEpoch = ExtractJsonString(text, "world_epoch");
    string recipientId = ExtractJsonString(text, "recipient_id");
    long zoneEpoch = ExtractJsonLong(text, "zone_epoch");
    long zoneX = ExtractJsonLong(text, "zone_x");
    long zoneY = ExtractJsonLong(text, "zone_y");
    long radiusZones = ExtractJsonLong(text, "radius_zones");
    long interested = ExtractJsonLong(text, "interested_recipients");
    long pending = ExtractJsonLong(text, "pending");
    long durable = ExtractJsonLong(text, "durable_objects");
    if (sequence <= 0 || !SafeToken(runId, 80) ||
        !SafeToken(worldEpoch, 96) || !SafeToken(recipientId, 80) ||
        zoneEpoch < 1 || zoneX is < int.MinValue or > int.MaxValue ||
        zoneY is < int.MinValue or > int.MaxValue ||
        radiusZones is < 0 or > 32 || interested < 0)
      throw new InvalidDataException("invalid canonical ZDO interest status");
    ZdoJournalCutoverRunner.EnqueueCanonicalInterestStatus(
        sequence, runId, worldEpoch, recipientId, zoneEpoch,
        checked((int) zoneX), checked((int) zoneY),
        checked((int) radiusZones), interested, pending, durable);
  }

  void DrainEvents() {
    while (_events.TryDequeue(out SessionEvent item)) {
      switch (item.Kind) {
        case "session_started":
          SetState("connected", true, _udp != null, string.Empty);
          lock (_gate) {
            if (_probe != null && !_probe.Terminal &&
                _probe.Mode == "external_resume" &&
                _probe.DropPerformed &&
                (_resumeEpoch > _probe.InitialResumeEpoch ||
                 (!string.Equals(
                     _connectionId, _probe.InitialConnectionId,
                     StringComparison.Ordinal) &&
                  string.Equals(
                      _logicalPeerId, _probe.InitialLogicalPeerId,
                      StringComparison.Ordinal)))) {
              _probe.Terminal = true;
              _probe.Success = true;
              _probe.Detail =
                  "external_gateway_recovery_observed connection_id=" +
                  _connectionId + " resume_epoch=" + _resumeEpoch +
                  " logical_peer_id=" + _logicalPeerId;
            }
          }
          break;
        case "socket_closed":
          SetState("reconnecting", false, false, string.Empty);
          lock (_gate) {
            if (_probe != null && !_probe.Terminal &&
                _probe.Mode == "external_resume")
              _probe.DropPerformed = true;
          }
          break;
        case "connection_error":
          SetState("reconnecting", false, false, item.Detail);
          break;
        case "probe_passed":
          lock (_gate) {
            if (_probe != null) {
              _probe.Terminal = true;
              _probe.Success = true;
              _probe.Detail = item.Detail;
            }
          }
          break;
        case "lumberjacks_direct_pulse_received":
          lock (_gate) {
            if (_directProbe != null && !_directProbe.Terminal) {
              _directProbe.LumberjacksReceived++;
              _directProbe.Terminal = true;
              _directProbe.Success = _directProbe.Mode == "deliver";
              _directProbe.Detail =
                  "lumberjacks_direct_pulse_applied sequence=" + item.Sequence;
              if (item.Sequence > _lastServerSequence)
                _lastServerSequence = item.Sequence;
            }
          }
          // The worker only banks the frame. Acknowledge reliable delivery after the
          // selected typed handler has run on Unity's main thread.
          TryQueueEnvelope(
              "reliable_ack",
              "\"through_sequence\":"
              + item.Sequence.ToString(CultureInfo.InvariantCulture));
          break;
        case "native_direct_pulse_received":
          lock (_gate) {
            if (_directProbe != null && !_directProbe.Terminal) {
              _directProbe.NativeReceived++;
              _directProbe.Terminal = true;
              _directProbe.Success = false;
              _directProbe.Detail = "unexpected_native_direct_pulse";
            }
          }
          break;
      }
      string actionId = !string.IsNullOrEmpty(item.ActionId)
          ? item.ActionId
          : (_probe != null && !_probe.Terminal
              ? _probe.ActionId
              : (_directProbe != null && !_directProbe.Terminal
                  ? _directProbe.ActionId
                  : string.Empty));
      ComfyNetworkSense.LogInfo(WriteReceipt(item.Kind, actionId,
          "sequence=" + item.Sequence + " connection_id=" + item.ConnectionId
          + (string.IsNullOrEmpty(item.Detail) ? string.Empty : " " + item.Detail)));
    }
  }

  void EvaluateProbeDeadline(float now) {
    string receiptLog;
    lock (_gate) {
      if (_probe == null || _probe.Terminal || now <= _probe.DeadlineAt) return;
      _probe.Terminal = true;
      if (_probe.Mode == "withhold_receipt" && _probe.ResponseSent) {
        _probe.Success = true;
        _probe.Detail = "bounded_receipt_timeout_no_native_fallback";
        receiptLog = WriteReceipt("expected_timeout", _probe.ActionId, _probe.Detail);
      } else {
        _probe.Success = false;
        _probe.Detail = "probe_deadline_exceeded";
        receiptLog = WriteReceipt("probe_failed", _probe.ActionId, _probe.Detail);
      }
    }
    ComfyNetworkSense.LogInfo(receiptLog);
  }

  void EvaluateDirectProbeDeadline(float now) {
    string receiptLog;
    lock (_gate) {
      if (_directProbe == null || _directProbe.Terminal || now <= _directProbe.DeadlineAt)
        return;
      _directProbe.Terminal = true;
      if (_directProbe.Mode == "withhold"
          && _directProbe.NativeReceived == 0
          && _directProbe.LumberjacksReceived == 0) {
        _directProbe.Success = true;
        _directProbe.Detail = "direct_pulse_stale_no_native_fallback";
        receiptLog = WriteReceipt(
            "direct_pulse_expected_stale", _directProbe.ActionId, _directProbe.Detail);
      } else {
        _directProbe.Success = false;
        _directProbe.Detail = "direct_pulse_deadline_exceeded";
        receiptLog = WriteReceipt(
            "direct_pulse_failed", _directProbe.ActionId, _directProbe.Detail);
      }
    }
    ComfyNetworkSense.LogInfo(receiptLog);
  }

  bool TryQueue(string frame) => TryQueue(frame, force: false);

  // MaxOutboundFrames is a BULK-DATA cap, not a control-plane cap. Ownership lease
  // frames are rare, small, and load-bearing: full40's reissue grant was rejected at
  // queue_depth=256 while a post-resume interest re-publish flooded the queue
  // (sequences 877->2639 in ~3s), starving the holder probe to its deadline — a
  // priority inversion between zone content and the lease control plane. Control
  // frames therefore always enqueue; the transient overshoot is bounded by the
  // probes' own attempt budgets. Ordering stays intact because there is one queue.
  bool TryQueue(string frame, bool force) {
    int depth = Interlocked.Increment(ref _outboundCount);
    if (!force && depth > MaxOutboundFrames) {
      Interlocked.Decrement(ref _outboundCount);
      return false;
    }
    _outbound.Enqueue(frame);
    return true;
  }

  bool TryQueueSemantic(string type, string payloadFields) {
    if (type is not (
            "valheim_zdo_mutation" or
            "valheim_logical_peer_control" or
            "valheim_zdo_interest" or
            "valheim_zdo_ack" or
            "valheim_ownership_lease_request" or
            "valheim_ownership_lease_issue" or
            "valheim_ownership_action" or
            "valheim_ownership_action_result" or
            "valheim_world_descriptor_publish" or
            "valheim_world_descriptor_request" or
            "valheim_zone_membership_enter" or
            "valheim_zone_snapshot_publish" or
            "valheim_zone_snapshot_ack" or
            "valheim_zone_membership_leave" or
            "valheim_motion_resync_publish") ||
        payloadFields == null || payloadFields.Length > 6 * 1024 * 1024)
      return false;
    lock (_gate) {
      if (!_webSocketConnected || string.IsNullOrEmpty(_logicalPeerId))
        return false;
    }
    return TryQueueEnvelope(type, payloadFields);
  }

  bool TryQueueEnvelope(string type, string payloadFields) =>
      TryQueueEnvelope(type, _ => payloadFields, out _);

  bool TryQueueEnvelope(
      string type, Func<long, string> payloadFactory, out long sequence) {
    // The _outboundGate critical section must never acquire _gate: the main
    // thread enters here holding _gate (BeginProbe -> TryQueueEnvelope) while
    // the session worker enters without it, and a nested _gate acquisition
    // below deadlocked both threads the instant a herd reconnect lined them
    // up (full29, 14:26:44.94). ConnectionId reads _gate, so snapshot it
    // before taking _outboundGate; the sequence counter is lock-free.
    string connectionId = ConnectionId;
    lock (_outboundGate) {
      sequence = NextClientSequence();
      bool queued = TryQueue(
          BuildEnvelope(type, sequence, payloadFactory(sequence)),
          IsOwnershipType(type));
      if (IsOwnershipType(type)) {
        _events.Enqueue(new SessionEvent(
            queued ? "ownership_frame_queued" : "ownership_frame_queue_rejected",
            sequence,
            connectionId,
            "type=" + type
            + " queue_depth=" + Volatile.Read(ref _outboundCount)
                .ToString(CultureInfo.InvariantCulture)));
      }
      return queued;
    }
  }

  static bool IsOwnershipType(string type) =>
      type is
          "valheim_ownership_lease_request" or
          "valheim_ownership_lease_issue" or
          "valheim_ownership_action" or
          "valheim_ownership_action_result";

  void ClearOutbound() {
    while (_outbound.TryDequeue(out _))
      Interlocked.Decrement(ref _outboundCount);
    while (_motionOutbound.TryDequeue(out _))
      Interlocked.Decrement(ref _motionOutboundCount);
  }

  long NextClientSequence() => Interlocked.Increment(ref _nextClientSequence);

  bool ShouldRun() {
    if (PluginConfig.LumberjacksGameSessionEnabled?.Value != true) return false;
    if (ZNet.instance == null) return false;
    if (ZNet.instance.IsServer())
      return (PluginConfig.RoutedRpcCutoverEnabled?.Value == true ||
              (PluginConfig.ZdoJournalCutoverEnabled?.Value == true &&
               PluginConfig.ZdoJournalCanonicalSessionEnabled?.Value == true) ||
               PluginConfig.OwnershipLeaseCutoverEnabled?.Value == true ||
               PluginConfig.WorldZoneCutoverEnabled?.Value == true ||
               PluginConfig.MotionAuthorityCutoverEnabled?.Value == true ||
               PluginConfig.LogicalPeerCutoverEnabled?.Value == true)
          && ZNet.GetUID() != 0;
    if (Player.m_localPlayer == null &&
        !(PluginConfig.WorldZoneCutoverEnabled?.Value == true ||
          NativeAutotestRequest.ActiveWorldZoneCutover)) return false;
    return !string.IsNullOrWhiteSpace(PluginConfig.LumberjacksEnrollmentId.Value)
        && !string.IsNullOrWhiteSpace(PluginConfig.LumberjacksClientAccessKey.Value);
  }

  string GatewayUrl(string resumeToken) {
    string value = NormalizeGatewayUrl();
    value += (value.Contains("?") ? "&" : "?")
        + "valheim_role=" + Uri.EscapeDataString(_localRole);
    if (_localRole == "client") {
      string client = NativeAutotestRequest.ActiveClient;
      if (SafeToken(client, 48))
        value += "&valheim_client=" + Uri.EscapeDataString(client);
      string character = NativeAutotestRequest.ActiveCharacter;
      if (string.IsNullOrWhiteSpace(character)) {
        try { character = Player.m_localPlayer?.GetPlayerName(); } catch { }
      }
      if (string.IsNullOrWhiteSpace(character))
        character = PluginConfig.AutoJoinCharacterName?.Value;
      if (!string.IsNullOrWhiteSpace(character))
        value += "&valheim_character=" + Uri.EscapeDataString(character.Trim());
      if (NativeAutotestRequest.ActiveColdJoinFault == "invalid_enrollment")
        value += "&c7_require_enrollment=true";
    }
    if (!string.IsNullOrEmpty(resumeToken))
      value += "&resume=" + Uri.EscapeDataString(resumeToken);
    return value;
  }

  static string NormalizeGatewayUrl() {
    string value = NativeAutotestRequest.ActiveGatewayUrl;
    if (string.IsNullOrWhiteSpace(value))
      value = Environment.GetEnvironmentVariable("COMFY_LUMBERJACKS_GATEWAY_URL");
    if (string.IsNullOrWhiteSpace(value)) value = PluginConfig.LumberjacksGatewayUrl.Value;
    value = (value ?? "ws://127.0.0.1:4000").Trim();
    if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) {
      return value.Replace("https://", "wss://");
    } else if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) {
      return value.Replace("http://", "ws://");
    }
    return value;
  }

  static string BuildEnvelope(string type, long sequence, string payloadFields) =>
      "{\"version\":1,\"type\":\"" + Escape(type)
      + "\",\"seq\":" + sequence.ToString(CultureInfo.InvariantCulture)
      + ",\"timestamp\":\"" + DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture)
      + "\",\"payload\":{" + payloadFields + "}}";

  // Snapshot-then-log: callers holding _gate must finish this (field reads plus the
  // already-async _writer.Write) while locked, then call ComfyNetworkSense.LogInfo on the
  // returned line only after releasing _gate. The shared BepInEx log sink can stall; doing
  // that call under _gate would wedge every _gate-guarded getter on any thread until it
  // returns.
  string WriteReceipt(string state, string actionId, string detail) {
    string runId = EvidenceRunId();
    Dictionary<string, object> row = new() {
        ["schema_version"] = 1,
        ["timestamp_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        ["state"] = state,
        ["run_id"] = runId,
        ["client"] = _localRole == "server"
            ? "server" : NativeAutotestRequest.ActiveClient,
        ["action_id"] = actionId ?? string.Empty,
        ["connection_id"] = _connectionId,
        ["logical_peer_id"] = _logicalPeerId,
        ["resume_epoch"] = _resumeEpoch,
        ["detail"] = detail ?? string.Empty
    };
    _writer.Write(ReceiptFileName, row);
    return "LUMBERJACKS_SESSION state=" + SafeMarker(state)
        + " run_id=" + SafeMarker(runId)
        + " client=" + SafeMarker(NativeAutotestRequest.ActiveClient)
        + " action=" + SafeMarker(actionId)
        + " detail=" + SafeMarker(detail);
  }

  string EvidenceRunId() =>
      _localRole == "server"
          ? PluginConfig.NativeNetworkEvidenceRunId?.Value ?? string.Empty
          : NativeAutotestRequest.ActiveRunId;

  void SetState(string state, bool websocket, bool udp, string error) {
    lock (_gate) {
      _state = state;
      _webSocketConnected = websocket;
      _udpReady = udp;
      _lastError = error ?? string.Empty;
    }
  }

  static async Task<ReceivedFrame> ReceiveFrame(
      ClientWebSocket socket, byte[] buffer, CancellationToken token) {
    using MemoryStream stream = new();
    WebSocketReceiveResult result;
    do {
      result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token)
          .ConfigureAwait(false);
      if (result.MessageType == WebSocketMessageType.Close) return null;
      stream.Write(buffer, 0, result.Count);
    } while (!result.EndOfMessage);
    return new ReceivedFrame(result.MessageType, stream.ToArray());
  }

  static string ExtractJsonString(string json, string name) {
    Match match = Regex.Match(
        json ?? string.Empty,
        "\"" + Regex.Escape(name) + "\"\\s*:\\s*\"(?<value>(?:\\\\.|[^\"])*)\"",
        RegexOptions.IgnoreCase);
    return match.Success ? DecodeJsonString(match.Groups["value"].Value) : string.Empty;
  }

  static string DecodeJsonString(string value) {
    if (string.IsNullOrEmpty(value) || value.IndexOf('\\') < 0) return value;

    StringBuilder decoded = new(value.Length);
    for (int index = 0; index < value.Length; index++) {
      char character = value[index];
      if (character != '\\') {
        decoded.Append(character);
        continue;
      }

      if (++index >= value.Length)
        throw new InvalidDataException("JSON string ended inside an escape sequence");

      switch (value[index]) {
        case '"': decoded.Append('"'); break;
        case '\\': decoded.Append('\\'); break;
        case '/': decoded.Append('/'); break;
        case 'b': decoded.Append('\b'); break;
        case 'f': decoded.Append('\f'); break;
        case 'n': decoded.Append('\n'); break;
        case 'r': decoded.Append('\r'); break;
        case 't': decoded.Append('\t'); break;
        case 'u':
          if (index + 4 >= value.Length
              || !ushort.TryParse(
                  value.Substring(index + 1, 4),
                  NumberStyles.AllowHexSpecifier,
                  CultureInfo.InvariantCulture,
                  out ushort codeUnit)) {
            throw new InvalidDataException("JSON string contains an invalid Unicode escape");
          }
          decoded.Append((char)codeUnit);
          index += 4;
          break;
        default:
          throw new InvalidDataException("JSON string contains an unsupported escape");
      }
    }
    return decoded.ToString();
  }

  static long ExtractJsonLong(string json, string name) {
    Match match = Regex.Match(
        json ?? string.Empty,
        "\"" + Regex.Escape(name) + "\"\\s*:\\s*(?<value>-?[0-9]+)",
        RegexOptions.IgnoreCase);
    return match.Success
        && long.TryParse(
            match.Groups["value"].Value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out long value)
        ? value
        : 0;
  }

  static bool ExtractJsonBool(string json, string name) {
    Match match = Regex.Match(
        json ?? string.Empty,
        "\"" + Regex.Escape(name) + "\"\\s*:\\s*(?<value>true|false)",
        RegexOptions.IgnoreCase);
    return match.Success
        && string.Equals(match.Groups["value"].Value, "true", StringComparison.OrdinalIgnoreCase);
  }

  static bool SafeToken(string value, int maxLength) {
    if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength) return false;
    foreach (char character in value)
      if (!char.IsLetterOrDigit(character)
          && character is not '-' and not '_' and not '.')
        return false;
    return true;
  }

  static string Escape(string value) =>
      (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"")
          .Replace("\r", "_").Replace("\n", "_");

  static string SafeMarker(string value) =>
      string.IsNullOrWhiteSpace(value)
          ? "none"
          : value.Trim().Replace(' ', '_').Replace('\t', '_')
              .Replace('\r', '_').Replace('\n', '_');

  public void Dispose() {
    if (_disposed) return;
    _disposed = true;
    Stop();
    Interlocked.CompareExchange(ref _active, null, this);
    _writer.Dispose();
  }

  sealed class SessionProbe {
    public string ActionId;
    public string ProbeId;
    public string RunId;
    public string Mode;
    public string InitialConnectionId;
    public string InitialLogicalPeerId;
    public long InitialResumeEpoch;
    public long RequestSequence;
    public float StartedAt;
    public float DeadlineAt;
    public bool DropPerformed;
    public bool ResponseSent;
    public bool Terminal;
    public bool Success;
    public string Detail = string.Empty;
  }

  sealed class DirectPulseProbe {
    public string ActionId;
    public string RunId;
    public string Mode;
    public float DeadlineAt;
    public int NativeReceived;
    public int LumberjacksReceived;
    public bool Terminal;
    public bool Success;
    public string Detail = string.Empty;
  }

  sealed class SessionEvent {
    public readonly string Kind;
    public readonly long Sequence;
    public readonly string ConnectionId;
    public readonly string Detail;
    public readonly string ActionId;
    public SessionEvent(
        string kind,
        long sequence,
        string connectionId,
        string detail,
        string actionId = "") {
      Kind = kind;
      Sequence = sequence;
      ConnectionId = connectionId;
      Detail = detail;
      ActionId = actionId;
    }
  }

  sealed class ReceivedFrame {
    public readonly WebSocketMessageType Type;
    public readonly byte[] Data;
    public ReceivedFrame(WebSocketMessageType type, byte[] data) {
      Type = type;
      Data = data;
    }
  }

  sealed class MotionOutbound {
    public readonly ushort Sequence;
    public readonly ValheimMotionSnapshot Snapshot;
    public MotionOutbound(ushort sequence, ValheimMotionSnapshot snapshot) {
      Sequence = sequence;
      Snapshot = snapshot;
    }
  }
}
