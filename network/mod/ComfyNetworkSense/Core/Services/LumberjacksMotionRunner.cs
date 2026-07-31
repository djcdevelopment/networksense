namespace ComfyNetworkSense;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
/// Client-only Valheim movement adapter for Lumberjacks Channel 2. It always observes when armed;
/// applying snapshots is a separate alpha switch, and stale data yields immediately to native
/// Valheim. This slice deliberately interpolates toward observed positions without velocity
/// extrapolation so the A/B does not reproduce the behavior it is intended to measure.
/// </summary>
public sealed class LumberjacksMotionRunner : IDisposable {
  const int ConnectTimeoutMs = 5000;
  const int ReceiveBufferBytes = 4096;
  const float InterframeDisplacementThresholdMeters = 0.05f;
  const string AuthorityReceiptFileName = "motion-authority-cutover.jsonl";
  const int GapFrameCount = 20;
  static LumberjacksMotionRunner _active;

  readonly ConcurrentQueue<ReceivedMotion> _received = new();
  readonly ConcurrentQueue<CanonicalFrame> _canonicalFrames = new();
  readonly Dictionary<string, RemoteMotion> _remote = new(StringComparer.Ordinal);
  readonly HashSet<string> _appliedResyncKeys = new(StringComparer.Ordinal);
  readonly Dictionary<ZDOID, GameObject> _playerInstances = new();
  readonly HashSet<string> _drainedKeys = new(StringComparer.Ordinal);
  readonly HashSet<string> _remoteDiscoveryWritten = new(StringComparer.Ordinal);
  readonly HashSet<string> _missingInstanceWritten = new(StringComparer.Ordinal);
  readonly HashSet<string> _localInstanceWritten = new(StringComparer.Ordinal);
  readonly object _outboundLock = new();
  readonly object _statusLock = new();
  readonly LumberjacksGameSessionRunner _gameSession;
  readonly TelemetryLogWriter _writer = new();

  CancellationTokenSource _cts;
  Task _connectionTask;
  ClientWebSocket _socket;
  UdpClient _udp;
  OutboundMotion _outbound;
  int _outboundGeneration;
  int _lastSentGeneration;
  float _nextConnectAt;
  float _nextSampleAt;
  float _nextPlayerIndexAt;
  ushort _sequence;
  string _state = "idle";
  string _lastError = string.Empty;
  bool _webSocketConnected;
  bool _udpReady;
  long _sentUdp;
  long _sentWebSocket;
  long _receivedUdp;
  long _receivedWebSocket;
  long _applied;
  long _staleFallbacks;
  long _unknownZdos;
  long _zdoLookupAttempts;
  long _directLookupHits;
  long _zdoObjectLookupHits;
  long _playerIndexLookupHits;
  long _playerIndexRebuilds;
  long _lastReceiveTimestamp;
  long _receiveIntervalCount;
  long _receiveIntervalTotalUs;
  long _receiveIntervalMaxUs;
  long _drainBatches;
  long _drainedSamples;
  long _drainBatchMax;
  long _coalescedInDrain;
  long _staleSequenceDrops;
  long _lateUpdateCalls;
  long _freshRemoteVisits;
  long _staleRemoteVisits;
  long _freshVisitAgeSamples;
  long _bindMeasurementCount;
  long _bindTotalUs;
  long _bindMaxUs;
  long _renderMeasurementCount;
  long _renderTotalUs;
  long _renderMaxUs;
  long _targetErrorSamples;
  long _targetErrorTotalMm;
  long _targetErrorMaxMm;
  long _freshVisitAgeTotalMs;
  long _freshVisitAgeMaxMs;
  long _correctionGuardRejections;
  long _interframeDisplacementChecks;
  long _interframeDisplacementOverThreshold;
  long _interframeDisplacementTotalMm;
  long _interframeDisplacementMaxMm;
  long _canonicalFramesQueued;
  long _canonicalMotionSent;
  long _nativeWriterSuppressed;
  long _nativePositionMasked;
  long _authorityHolds;
  long _authorityGaps;
  long _reliableResyncQueued;
  long _reliableResyncApplied;
  long _reliableResyncReceipts;
  long _authorityFailures;
  MotionProbe _probe;
  string _lastResyncActionId = string.Empty;
  string _rendezvousActionId = string.Empty;
  bool _rendezvousMoved;
  bool _rendezvousCompleteWritten;
  bool _localMotionIdentityWritten;
  bool _disposed;

  public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;
  public bool WebSocketConnected { get { lock (_statusLock) return _webSocketConnected; } }
  public bool UdpReady { get { lock (_statusLock) return _udpReady; } }
  public string State { get { lock (_statusLock) return _state; } }
  public string LastError { get { lock (_statusLock) return _lastError; } }
  public long SentUdp => Interlocked.Read(ref _sentUdp);
  public long SentWebSocket => Interlocked.Read(ref _sentWebSocket);
  public long ReceivedUdp => Interlocked.Read(ref _receivedUdp);
  public long ReceivedWebSocket => Interlocked.Read(ref _receivedWebSocket);
  public long Applied => Interlocked.Read(ref _applied);

  public LumberjacksMotionRunner(LumberjacksGameSessionRunner gameSession) {
    _gameSession = gameSession;
    LumberjacksMotionRunner previous = Interlocked.Exchange(ref _active, this);
    previous?.Dispose();
  }

  public void Update(float now) {
    if (!ShouldRun()) {
      if (IsRunning) Stop();
      return;
    }

    DrainCanonicalFrames();
    if (CanonicalSessionEnabled()) {
      if (IsRunning) Stop();
      SetState(
          _gameSession?.WebSocketConnected == true
              ? "canonical_session"
              : "canonical_session_waiting",
          _gameSession?.WebSocketConnected == true,
          _gameSession?.UdpReady == true,
          _gameSession?.LastError ?? string.Empty);
      DrainReceived(now);
      UpdateProbeDrive(now);
      CaptureLocalMotion(now);
      EvaluateProbe(now);
      return;
    }

    if (!IsRunning && now >= _nextConnectAt) Start();
    DrainReceived(now);
    UpdateProbeDrive(now);
    CaptureLocalMotion(now);
    EvaluateProbe(now);
  }

