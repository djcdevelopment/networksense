namespace ComfyNetworkSense;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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

  readonly ConcurrentQueue<ReceivedMotion> _received = new();
  readonly Dictionary<string, RemoteMotion> _remote = new(StringComparer.Ordinal);
  readonly Dictionary<ZDOID, GameObject> _playerInstances = new();
  readonly HashSet<string> _drainedKeys = new(StringComparer.Ordinal);
  readonly object _outboundLock = new();
  readonly object _statusLock = new();

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

  public void Update(float now) {
    if (!ShouldRun()) {
      if (IsRunning) Stop();
      return;
    }

    if (!IsRunning && now >= _nextConnectAt) Start();
    DrainReceived(now);
    CaptureLocalMotion(now);
  }

  public void LateUpdate(float deltaTime) {
    if (!AlphaTransportSwitches.MotionApplyEnabled || IsDedicatedServer()) return;
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
          remote.HasAppliedPosition = false;
          if (!remote.FallbackNoted) {
            remote.FallbackNoted = true;
            Interlocked.Increment(ref _staleFallbacks);
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
          continue;
        }
        if (Player.m_localPlayer != null && instance == Player.m_localPlayer.gameObject) {
          remote.HasAppliedPosition = false;
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

        // Fail back to native for implausible corrections. Portals and initial spawn settle natively;
        // Channel 2 resumes once both presentations are in the same neighborhood.
        if (targetError > 30.0f) {
          remote.HasAppliedPosition = false;
          _correctionGuardRejections++;
          continue;
        }

        Vector3 appliedPosition = Vector3.Lerp(current, target, alpha);
        instance.transform.position = appliedPosition;
        Quaternion rotation = Quaternion.Euler(0.0f, remote.Snapshot.Yaw, 0.0f);
        instance.transform.rotation = Quaternion.Slerp(instance.transform.rotation, rotation, alpha);
        remote.LastAppliedPosition = appliedPosition;
        remote.HasAppliedPosition = true;
        Interlocked.Increment(ref _applied);
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
          ["last_error"] = _lastError
      };
    }
  }

  bool ShouldRun() {
    if (!PluginConfig.LumberjacksMotionEnabled.Value || IsDedicatedServer()) return false;
    if (!AlphaTransportSwitches.LumberjacksWebSocketEnabled) return false;
    if (Player.m_localPlayer == null || ZNet.instance == null || ZNet.instance.IsServer()) return false;
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
    if (!WebSocketConnected || now < _nextSampleAt) return;
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
      RemoteMotion updated = new(received.Sequence, received.Snapshot, now);
      if (existing != null) updated.CopyPresentationState(existing);
      _remote[key] = updated;
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
  static string JsonEscape(string value) => (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
  static bool IsDedicatedServer() => ZNet.instance != null && ZNet.instance.IsServer() && ZNet.instance.IsDedicated();
  static bool DetailedTelemetryEnabled() =>
      PluginConfig.WriteTelemetryLogs != null && PluginConfig.WriteTelemetryLogs.Value;
  static long SecondsToMilliseconds(float seconds) =>
      (long) Math.Round(Math.Max(0.0, seconds) * 1000.0);
  static long MetersToMillimeters(float meters) =>
      (long) Math.Round(Math.Max(0.0, meters) * 1000.0);
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

  public void Dispose() => Stop();

  sealed class OutboundMotion { public readonly ushort Sequence; public readonly ValheimMotionSnapshot Snapshot; public OutboundMotion(ushort s, ValheimMotionSnapshot m) { Sequence = s; Snapshot = m; } }
  sealed class ReceivedMotion { public readonly ushort Sequence; public readonly ValheimMotionSnapshot Snapshot; public readonly bool Udp; public ReceivedMotion(ushort s, ValheimMotionSnapshot m, bool udp) { Sequence = s; Snapshot = m; Udp = udp; } }
  sealed class RemoteMotion {
    public readonly ushort Sequence;
    public readonly ValheimMotionSnapshot Snapshot;
    public readonly float ArrivedAt;
    public bool FallbackNoted;
    public bool HasAppliedPosition;
    public Vector3 LastAppliedPosition;
    public RemoteMotion(ushort s, ValheimMotionSnapshot m, float at) { Sequence = s; Snapshot = m; ArrivedAt = at; }
    public void CopyPresentationState(RemoteMotion previous) {
      HasAppliedPosition = previous.HasAppliedPosition;
      LastAppliedPosition = previous.LastAppliedPosition;
    }
  }
  sealed class ReceivedFrame { public readonly WebSocketMessageType Type; public readonly byte[] Data; public ReceivedFrame(WebSocketMessageType type, byte[] data) { Type = type; Data = data; } }
}