  public void LateUpdate(float deltaTime) {
    if ((!AlphaTransportSwitches.MotionApplyEnabled && !AuthorityEnabled()) ||
        IsDedicatedServer()) return;
    bool measurePhases = DetailedTelemetryEnabled();
    long renderStarted = measurePhases ? Stopwatch.GetTimestamp() : 0;
    _lateUpdateCalls++;
    float now = Time.unscaledTime;
    float freshSeconds = Mathf.Clamp(PluginConfig.LumberjacksMotionFreshSeconds.Value, 0.1f, 2.0f);
    float alpha = 1.0f - Mathf.Exp(-Mathf.Clamp(PluginConfig.LumberjacksMotionSmoothing.Value, 1.0f, 60.0f)
        * Mathf.Max(0.001f, deltaTime));

    try {
      foreach (RemoteMotion remote in _remote.Values) {
        float sampleAgeSeconds = now - remote.ArrivedAt;
        if (sampleAgeSeconds > freshSeconds) {
          _staleRemoteVisits++;
          if (AuthorityEnabled()) {
            HoldRemote(remote, "freshness_expired");
          } else {
            remote.HasAppliedPosition = false;
          }
          if (!remote.FallbackNoted) {
            remote.FallbackNoted = true;
            if (AuthorityEnabled()) {
              Interlocked.Increment(ref _authorityHolds);
              WriteAuthority(
                  "hold_entered", _probe?.ActionId ?? string.Empty,
                  "sequence=" + remote.Sequence
                  + " age_ms="
                  + SecondsToMilliseconds(sampleAgeSeconds)
                      .ToString(CultureInfo.InvariantCulture)
                  + " native_fallback=false");
            } else {
              Interlocked.Increment(ref _staleFallbacks);
            }
          }
          continue;
        }

        _freshRemoteVisits++;
        if (measurePhases) {
          _freshVisitAgeSamples++;
          RecordMainThreadTotalAndMax(
              ref _freshVisitAgeTotalMs,
              ref _freshVisitAgeMaxMs,
              SecondsToMilliseconds(sampleAgeSeconds));
        }

        ZDOID zdoId = new(remote.Snapshot.ZdoUserId, remote.Snapshot.ZdoId);
        Interlocked.Increment(ref _zdoLookupAttempts);
        long bindStarted = measurePhases ? Stopwatch.GetTimestamp() : 0;
        GameObject instance = ResolveInstance(zdoId, now);
        if (measurePhases) {
          _bindMeasurementCount++;
          RecordMainThreadTotalAndMax(
              ref _bindTotalUs,
              ref _bindMaxUs,
              ElapsedMicroseconds(bindStarted));
        }
        if (instance == null) {
          remote.HasAppliedPosition = false;
          Interlocked.Increment(ref _unknownZdos);
          string key = zdoId.UserID + ":" + zdoId.ID;
          if (_missingInstanceWritten.Add(key))
            WriteAuthority(
                "remote_instance_missing", _probe?.ActionId ?? string.Empty,
                "uid=" + key + " known_player_uids=" + KnownPlayerIds());
          continue;
        }
        if (Player.m_localPlayer != null && instance == Player.m_localPlayer.gameObject) {
          remote.HasAppliedPosition = false;
          string key = zdoId.UserID + ":" + zdoId.ID;
          if (_localInstanceWritten.Add(key))
            WriteAuthority(
                "remote_resolved_to_local", _probe?.ActionId ?? string.Empty,
                "uid=" + key);
          continue;
        }

        Vector3 current = instance.transform.position;
        if (measurePhases && remote.HasAppliedPosition) {
          long displacementMm = MetersToMillimeters(Vector3.Distance(current, remote.LastAppliedPosition));
          _interframeDisplacementChecks++;
          _interframeDisplacementTotalMm += displacementMm;
          UpdateMainThreadMax(ref _interframeDisplacementMaxMm, displacementMm);
          if (displacementMm >= MetersToMillimeters(InterframeDisplacementThresholdMeters))
            _interframeDisplacementOverThreshold++;
        }

        Vector3 target = new(remote.Snapshot.X, remote.Snapshot.Y, remote.Snapshot.Z);
        float targetError = Vector3.Distance(current, target);
        if (measurePhases) {
          _targetErrorSamples++;
          RecordMainThreadTotalAndMax(
              ref _targetErrorTotalMm,
              ref _targetErrorMaxMm,
              MetersToMillimeters(targetError));
        }

        // Legacy observe/apply mode yields to native for an implausible correction. C6 authority
        // mode fails closed and holds; hidden native correction is never re-enabled.
        if (targetError > 30.0f) {
          if (AuthorityEnabled()) {
            HoldRemote(remote, "implausible_target");
            Interlocked.Increment(ref _authorityFailures);
            WriteAuthority(
                "target_rejected", _probe?.ActionId ?? string.Empty,
                "sequence=" + remote.Sequence
                + " target_error_mm="
                + MetersToMillimeters(targetError)
                    .ToString(CultureInfo.InvariantCulture)
                + " native_fallback=false");
          } else {
            remote.HasAppliedPosition = false;
          }
          _correctionGuardRejections++;
          continue;
        }

        Vector3 appliedPosition = remote.HardResync
            ? target
            : Vector3.Lerp(current, target, alpha);
        instance.transform.position = appliedPosition;
        Quaternion rotation = Quaternion.Euler(0.0f, remote.Snapshot.Yaw, 0.0f);
        instance.transform.rotation = remote.HardResync
            ? rotation
            : Quaternion.Slerp(instance.transform.rotation, rotation, alpha);
        Rigidbody body = instance.GetComponent<Rigidbody>();
        if (body != null) {
          body.linearVelocity = new Vector3(
              remote.Snapshot.VelocityX,
              remote.Snapshot.VelocityY,
              remote.Snapshot.VelocityZ);
        }
        remote.LastAppliedPosition = appliedPosition;
        remote.HasAppliedPosition = true;
        Interlocked.Increment(ref _applied);
        if (remote.HardResync && !remote.ReliableAcknowledged) {
          remote.ReliableAcknowledged = true;
          Interlocked.Increment(ref _reliableResyncApplied);
          _lastResyncActionId = remote.ActionId;
          bool acknowledged =
              _gameSession?.QueueReliableAck(remote.ReliableSequence) == true;
          WriteAuthority(
              "reliable_resync_applied", remote.ActionId,
              "motion_sequence=" + remote.Sequence
              + " reliable_sequence=" + remote.ReliableSequence
              + " gap_start=" + remote.GapStart
              + " gap_end=" + remote.GapEnd
              + " ack_queued=" + (acknowledged ? "true" : "false"));
        }
      }
    } finally {
      if (measurePhases) {
        _renderMeasurementCount++;
        RecordMainThreadTotalAndMax(
            ref _renderTotalUs,
            ref _renderMaxUs,
            ElapsedMicroseconds(renderStarted));
      }
    }
  }

  public IDictionary<string, object> Snapshot() {
    lock (_statusLock) {
      return new Dictionary<string, object> {
          ["state"] = _state,
          ["websocket_connected"] = _webSocketConnected,
          ["udp_ready"] = _udpReady,
          ["apply_enabled"] = AlphaTransportSwitches.MotionApplyEnabled,
          ["sent_udp"] = Interlocked.Read(ref _sentUdp),
          ["sent_websocket"] = Interlocked.Read(ref _sentWebSocket),
          ["received_udp"] = Interlocked.Read(ref _receivedUdp),
          ["received_websocket"] = Interlocked.Read(ref _receivedWebSocket),
          ["applied"] = Interlocked.Read(ref _applied),
          ["stale_fallbacks"] = Interlocked.Read(ref _staleFallbacks),
          ["unknown_zdos"] = Interlocked.Read(ref _unknownZdos),
          ["zdo_lookup_attempts"] = Interlocked.Read(ref _zdoLookupAttempts),
          ["direct_lookup_hits"] = Interlocked.Read(ref _directLookupHits),
          ["zdo_object_lookup_hits"] = Interlocked.Read(ref _zdoObjectLookupHits),
          ["player_index_lookup_hits"] = Interlocked.Read(ref _playerIndexLookupHits),
          ["player_index_rebuilds"] = Interlocked.Read(ref _playerIndexRebuilds),
          ["player_index_size"] = _playerInstances.Count,
          ["remote_entities"] = _remote.Count,
          ["phase_measurements_enabled"] = DetailedTelemetryEnabled(),
          ["receive_interval_count"] = Interlocked.Read(ref _receiveIntervalCount),
          ["receive_interval_total_us"] = Interlocked.Read(ref _receiveIntervalTotalUs),
          ["receive_interval_max_us"] = Interlocked.Read(ref _receiveIntervalMaxUs),
          ["drain_batches"] = _drainBatches,
          ["drained_samples"] = _drainedSamples,
          ["drain_batch_max"] = _drainBatchMax,
          ["coalesced_in_drain"] = _coalescedInDrain,
          ["stale_sequence_drops"] = _staleSequenceDrops,
          ["late_update_calls"] = _lateUpdateCalls,
          ["fresh_remote_visits"] = _freshRemoteVisits,
          ["stale_remote_visits"] = _staleRemoteVisits,
          ["fresh_visit_age_samples"] = _freshVisitAgeSamples,
          ["bind_measurement_count"] = _bindMeasurementCount,
          ["bind_total_us"] = _bindTotalUs,
          ["bind_max_us"] = _bindMaxUs,
          ["render_measurement_count"] = _renderMeasurementCount,
          ["render_total_us"] = _renderTotalUs,
          ["render_max_us"] = _renderMaxUs,
          ["target_error_samples"] = _targetErrorSamples,
          ["target_error_total_mm"] = _targetErrorTotalMm,
          ["target_error_max_mm"] = _targetErrorMaxMm,
          ["fresh_visit_age_total_ms"] = _freshVisitAgeTotalMs,
          ["fresh_visit_age_max_ms"] = _freshVisitAgeMaxMs,
          ["correction_guard_rejections"] = _correctionGuardRejections,
          ["interframe_displacement_checks"] = _interframeDisplacementChecks,
          ["interframe_displacement_over_50mm"] = _interframeDisplacementOverThreshold,
          ["interframe_displacement_total_mm"] = _interframeDisplacementTotalMm,
          ["interframe_displacement_max_mm"] = _interframeDisplacementMaxMm,
          ["authority_enabled"] = AuthorityEnabled(),
          ["canonical_frames_queued"] =
              Interlocked.Read(ref _canonicalFramesQueued),
          ["canonical_motion_sent"] =
              Interlocked.Read(ref _canonicalMotionSent),
          ["native_writer_suppressed"] =
              Interlocked.Read(ref _nativeWriterSuppressed),
          ["native_position_masked"] =
              Interlocked.Read(ref _nativePositionMasked),
          ["authority_holds"] = Interlocked.Read(ref _authorityHolds),
          ["authority_gaps"] = Interlocked.Read(ref _authorityGaps),
          ["reliable_resync_queued"] =
              Interlocked.Read(ref _reliableResyncQueued),
          ["reliable_resync_applied"] =
              Interlocked.Read(ref _reliableResyncApplied),
          ["reliable_resync_receipts"] =
              Interlocked.Read(ref _reliableResyncReceipts),
          ["authority_failures"] =
              Interlocked.Read(ref _authorityFailures),
          ["probe_state"] = _probe == null
              ? "none"
              : (_probe.Terminal
                  ? (_probe.Success ? "passed" : "failed")
                  : "running"),
          ["probe_detail"] = _probe?.Detail ?? string.Empty,
          ["last_error"] = _lastError
      };
    }
  }

  bool ShouldRun() {
    if ((!PluginConfig.LumberjacksMotionEnabled.Value && !AuthorityEnabled()) ||
        IsDedicatedServer()) return false;
    if (!AlphaTransportSwitches.LumberjacksWebSocketEnabled) return false;
    if (Player.m_localPlayer == null || ZNet.instance == null || ZNet.instance.IsServer()) return false;
    if (CanonicalSessionEnabled())
      return _gameSession != null;
    return !string.IsNullOrWhiteSpace(PluginConfig.LumberjacksEnrollmentId.Value)
        && !string.IsNullOrWhiteSpace(PluginConfig.LumberjacksClientAccessKey.Value);
  }

  void Start() {
    Stop();
    Interlocked.Exchange(ref _lastReceiveTimestamp, 0);
    _nextConnectAt = Time.unscaledTime + 5.0f;
    _cts = new CancellationTokenSource();
    SetState("connecting", false, false, string.Empty);
    _connectionTask = Task.Run(() => RunConnection(_cts.Token));
  }

  public void Stop() {
    try { _cts?.Cancel(); _socket?.Abort(); _udp?.Close(); } catch { }
    _cts?.Dispose();
    _cts = null;
    _socket = null;
    _udp = null;
    SetState("idle", false, false, string.Empty);
  }

  async Task RunConnection(CancellationToken token) {
    try {
      using ClientWebSocket socket = new();
      LumberjacksClientAuth.Apply(socket);
      _socket = socket;
      using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token)) {
        timeout.CancelAfter(ConnectTimeoutMs);
        await socket.ConnectAsync(new Uri(NormalizeGatewayUrl()), timeout.Token).ConfigureAwait(false);
      }
      SetState("websocket", true, false, string.Empty);

      byte[] receiveBuffer = new byte[ReceiveBufferBytes];
      Task sendTask = null;
      Task udpReceiveTask = null;
      while (!token.IsCancellationRequested && socket.State == WebSocketState.Open) {
        ReceivedFrame frame = await ReceiveFrame(socket, receiveBuffer, token).ConfigureAwait(false);
        if (frame == null) break;
        if (frame.Type == WebSocketMessageType.Binary) {
          if (ValheimMotionCodec.TryRead(frame.Data, tokenPrefixed: false, out ushort seq, out ValheimMotionSnapshot motion)) {
            EnqueueReceived(seq, motion, udp: false);
          }
          continue;
        }

        string text = Encoding.UTF8.GetString(frame.Data);
        if (!string.Equals(ExtractJsonString(text, "type"), "session_started", StringComparison.OrdinalIgnoreCase)) continue;
        string udpTokenText = ExtractJsonString(text, "udp_token");
        int udpPort = ExtractJsonInt(text, "udp_port");
        if (!ulong.TryParse(udpTokenText, out ulong udpToken) || udpPort <= 0)
          throw new InvalidDataException("session_started did not include a usable UDP binding");

        await SendText(socket, BuildJoinRegion(), token).ConfigureAwait(false);
        UdpClient udp = new();
        udp.Client.ReceiveBufferSize = 1024 * 1024;
        udp.Connect(new Uri(NormalizeGatewayUrl()).Host, udpPort);
        _udp = udp;
        SetState("observing", true, true, string.Empty);
        udpReceiveTask = RunUdpReceive(udp, token);
        sendTask = RunSendLoop(socket, udp, udpToken, token);
      }

      try { _udp?.Close(); } catch { }
      if (sendTask != null) await IgnoreCancellation(sendTask).ConfigureAwait(false);
      if (udpReceiveTask != null) await IgnoreCancellation(udpReceiveTask).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      // Normal stop/reconnect.
    } catch (Exception exception) {
      SetState("error", false, false, exception.GetType().Name + ": " + exception.Message);
    } finally {
      try { _udp?.Close(); } catch { }
      _udp = null;
      _socket = null;
      if (!token.IsCancellationRequested) _cts?.Cancel();
    }
  }

  async Task RunSendLoop(ClientWebSocket socket, UdpClient udp, ulong token, CancellationToken cancellationToken) {
    while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open) {
      OutboundMotion outbound = null;
      int generation;
      lock (_outboundLock) { generation = _outboundGeneration; if (generation != _lastSentGeneration) outbound = _outbound; }
      if (outbound == null) { await Task.Delay(5, cancellationToken).ConfigureAwait(false); continue; }

      if (AlphaTransportSwitches.LumberjacksUdpEnabled) {
        byte[] packet = ValheimMotionCodec.BuildUdpPacket(token, outbound.Sequence, outbound.Snapshot);
        await udp.SendAsync(packet, packet.Length).ConfigureAwait(false);
        Interlocked.Increment(ref _sentUdp);
      } else {
        byte[] frame = ValheimMotionCodec.BuildWebSocketFrame(outbound.Sequence, outbound.Snapshot);
        await socket.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Binary, true, cancellationToken)
            .ConfigureAwait(false);
        Interlocked.Increment(ref _sentWebSocket);
      }
      lock (_outboundLock) { if (_lastSentGeneration < generation) _lastSentGeneration = generation; }
    }
  }

  async Task RunUdpReceive(UdpClient udp, CancellationToken token) {
    using (token.Register(() => udp.Close())) {
      while (!token.IsCancellationRequested) {
        try {
          UdpReceiveResult result = await udp.ReceiveAsync().ConfigureAwait(false);
          if (ValheimMotionCodec.TryRead(result.Buffer, tokenPrefixed: true, out ushort seq, out ValheimMotionSnapshot motion)) {
            EnqueueReceived(seq, motion, udp: true);
          }
        } catch (ObjectDisposedException) { break; }
        catch (SocketException) { if (token.IsCancellationRequested) break; }
      }
    }
  }

  void CaptureLocalMotion(float now) {
    bool connected = CanonicalSessionEnabled()
        ? _gameSession?.WebSocketConnected == true
        : WebSocketConnected;
    if (!connected || now < _nextSampleAt) return;
    _nextSampleAt = now + 1.0f / Mathf.Clamp(PluginConfig.LumberjacksMotionSendHz.Value, 5.0f, 30.0f);
    Player player = Player.m_localPlayer;
    ZNetView view = player?.GetComponent<ZNetView>();
    ZDO zdo = view?.GetZDO();
    if (player == null || zdo == null) return;

    Vector3 position = player.transform.position;
    Rigidbody body = player.GetComponent<Rigidbody>();
    Vector3 velocity = body == null ? Vector3.zero : body.linearVelocity;
    ValheimMotionSnapshot snapshot = new(
        zdo.m_uid.UserID, zdo.m_uid.ID,
        position.x, position.y, position.z,
        velocity.x, velocity.y, velocity.z,
        player.transform.rotation.eulerAngles.y,
        unchecked((uint) DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    OutboundMotion outbound = new(++_sequence, snapshot);
    if (!_localMotionIdentityWritten) {
      _localMotionIdentityWritten = true;
      WriteAuthority(
          "local_motion_identity", _probe?.ActionId ?? string.Empty,
          "uid=" + snapshot.ZdoUserId + ":" + snapshot.ZdoId
          + " position=" + PositionDetail(snapshot));
    }
    if (CanonicalSessionEnabled()) {
      if (_probe != null && !_probe.Terminal &&
          _probe.Mode == "drive_gap" &&
          now >= _probe.GapStartsAt &&
          _probe.GapDropped < GapFrameCount) {
        if (_probe.GapDropped == 0)
          _probe.GapStart = outbound.Sequence;
        _probe.GapEnd = outbound.Sequence;
        _probe.GapDropped++;
        WriteAuthority(
            "motion_frame_withheld", _probe.ActionId,
            "sequence=" + outbound.Sequence
            + " withheld_index=" + _probe.GapDropped
            + " withheld_total=" + GapFrameCount);
        if (_probe.GapDropped == GapFrameCount)
          QueueReliableResync(_probe, outbound);
        return;
      }
      if (_gameSession.TryQueueMotion(
              outbound.Sequence, outbound.Snapshot, out string detail)) {
        Interlocked.Increment(ref _canonicalFramesQueued);
      } else if (_probe != null && !_probe.Terminal) {
        FailProbe(detail);
      }
      return;
    }
    lock (_outboundLock) { _outbound = outbound; _outboundGeneration++; }
  }

  void DrainReceived(float now) {
    int drained = 0;
    _drainedKeys.Clear();
    while (_received.TryDequeue(out ReceivedMotion received)) {
      drained++;
      string key = received.Snapshot.ZdoUserId + ":" + received.Snapshot.ZdoId;
      if (!_drainedKeys.Add(key)) _coalescedInDrain++;
      if (_remote.TryGetValue(key, out RemoteMotion existing) &&
          !ValheimMotionCodec.IsNewer(received.Sequence, existing.Sequence)) {
        _staleSequenceDrops++;
        continue;
      }
      if (existing != null) {
        ushort sequenceDelta =
            unchecked((ushort) (received.Sequence - existing.Sequence));
        if (sequenceDelta > 1 && sequenceDelta < 0x8000) {
          Interlocked.Increment(ref _authorityGaps);
          WriteAuthority(
              "motion_gap_observed", _probe?.ActionId ?? string.Empty,
              "previous_sequence=" + existing.Sequence
              + " current_sequence=" + received.Sequence
              + " missing=" + (sequenceDelta - 1));
        }
      }
      RemoteMotion updated = new(
          received.Sequence, received.Snapshot, now,
          received.HardResync, received.ReliableSequence,
          received.ActionId, received.GapStart, received.GapEnd);
      if (existing != null) updated.CopyPresentationState(existing);
      _remote[key] = updated;
      if (_remoteDiscoveryWritten.Add(key))
        WriteAuthority(
            "remote_motion_discovered", _probe?.ActionId ?? string.Empty,
            "uid=" + key
            + " position=" + PositionDetail(received.Snapshot)
            + " transport=" + (received.Udp ? "udp" : "websocket"));
    }
    if (drained > 0) {
      _drainBatches++;
      _drainedSamples += drained;
      UpdateMainThreadMax(ref _drainBatchMax, drained);
    }
  }

  void EnqueueReceived(ushort sequence, ValheimMotionSnapshot motion, bool udp) {
    _received.Enqueue(new(sequence, motion, udp));
    if (udp) Interlocked.Increment(ref _receivedUdp);
    else Interlocked.Increment(ref _receivedWebSocket);

    if (!DetailedTelemetryEnabled()) return;
    long current = Stopwatch.GetTimestamp();
    long previous = Interlocked.Exchange(ref _lastReceiveTimestamp, current);
    if (previous <= 0 || current <= previous) return;
    long intervalUs = StopwatchTicksToMicroseconds(current - previous);
    Interlocked.Increment(ref _receiveIntervalCount);
    Interlocked.Add(ref _receiveIntervalTotalUs, intervalUs);
    UpdateMax(ref _receiveIntervalMaxUs, intervalUs);
  }

  public static void EnqueueCanonicalMotion(
      ushort sequence, ValheimMotionSnapshot motion, bool udp) {
    LumberjacksMotionRunner active = Volatile.Read(ref _active);
    if (active == null || !AuthorityEnabled()) return;
    active.EnqueueReceived(sequence, motion, udp);
  }

  public static void NotifyCanonicalMotionSent(ushort sequence, bool udp) {
    LumberjacksMotionRunner active = Volatile.Read(ref _active);
    if (active == null) return;
    Interlocked.Increment(ref active._canonicalMotionSent);
    if (udp) Interlocked.Increment(ref active._sentUdp);
    else Interlocked.Increment(ref active._sentWebSocket);
    if (active._probe != null && !active._probe.Terminal &&
        (sequence <= 4 || IsPowerOfTwo(sequence))) {
      active.WriteAuthority(
          "motion_frame_sent", active._probe.ActionId,
          "sequence=" + sequence
          + " transport=" + (udp ? "udp" : "websocket")
          + " connection_id=" + active._gameSession?.ConnectionId);
    }
  }

  public static void EnqueueCanonicalFrame(string type, string text) {
    LumberjacksMotionRunner active = Volatile.Read(ref _active);
    if (active == null || !AuthorityEnabled()) return;
    active._canonicalFrames.Enqueue(new CanonicalFrame(type, text));
  }

  public bool BeginAuthorityProbe(
      string actionId,
      string mode,
      string correlationId,
      float durationSeconds,
      float deadlineSeconds,
      float distanceMeters,
      string direction,
      out string detail) {
    detail = string.Empty;
    if (!AuthorityEnabled()) {
      detail = "motion_authority_cutover_not_enabled";
      return false;
    }
    if (_gameSession?.WebSocketConnected != true) {
      detail = "lumberjacks_session_not_connected";
      return false;
    }
    if (!SafeToken(actionId, 80) || !SafeToken(correlationId, 80) ||
        mode is not ("drive" or "observe" or "drive_gap" or "observe_gap") ||
        durationSeconds is < 1.0f or > 30.0f ||
        deadlineSeconds < durationSeconds || deadlineSeconds > 60.0f ||
        distanceMeters is < 0.0f or > 24.0f ||
        !AllowedDirection(direction)) {
      detail = "motion_probe_parameters_invalid";
      return false;
    }
    if (Player.m_localPlayer == null) {
      detail = "motion_local_player_not_ready";
      return false;
    }
    if (mode.StartsWith("observe", StringComparison.Ordinal) &&
        _remote.Count == 0) {
      detail = "motion_remote_not_ready";
      return false;
    }
    if (_probe != null && !_probe.Terminal) {
      detail = "another_motion_probe_active";
      return false;
    }

    float now = Time.unscaledTime;
    _probe = new MotionProbe {
        ActionId = actionId,
        CorrelationId = correlationId,
        Mode = mode,
        StartedAt = now,
        DeadlineAt = now + deadlineSeconds,
        DurationSeconds = durationSeconds,
        DistanceMeters = distanceMeters,
        Direction = direction,
        Origin = ((Component) Player.m_localPlayer).transform.position,
        GapStartsAt = now + Mathf.Min(durationSeconds * 0.30f, 1.5f),
        SentBefore = Interlocked.Read(ref _canonicalMotionSent),
        ReceivedBefore =
            Interlocked.Read(ref _receivedUdp) +
            Interlocked.Read(ref _receivedWebSocket),
        AppliedBefore = Interlocked.Read(ref _applied),
        NativeWriterSuppressedBefore =
            Interlocked.Read(ref _nativeWriterSuppressed),
        NativePositionMaskedBefore =
            Interlocked.Read(ref _nativePositionMasked),
        HoldsBefore = Interlocked.Read(ref _authorityHolds),
        GapsBefore = Interlocked.Read(ref _authorityGaps),
        ResyncAppliedBefore =
            Interlocked.Read(ref _reliableResyncApplied),
        FailuresBefore = Interlocked.Read(ref _authorityFailures)
    };
    WriteAuthority(
        "probe_started", actionId,
        "mode=" + mode
        + " correlation_id=" + correlationId
        + " connection_id=" + _gameSession.ConnectionId
        + " logical_peer_id=" + _gameSession.LogicalPeerId);
    detail = "motion_probe_started";
    return true;
  }

  public bool TryRendezvous(
      string actionId, out bool complete, out string detail) {
    complete = false;
    detail = string.Empty;
    if (!AuthorityEnabled() || _gameSession?.WebSocketConnected != true ||
        Player.m_localPlayer == null) {
      detail = "motion_rendezvous_not_ready";
      return true;
    }
    if (!SafeToken(actionId, 80)) {
      detail = "motion_rendezvous_action_invalid";
      return false;
    }

    RemoteMotion selected = null;
    foreach (RemoteMotion candidate in _remote.Values) {
      if (selected == null || candidate.ArrivedAt > selected.ArrivedAt)
        selected = candidate;
    }
    if (selected == null) {
      detail = "motion_remote_not_ready";
      return true;
    }

    if (!string.Equals(_rendezvousActionId, actionId, StringComparison.Ordinal)) {
      _rendezvousActionId = actionId;
      _rendezvousMoved = false;
      _rendezvousCompleteWritten = false;
    }

    ZDOID remoteId = new(selected.Snapshot.ZdoUserId, selected.Snapshot.ZdoId);
    GameObject remoteInstance = ResolveInstance(remoteId, Time.unscaledTime);
    if (remoteInstance != null &&
        remoteInstance != Player.m_localPlayer.gameObject) {
      complete = true;
      detail = "remote_player_instance_ready uid="
          + remoteId.UserID + ":" + remoteId.ID;
      if (!_rendezvousCompleteWritten) {
        _rendezvousCompleteWritten = true;
        WriteAuthority("rendezvous_complete", actionId, detail);
      }
      return true;
    }

    if (!_rendezvousMoved) {
      Vector3 target = new(
          selected.Snapshot.X + 3.0f,
          selected.Snapshot.Y,
          selected.Snapshot.Z + 3.0f);
      Player.m_localPlayer.transform.position = target;
      Rigidbody body = Player.m_localPlayer.GetComponent<Rigidbody>();
      if (body != null) body.linearVelocity = Vector3.zero;
      _rendezvousMoved = true;
      WriteAuthority(
          "rendezvous_moved", actionId,
          "remote_uid=" + remoteId.UserID + ":" + remoteId.ID
          + " target=" + target.x.ToString("0.###", CultureInfo.InvariantCulture)
          + "," + target.y.ToString("0.###", CultureInfo.InvariantCulture)
          + "," + target.z.ToString("0.###", CultureInfo.InvariantCulture)
          + " source=lumberjacks_motion");
    }
    detail = "motion_rendezvous_waiting_for_remote_instance";
    return true;
  }

  public bool TryGetAuthorityProbeResult(
      string actionId,
      out bool terminal,
      out bool success,
      out string detail) {
    if (_probe == null ||
        !string.Equals(_probe.ActionId, actionId, StringComparison.Ordinal)) {
      terminal = false;
      success = false;
      detail = "motion_probe_not_found";
      return false;
    }
    terminal = _probe.Terminal;
    success = _probe.Success;
    detail = _probe.Detail ?? string.Empty;
    return true;
  }

  void UpdateProbeDrive(float now) {
    MotionProbe probe = _probe;
    if (probe == null || probe.Terminal ||
        !probe.Mode.StartsWith("drive", StringComparison.Ordinal))
      return;
    float fraction = Mathf.Clamp01(
        (now - probe.StartedAt) / probe.DurationSeconds);
    Vector3 target =
        probe.Origin +
        Direction(probe.Direction) * (probe.DistanceMeters * fraction);
    target.y = probe.Origin.y;
    ((Component) Player.m_localPlayer).transform.position = target;
  }

  void EvaluateProbe(float now) {
    MotionProbe probe = _probe;
    if (probe == null || probe.Terminal) return;
    if (now > probe.DeadlineAt) {
      FailProbe("motion_probe_deadline_exceeded");
      return;
    }
    if (now - probe.StartedAt < probe.DurationSeconds) return;

    long sent =
        Interlocked.Read(ref _canonicalMotionSent) - probe.SentBefore;
    long received =
        Interlocked.Read(ref _receivedUdp) +
        Interlocked.Read(ref _receivedWebSocket) -
        probe.ReceivedBefore;
    long applied = Interlocked.Read(ref _applied) - probe.AppliedBefore;
    long writerSuppressed =
        Interlocked.Read(ref _nativeWriterSuppressed) -
        probe.NativeWriterSuppressedBefore;
    long positionMasked =
        Interlocked.Read(ref _nativePositionMasked) -
        probe.NativePositionMaskedBefore;
    long holds =
        Interlocked.Read(ref _authorityHolds) - probe.HoldsBefore;
    long gaps =
        Interlocked.Read(ref _authorityGaps) - probe.GapsBefore;
    long resyncApplied =
        Interlocked.Read(ref _reliableResyncApplied) -
        probe.ResyncAppliedBefore;
    long failures =
        Interlocked.Read(ref _authorityFailures) - probe.FailuresBefore;

    bool passed;
    string reason;
    switch (probe.Mode) {
      case "drive":
        passed = sent >= 12 && failures == 0;
        reason = passed
            ? "numbered_motion_sent"
            : "drive_motion_boundary_incomplete";
        break;
      case "observe":
        passed = received >= 12 && applied > 0 &&
            writerSuppressed > 0 && failures == 0;
        reason = passed
            ? "numbered_motion_applied_native_writer_zero"
            : "observe_motion_boundary_incomplete";
        break;
      case "drive_gap":
        passed = sent >= 12 && probe.GapDropped == GapFrameCount &&
            probe.ResyncReceiptAccepted && failures == 0;
        reason = passed
            ? "gap_withheld_reliable_resync_accepted"
            : "drive_gap_boundary_incomplete";
        break;
      default:
        passed = received >= 12 && applied > 0 &&
            writerSuppressed > 0 && holds > 0 && gaps > 0 &&
            resyncApplied > 0 &&
            string.Equals(
                _lastResyncActionId, probe.CorrelationId,
                StringComparison.Ordinal) &&
            failures == 0;
        reason = passed
            ? "gap_held_reliable_resync_applied_native_writer_zero"
            : "observe_gap_boundary_incomplete";
        break;
    }

    string counts =
        "sent=" + sent
        + " received=" + received
        + " applied=" + applied
        + " native_writer_suppressed=" + writerSuppressed
        + " native_position_masked=" + positionMasked
        + " holds=" + holds
        + " gaps=" + gaps
        + " resync_applied=" + resyncApplied
        + " failures=" + failures
        + " gap_dropped=" + probe.GapDropped;
    if (!passed) {
      FailProbe(reason + " " + counts);
      return;
    }
    probe.Terminal = true;
    probe.Success = true;
    probe.Detail = reason + " " + counts;
    WriteAuthority("probe_passed", probe.ActionId, probe.Detail);
  }

  void FailProbe(string detail) {
    MotionProbe probe = _probe;
    if (probe == null || probe.Terminal) return;
    probe.Terminal = true;
    probe.Success = false;
    probe.Detail = detail;
    Interlocked.Increment(ref _authorityFailures);
    WriteAuthority("probe_failed", probe.ActionId, detail);
  }

  void QueueReliableResync(MotionProbe probe, OutboundMotion outbound) {
    string fields =
        "\"run_id\":\"" + JsonEscape(NativeAutotestRequest.ActiveRunId)
        + "\",\"action_id\":\"" + JsonEscape(probe.CorrelationId)
        + "\",\"source_motion_sequence\":" + outbound.Sequence
        + ",\"gap_start_sequence\":" + probe.GapStart
        + ",\"gap_end_sequence\":" + probe.GapEnd
        + ",\"zdo_user_id\":"
        + outbound.Snapshot.ZdoUserId.ToString(CultureInfo.InvariantCulture)
        + ",\"zdo_id\":"
        + outbound.Snapshot.ZdoId.ToString(CultureInfo.InvariantCulture)
        + ",\"x\":" + Float(outbound.Snapshot.X)
        + ",\"y\":" + Float(outbound.Snapshot.Y)
        + ",\"z\":" + Float(outbound.Snapshot.Z)
        + ",\"velocity_x\":" + Float(outbound.Snapshot.VelocityX)
        + ",\"velocity_y\":" + Float(outbound.Snapshot.VelocityY)
        + ",\"velocity_z\":" + Float(outbound.Snapshot.VelocityZ)
        + ",\"yaw\":" + Float(outbound.Snapshot.Yaw)
        + ",\"sent_milliseconds\":"
        + outbound.Snapshot.SentMilliseconds
            .ToString(CultureInfo.InvariantCulture)
        + ",\"reason\":\"gap_recovery\"";
    if (_gameSession?.TryQueueMotionResync(fields) != true) {
      FailProbe("reliable_motion_resync_queue_failed");
      return;
    }
    Interlocked.Increment(ref _reliableResyncQueued);
    WriteAuthority(
        "reliable_resync_queued", probe.ActionId,
        "correlation_id=" + probe.CorrelationId
        + " motion_sequence=" + outbound.Sequence
        + " gap_start=" + probe.GapStart
        + " gap_end=" + probe.GapEnd);
  }

  void DrainCanonicalFrames() {
    while (_canonicalFrames.TryDequeue(out CanonicalFrame frame)) {
      long reliableSequence = ExtractJsonLong(frame.Text, "seq");
      string runId = ExtractJsonString(frame.Text, "run_id");
      string actionId = ExtractJsonString(frame.Text, "action_id");
      if (!string.Equals(
              runId, NativeAutotestRequest.ActiveRunId,
              StringComparison.Ordinal) ||
          !SafeToken(actionId, 80) || reliableSequence <= 0) {
        Interlocked.Increment(ref _authorityFailures);
        WriteAuthority(
            "canonical_frame_rejected", actionId,
            "reason=run_or_sequence_invalid type=" + frame.Type);
        continue;
      }

      if (frame.Type == "valheim_motion_resync_receipt") {
        string result = ExtractJsonString(frame.Text, "result");
        int targetCount = ExtractJsonInt(frame.Text, "target_count");
        if (_probe != null && !_probe.Terminal &&
            _probe.Mode == "drive_gap" &&
            string.Equals(
                _probe.CorrelationId, actionId,
                StringComparison.Ordinal) &&
            result == "accepted" && targetCount > 0) {
          _probe.ResyncReceiptAccepted = true;
        }
        Interlocked.Increment(ref _reliableResyncReceipts);
        bool acknowledged =
            _gameSession?.QueueReliableAck(reliableSequence) == true;
        WriteAuthority(
            "reliable_resync_receipt", _probe?.ActionId ?? actionId,
            "correlation_id=" + actionId
            + " result=" + result
            + " target_count=" + targetCount
            + " reliable_sequence=" + reliableSequence
            + " ack_queued=" + (acknowledged ? "true" : "false"));
        continue;
      }

      long userId = ExtractJsonLong(frame.Text, "zdo_user_id");
      long zdoIdLong = ExtractJsonLong(frame.Text, "zdo_id");
      int motionSequence = ExtractJsonInt(
          frame.Text, "source_motion_sequence");
      int gapStart = ExtractJsonInt(frame.Text, "gap_start_sequence");
      int gapEnd = ExtractJsonInt(frame.Text, "gap_end_sequence");
      if (userId == 0 || zdoIdLong is <= 0 or > uint.MaxValue ||
          motionSequence is <= 0 or > ushort.MaxValue ||
          gapStart <= 0 || gapEnd < gapStart) {
        Interlocked.Increment(ref _authorityFailures);
        WriteAuthority(
            "canonical_frame_rejected", actionId,
            "reason=motion_resync_payload_invalid");
        continue;
      }
      ValheimMotionSnapshot snapshot = new(
          userId,
          (uint) zdoIdLong,
          ExtractJsonFloat(frame.Text, "x"),
          ExtractJsonFloat(frame.Text, "y"),
          ExtractJsonFloat(frame.Text, "z"),
          ExtractJsonFloat(frame.Text, "velocity_x"),
          ExtractJsonFloat(frame.Text, "velocity_y"),
          ExtractJsonFloat(frame.Text, "velocity_z"),
          ExtractJsonFloat(frame.Text, "yaw"),
          (uint) Math.Max(
              0, ExtractJsonLong(frame.Text, "sent_milliseconds")));
      ApplyReliableResync(
          (ushort) motionSequence, snapshot, reliableSequence,
          actionId, gapStart, gapEnd);
    }
  }

  void ApplyReliableResync(
      ushort motionSequence,
      ValheimMotionSnapshot snapshot,
      long reliableSequence,
      string actionId,
      int gapStart,
      int gapEnd) {
    string resyncKey = actionId + ":" + motionSequence;
    if (!_appliedResyncKeys.Add(resyncKey)) {
      _gameSession?.QueueReliableAck(reliableSequence);
      WriteAuthority(
          "reliable_resync_replayed_idempotent",
          _probe?.ActionId ?? actionId,
          "correlation_id=" + actionId
          + " motion_sequence=" + motionSequence
          + " reliable_sequence=" + reliableSequence);
      return;
    }

    ZDOID zdoId = new(snapshot.ZdoUserId, snapshot.ZdoId);
    GameObject instance = ResolveInstance(zdoId, Time.unscaledTime);
    if (instance == null ||
        (Player.m_localPlayer != null &&
         instance == Player.m_localPlayer.gameObject)) {
      _appliedResyncKeys.Remove(resyncKey);
      Interlocked.Increment(ref _authorityFailures);
      WriteAuthority(
          "reliable_resync_rejected",
          _probe?.ActionId ?? actionId,
          "reason=remote_player_not_found correlation_id=" + actionId);
      return;
    }

    Vector3 target = new(snapshot.X, snapshot.Y, snapshot.Z);
    instance.transform.position = target;
    instance.transform.rotation =
        Quaternion.Euler(0.0f, snapshot.Yaw, 0.0f);
    Rigidbody body = instance.GetComponent<Rigidbody>();
    if (body != null) {
      body.linearVelocity = new Vector3(
          snapshot.VelocityX, snapshot.VelocityY, snapshot.VelocityZ);
    }
    RemoteMotion remote = new(
        motionSequence, snapshot, Time.unscaledTime,
        false, 0, actionId, gapStart, gapEnd) {
        HasAppliedPosition = true,
        LastAppliedPosition = target,
        ReliableAcknowledged = true
    };
    _remote[snapshot.ZdoUserId + ":" + snapshot.ZdoId] = remote;
    Interlocked.Increment(ref _applied);
    Interlocked.Increment(ref _authorityGaps);
    Interlocked.Increment(ref _reliableResyncApplied);
    _lastResyncActionId = actionId;
    bool acknowledged =
        _gameSession?.QueueReliableAck(reliableSequence) == true;
    WriteAuthority(
        "reliable_resync_applied", _probe?.ActionId ?? actionId,
        "correlation_id=" + actionId
        + " motion_sequence=" + motionSequence
        + " reliable_sequence=" + reliableSequence
        + " gap_start=" + gapStart
        + " gap_end=" + gapEnd
        + " ack_queued=" + (acknowledged ? "true" : "false"));
  }

  public static bool SuppressNativeTransform(ZSyncTransform transform) {
    LumberjacksMotionRunner active = Volatile.Read(ref _active);
    if (active == null || !AuthorityEnabled() ||
        transform == null || !active.IsCanonicalRemote(transform))
      return false;
    long count = Interlocked.Increment(ref active._nativeWriterSuppressed);
    if (count <= 4 || IsPowerOfTwo(count)) {
      active.WriteAuthority(
          "native_transform_writer_suppressed",
          active._probe?.ActionId ?? string.Empty,
          "count=" + count + " writer=ZSyncTransform.CustomFixedUpdate");
    }
    return true;
  }

  public static bool MaskNativePosition(ZDO zdo) {
    LumberjacksMotionRunner active = Volatile.Read(ref _active);
    if (active == null || !AuthorityEnabled() ||
        zdo == null || !active.HasRemote(zdo.m_uid))
      return false;
    long count = Interlocked.Increment(ref active._nativePositionMasked);
    if (count <= 4 || IsPowerOfTwo(count)) {
      active.WriteAuthority(
          "native_player_zdo_position_masked",
          active._probe?.ActionId ?? string.Empty,
          "count=" + count + " uid=" + zdo.m_uid);
    }
    return true;
  }

  bool IsCanonicalRemote(ZSyncTransform transform) {
    try {
      ZNetView view = transform.GetComponent<ZNetView>();
      ZDO zdo = view?.GetZDO();
      return zdo != null && HasRemote(zdo.m_uid);
    } catch {
      return false;
    }
  }

  bool HasRemote(ZDOID id) =>
      _remote.TryGetValue(
          id.UserID + ":" + id.ID,
          out RemoteMotion remote) &&
      remote.HasAppliedPosition;

  void HoldRemote(RemoteMotion remote, string reason) {
    ZDOID zdoId = new(remote.Snapshot.ZdoUserId, remote.Snapshot.ZdoId);
    GameObject instance = ResolveInstance(zdoId, Time.unscaledTime);
    Rigidbody body = instance?.GetComponent<Rigidbody>();
    if (body != null) body.linearVelocity = Vector3.zero;
    remote.HoldReason = reason;
  }

  void WriteAuthority(string state, string actionId, string detail) {
    Dictionary<string, object> row = new() {
        ["schema_version"] = 1,
        ["timestamp_utc"] =
            DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        ["state"] = state,
        ["run_id"] = NativeAutotestRequest.ActiveRunId,
        ["client"] = NativeAutotestRequest.ActiveClient,
        ["action_id"] = actionId ?? string.Empty,
        ["detail"] = detail ?? string.Empty
    };
    _writer.Write(AuthorityReceiptFileName, row);
    ComfyNetworkSense.LogInfo(
        "MOTION_AUTHORITY state=" + SafeMarker(state)
        + " run_id=" + SafeMarker(NativeAutotestRequest.ActiveRunId)
        + " client=" + SafeMarker(NativeAutotestRequest.ActiveClient)
        + " action=" + SafeMarker(actionId)
        + " detail=" + SafeMarker(detail));
  }

  GameObject ResolveInstance(ZDOID zdoId, float now) {
    ZNetScene scene = ZNetScene.instance;
    if (scene == null) return null;

    // Prefer the direct lookup, then resolve through ZDOMan. The latter matters on
    // clients where the authoritative ZDO has arrived before ZNetScene has indexed
    // the corresponding view under the ID overload.
    GameObject direct = scene.FindInstance(zdoId);
    if (direct != null) {
      Interlocked.Increment(ref _directLookupHits);
      return direct;
    }

    ZDO zdo = ZDOMan.instance?.GetZDO(zdoId);
    ZNetView view = zdo == null ? null : scene.FindInstance(zdo);
    GameObject resolved = view?.gameObject;
    if (resolved != null) {
      Interlocked.Increment(ref _zdoObjectLookupHits);
      return resolved;
    }

    if (now >= _nextPlayerIndexAt) {
      _nextPlayerIndexAt = now + 1.0f;
      RebuildPlayerIndex();
    }
    if (_playerInstances.TryGetValue(zdoId, out GameObject player)) {
      Interlocked.Increment(ref _playerIndexLookupHits);
      return player;
    }
    return null;
  }

  void RebuildPlayerIndex() {
    Interlocked.Increment(ref _playerIndexRebuilds);
    _playerInstances.Clear();
    foreach (Player player in UnityEngine.Object.FindObjectsByType<Player>(FindObjectsSortMode.None)) {
      ZNetView view = player?.GetComponent<ZNetView>();
      ZDO zdo = view?.GetZDO();
      if (zdo != null && player != null) _playerInstances[zdo.m_uid] = player.gameObject;
    }
  }

  string NormalizeGatewayUrl() {
    string value = Environment.GetEnvironmentVariable("COMFY_LUMBERJACKS_GATEWAY_URL");
    if (string.IsNullOrWhiteSpace(value)) value = PluginConfig.LumberjacksGatewayUrl.Value;
    value = (value ?? "ws://127.0.0.1:4000").Trim();
    if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return "wss://" + value.Substring(8);
    if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return "ws://" + value.Substring(7);
    return value;
  }

  string BuildJoinRegion() => "{\"version\":1,\"type\":\"join_region\",\"seq\":1,\"timestamp\":\""
      + DateTimeOffset.UtcNow.ToString("o") + "\",\"payload\":{\"region_id\":\""
      + JsonEscape(PluginConfig.LumberjacksRegionId.Value) + "\"}}";

  static async Task SendText(ClientWebSocket socket, string text, CancellationToken token) {
    byte[] data = Encoding.UTF8.GetBytes(text);
    await socket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
  }

  static async Task<ReceivedFrame> ReceiveFrame(ClientWebSocket socket, byte[] buffer, CancellationToken token) {
    using MemoryStream stream = new();
    WebSocketReceiveResult result;
    do {
      result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
      if (result.MessageType == WebSocketMessageType.Close) return null;
      stream.Write(buffer, 0, result.Count);
    } while (!result.EndOfMessage);
    return new(result.MessageType, stream.ToArray());
  }

  static string ExtractJsonString(string json, string name) {
    Match match = Regex.Match(json ?? string.Empty,
        "\"" + Regex.Escape(name) + "\"\\s*:\\s*\"(?<value>(?:\\\\.|[^\"])*)\"", RegexOptions.IgnoreCase);
    return match.Success ? match.Groups["value"].Value : string.Empty;
  }
  static int ExtractJsonInt(string json, string name) {
    Match match = Regex.Match(json ?? string.Empty,
        "\"" + Regex.Escape(name) + "\"\\s*:\\s*(?<value>[0-9]+)", RegexOptions.IgnoreCase);
    return match.Success && int.TryParse(match.Groups["value"].Value, out int value) ? value : 0;
  }
  static long ExtractJsonLong(string json, string name) {
    Match match = Regex.Match(
        json ?? string.Empty,
        "\"" + Regex.Escape(name)
        + "\"\\s*:\\s*(?<value>-?[0-9]+)",
        RegexOptions.IgnoreCase);
    return match.Success &&
        long.TryParse(
            match.Groups["value"].Value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out long value)
        ? value
        : 0;
  }
  static float ExtractJsonFloat(string json, string name) {
    Match match = Regex.Match(
        json ?? string.Empty,
        "\"" + Regex.Escape(name)
        + "\"\\s*:\\s*(?<value>-?(?:[0-9]+(?:\\.[0-9]*)?|\\.[0-9]+)(?:[eE][+-]?[0-9]+)?)",
        RegexOptions.IgnoreCase);
    return match.Success &&
        float.TryParse(
            match.Groups["value"].Value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out float value) &&
        !float.IsNaN(value) && !float.IsInfinity(value)
        ? value
        : 0.0f;
  }
  static string JsonEscape(string value) => (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
  static string Float(float value) =>
      value.ToString("R", CultureInfo.InvariantCulture);
  static bool AuthorityEnabled() =>
      PluginConfig.MotionAuthorityCutoverEnabled?.Value == true ||
      NativeAutotestRequest.ActiveMotionAuthorityCutover;
  static bool CanonicalSessionEnabled() =>
      PluginConfig.LumberjacksGameSessionEnabled?.Value == true;
  static bool SafeToken(string value, int maximumLength) {
    if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
      return false;
    foreach (char character in value) {
      if (!char.IsLetterOrDigit(character) &&
          character is not ('-' or '_' or '.'))
        return false;
    }
    return true;
  }
  static bool AllowedDirection(string value) =>
      (value ?? string.Empty).Trim().ToLowerInvariant()
          is "north" or "east" or "south" or "west";
  static Vector3 Direction(string value) =>
      (value ?? string.Empty).Trim().ToLowerInvariant() switch {
          "east" => Vector3.right,
          "south" => Vector3.back,
          "west" => Vector3.left,
          _ => Vector3.forward
      };
  static string SafeMarker(string value) =>
      string.IsNullOrWhiteSpace(value)
          ? "none"
          : value.Trim().Replace(' ', '_').Replace('\t', '_')
              .Replace('\r', '_').Replace('\n', '_');
  static bool IsPowerOfTwo(long value) =>
      value > 0 && (value & (value - 1)) == 0;
  static bool IsDedicatedServer() => ZNet.instance != null && ZNet.instance.IsServer() && ZNet.instance.IsDedicated();
  static bool DetailedTelemetryEnabled() =>
      PluginConfig.WriteTelemetryLogs != null && PluginConfig.WriteTelemetryLogs.Value;
  static long SecondsToMilliseconds(float seconds) =>
      (long) Math.Round(Math.Max(0.0, seconds) * 1000.0);
  static long MetersToMillimeters(float meters) =>
      (long) Math.Round(Math.Max(0.0, meters) * 1000.0);
  static string PositionDetail(ValheimMotionSnapshot snapshot) =>
      snapshot.X.ToString("0.###", CultureInfo.InvariantCulture) + ","
      + snapshot.Y.ToString("0.###", CultureInfo.InvariantCulture) + ","
      + snapshot.Z.ToString("0.###", CultureInfo.InvariantCulture);
  string KnownPlayerIds() {
    StringBuilder builder = new();
    foreach (ZDOID id in _playerInstances.Keys) {
      if (builder.Length > 0) builder.Append(',');
      builder.Append(id.UserID).Append(':').Append(id.ID);
    }
    return builder.Length == 0 ? "none" : builder.ToString();
  }
  static long ElapsedMicroseconds(long started) =>
      StopwatchTicksToMicroseconds(Math.Max(0, Stopwatch.GetTimestamp() - started));
  static long StopwatchTicksToMicroseconds(long ticks) =>
      (long) Math.Round(ticks * 1000000.0 / Stopwatch.Frequency);
  static void RecordMainThreadTotalAndMax(ref long total, ref long maximum, long value) {
    total += value;
    UpdateMainThreadMax(ref maximum, value);
  }
  static void UpdateMainThreadMax(ref long target, long candidate) {
    if (candidate > target) target = candidate;
  }
  static void UpdateMax(ref long target, long candidate) {
    long current = Interlocked.Read(ref target);
    while (candidate > current) {
      long observed = Interlocked.CompareExchange(ref target, candidate, current);
      if (observed == current) return;
      current = observed;
    }
  }
  static async Task IgnoreCancellation(Task task) { try { await task.ConfigureAwait(false); } catch { } }

  void SetState(string state, bool websocket, bool udp, string error) {
    lock (_statusLock) { _state = state; _webSocketConnected = websocket; _udpReady = udp; _lastError = error ?? string.Empty; }
  }

  public void Dispose() {
    if (_disposed) return;
    _disposed = true;
    Stop();
    Interlocked.CompareExchange(ref _active, null, this);
    _writer.Dispose();
  }

  sealed class OutboundMotion { public readonly ushort Sequence; public readonly ValheimMotionSnapshot Snapshot; public OutboundMotion(ushort s, ValheimMotionSnapshot m) { Sequence = s; Snapshot = m; } }
  sealed class ReceivedMotion {
    public readonly ushort Sequence;
    public readonly ValheimMotionSnapshot Snapshot;
    public readonly bool Udp;
    public readonly bool HardResync;
    public readonly long ReliableSequence;
    public readonly string ActionId;
    public readonly int GapStart;
    public readonly int GapEnd;
    public ReceivedMotion(
        ushort sequence, ValheimMotionSnapshot snapshot, bool udp,
        bool hardResync = false, long reliableSequence = 0,
        string actionId = "", int gapStart = 0, int gapEnd = 0) {
      Sequence = sequence;
      Snapshot = snapshot;
      Udp = udp;
      HardResync = hardResync;
      ReliableSequence = reliableSequence;
      ActionId = actionId ?? string.Empty;
      GapStart = gapStart;
      GapEnd = gapEnd;
    }
  }
  sealed class RemoteMotion {
    public readonly ushort Sequence;
    public readonly ValheimMotionSnapshot Snapshot;
    public readonly float ArrivedAt;
    public readonly bool HardResync;
    public readonly long ReliableSequence;
    public readonly string ActionId;
    public readonly int GapStart;
    public readonly int GapEnd;
    public bool FallbackNoted;
    public bool HasAppliedPosition;
    public bool ReliableAcknowledged;
    public Vector3 LastAppliedPosition;
    public string HoldReason;
    public RemoteMotion(
        ushort sequence, ValheimMotionSnapshot snapshot, float arrivedAt,
        bool hardResync, long reliableSequence, string actionId,
        int gapStart, int gapEnd) {
      Sequence = sequence;
      Snapshot = snapshot;
      ArrivedAt = arrivedAt;
      HardResync = hardResync;
      ReliableSequence = reliableSequence;
      ActionId = actionId ?? string.Empty;
      GapStart = gapStart;
      GapEnd = gapEnd;
    }
    public void CopyPresentationState(RemoteMotion previous) {
      HasAppliedPosition = previous.HasAppliedPosition;
      LastAppliedPosition = previous.LastAppliedPosition;
    }
  }
  sealed class MotionProbe {
    public string ActionId;
    public string CorrelationId;
    public string Mode;
    public float StartedAt;
    public float DeadlineAt;
    public float DurationSeconds;
    public float DistanceMeters;
    public string Direction;
    public Vector3 Origin;
    public float GapStartsAt;
    public int GapDropped;
    public ushort GapStart;
    public ushort GapEnd;
    public bool ResyncReceiptAccepted;
    public long SentBefore;
    public long ReceivedBefore;
    public long AppliedBefore;
    public long NativeWriterSuppressedBefore;
    public long NativePositionMaskedBefore;
    public long HoldsBefore;
    public long GapsBefore;
    public long ResyncAppliedBefore;
    public long FailuresBefore;
    public bool Terminal;
    public bool Success;
    public string Detail = string.Empty;
  }
  sealed class CanonicalFrame {
    public readonly string Type;
    public readonly string Text;
    public CanonicalFrame(string type, string text) {
      Type = type ?? string.Empty;
      Text = text ?? string.Empty;
    }
  }
  sealed class ReceivedFrame { public readonly WebSocketMessageType Type; public readonly byte[] Data; public ReceivedFrame(WebSocketMessageType type, byte[] data) { Type = type; Data = data; } }
}
