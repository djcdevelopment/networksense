namespace ComfyNetworkSense;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using UnityEngine;

public sealed class LumberjacksShadowAuthorityRunner : IDisposable {
  const int ConnectTimeoutMs = 5000;
  const int ReceiveBufferBytes = 32768;

  readonly ConcurrentQueue<ShadowInput> _pendingInputs = new();
  readonly object _statsLock = new();

  CancellationTokenSource _cts;
  Task _receiveTask;
  Task _sendTask;
  ClientWebSocket _socket;

  string _gatewayUrl = "ws://127.0.0.1:4000";
  string _regionId = "region-spawn";
  string _playerId = string.Empty;
  string _status = "idle";
  string _lastError = string.Empty;
  string _lastMessageType = string.Empty;
  string _runLabel = "manual";
  string _routeStopId = string.Empty;
  string _routePhase = string.Empty;
  DateTime _startedUtc;
  DateTime _lastMessageUtc;
  DateTime _lastAuthorityUtc;

  int _messagesReceived;
  int _snapshotsReceived;
  int _entityUpdatesReceived;
  int _selfAuthorityUpdates;
  int _inputsQueued;
  int _inputsSent;
  int _samples;
  int _driftSamples;
  int _errors;
  int _lastInputSeqEcho;

  bool _connected;
  bool _sessionStarted;
  bool _worldSnapshotReceived;
  bool _valheimStartSet;
  bool _lastValheimSet;
  bool _authorityStartSet;

  ushort _inputSeq = 1;
  float _lastSampleTime;
  float _sampleAccumulator;
  float _logAccumulator;

  Vector3 _valheimStart;
  Vector3 _lastValheimPosition;
  Vector3 _authorityStart;
  Vector3 _latestAuthorityPosition;

  float _lastValheimSpeedMetersPerSecond;
  float _lastLumberjacksDeltaUnits;
  float _lastAuthorityScaledMeters;
  float _lastDriftMeters;
  float _maxDriftMeters;
  float _totalDriftMeters;
  float _lastDirectionErrorDegrees;

  public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;

  public string Start(string gatewayUrl, string regionId, TelemetryCoordinator coordinator) {
    if (IsRunning) {
      return $"Lumberjacks shadow authority already running: {GetStatus()}";
    }

    _gatewayUrl = NormalizeGatewayUrl(gatewayUrl);
    _regionId = string.IsNullOrWhiteSpace(regionId) ? "region-spawn" : regionId.Trim();
    _playerId = string.Empty;
    _status = "starting";
    _lastError = string.Empty;
    _lastMessageType = string.Empty;
    _runLabel = "manual";
    _routeStopId = string.Empty;
    _routePhase = "start";
    _startedUtc = DateTime.UtcNow;
    _lastMessageUtc = DateTime.MinValue;
    _lastAuthorityUtc = DateTime.MinValue;

    _messagesReceived = 0;
    _snapshotsReceived = 0;
    _entityUpdatesReceived = 0;
    _selfAuthorityUpdates = 0;
    _inputsQueued = 0;
    _inputsSent = 0;
    _samples = 0;
    _driftSamples = 0;
    _errors = 0;
    _lastInputSeqEcho = 0;

    _connected = false;
    _sessionStarted = false;
    _worldSnapshotReceived = false;
    _valheimStartSet = false;
    _lastValheimSet = false;
    _authorityStartSet = false;

    _inputSeq = 1;
    _lastSampleTime = 0.0f;
    _sampleAccumulator = 0.0f;
    _logAccumulator = 0.0f;

    _lastValheimSpeedMetersPerSecond = 0.0f;
    _lastLumberjacksDeltaUnits = 0.0f;
    _lastAuthorityScaledMeters = 0.0f;
    _lastDriftMeters = 0.0f;
    _maxDriftMeters = 0.0f;
    _totalDriftMeters = 0.0f;
    _lastDirectionErrorDegrees = 0.0f;

    while (_pendingInputs.TryDequeue(out _)) {
      // Drop stale inputs from a previous run.
    }

    _cts = new CancellationTokenSource();
    CancellationTokenSource runCts = _cts;
    _receiveTask = Task.Run(() => RunReceiveLoop(runCts, coordinator));
    _sendTask = Task.Run(() => RunSendLoop(_cts.Token));

    coordinator?.RecordLumberjacksShadow(BuildStatusRow("start"));
    return $"Lumberjacks shadow authority starting: {_gatewayUrl} region={_regionId}.";
  }

  public string Stop(TelemetryCoordinator coordinator = null) {
    if (!IsRunning) {
      return "Lumberjacks shadow authority is not running.";
    }

    _status = "stopping";
    try {
      _cts.Cancel();
      _socket?.Abort();
    } catch {
      // Best-effort shutdown.
    }

    coordinator?.RecordLumberjacksShadow(BuildStatusRow("stop"));
    return "Lumberjacks shadow authority stopping; no Valheim position corrections were applied.";
  }

  public void SetRouteContext(string runLabel, string routeStopId, string routePhase) {
    lock (_statsLock) {
      _runLabel = string.IsNullOrWhiteSpace(runLabel) ? "manual" : runLabel.Trim();
      _routeStopId = routeStopId ?? string.Empty;
      _routePhase = routePhase ?? string.Empty;
    }
  }

  public void Update(float deltaTime, TelemetryCoordinator coordinator) {
    using NetworkSensePerfProbe.Section section = NetworkSensePerfProbe.Measure("LumberjacksShadowAuthorityRunner.Update");

    if (!IsRunning) {
      return;
    }

    _sampleAccumulator += deltaTime;
    float inputHz = Mathf.Clamp(PluginConfig.LumberjacksShadowInputHz.Value, 1.0f, 30.0f);
    float sampleInterval = 1.0f / inputHz;
    if (_sampleAccumulator >= sampleInterval) {
      _sampleAccumulator = 0.0f;
      SampleValheimMotion();
    }

    _logAccumulator += deltaTime;
    float logInterval = Mathf.Clamp(PluginConfig.LumberjacksShadowLogIntervalSeconds.Value, 0.5f, 30.0f);
    if (_logAccumulator >= logInterval) {
      _logAccumulator = 0.0f;
      coordinator?.RecordLumberjacksShadow(BuildStatusRow("sample"));
    }
  }

  public string GetStatus() {
    lock (_statsLock) {
      float averageDrift = _driftSamples > 0 ? _totalDriftMeters / _driftSamples : 0.0f;
      return
          $"Lumberjacks shadow authority: status={_status}, connected={_connected}, session={_sessionStarted}, "
          + $"snapshot={_worldSnapshotReceived}, samples={_samples}, driftSamples={_driftSamples}, "
          + $"inputs={_inputsSent}, selfUpdates={_selfAuthorityUpdates}, "
          + $"lastDrift={_lastDriftMeters:F2}m, maxDrift={_maxDriftMeters:F2}m, avgDrift={averageDrift:F2}m, "
          + $"error={_lastError}";
    }
  }

  public IDictionary<string, object> BuildStatusRow(string eventType) {
    lock (_statsLock) {
      float averageDrift = _driftSamples > 0 ? _totalDriftMeters / _driftSamples : 0.0f;
      return new Dictionary<string, object> {
          ["event"] = eventType,
          ["gateway_url"] = _gatewayUrl,
          ["region_id"] = _regionId,
          ["run_label"] = _runLabel,
          ["route_stop_id"] = _routeStopId,
          ["route_phase"] = _routePhase,
          ["status"] = _status,
          ["connected"] = _connected,
          ["session_started"] = _sessionStarted,
          ["world_snapshot"] = _worldSnapshotReceived,
          ["player_id"] = _playerId,
          ["messages_received"] = _messagesReceived,
          ["snapshots_received"] = _snapshotsReceived,
          ["entity_updates_received"] = _entityUpdatesReceived,
          ["self_authority_updates"] = _selfAuthorityUpdates,
          ["inputs_queued"] = _inputsQueued,
          ["inputs_sent"] = _inputsSent,
          ["samples"] = _samples,
          ["drift_samples"] = _driftSamples,
          ["last_drift_meters"] = Math.Round(_lastDriftMeters, 4),
          ["max_drift_meters"] = Math.Round(_maxDriftMeters, 4),
          ["average_drift_meters"] = Math.Round(averageDrift, 4),
          ["last_valheim_speed_mps"] = Math.Round(_lastValheimSpeedMetersPerSecond, 4),
          ["last_lumberjacks_delta_units"] = Math.Round(_lastLumberjacksDeltaUnits, 4),
          ["last_authority_scaled_meters"] = Math.Round(_lastAuthorityScaledMeters, 4),
          ["last_direction_error_degrees"] = Math.Round(_lastDirectionErrorDegrees, 4),
          ["last_input_seq_echo"] = _lastInputSeqEcho,
          ["last_authority_utc"] = _lastAuthorityUtc == DateTime.MinValue ? string.Empty : _lastAuthorityUtc.ToString("o"),
          ["errors"] = _errors,
          ["last_message_type"] = _lastMessageType,
          ["last_message_utc"] = _lastMessageUtc == DateTime.MinValue ? string.Empty : _lastMessageUtc.ToString("o"),
          ["last_error"] = _lastError,
          ["claim"] = "Lumberjacks authoritative movement is shadowed and compared against Valheim local motion only; no Valheim transform corrections, ZNetView changes, or ZDO writes are applied."
      };
    }
  }

  void SampleValheimMotion() {
    Player player = Player.m_localPlayer;
    if (player == null) {
      return;
    }

    Vector3 position = ((Component) player).transform.position;
    float now = Time.unscaledTime;

    if (!_valheimStartSet) {
      _valheimStart = position;
      _lastValheimPosition = position;
      _lastSampleTime = now;
      _valheimStartSet = true;
      _lastValheimSet = true;
      return;
    }

    if (!_lastValheimSet) {
      _lastValheimPosition = position;
      _lastSampleTime = now;
      _lastValheimSet = true;
      return;
    }

    float elapsed = Mathf.Max(0.001f, now - _lastSampleTime);
    Vector3 delta = position - _lastValheimPosition;
    float horizontalMeters = HorizontalMagnitude(delta);
    float speedMetersPerSecond = horizontalMeters / elapsed;
    byte speedPercent = SpeedPercent(speedMetersPerSecond);
    byte direction = speedPercent == 0 ? (byte) 255 : DirectionByte(delta);
    byte actionFlags = ActionFlags();
    ushort sequence = _inputSeq++;

    _pendingInputs.Enqueue(new ShadowInput {
        Direction = direction,
        SpeedPercent = speedPercent,
        ActionFlags = actionFlags,
        Sequence = sequence
    });

    lock (_statsLock) {
      _samples++;
      _inputsQueued++;
      _lastValheimSpeedMetersPerSecond = speedMetersPerSecond;
      UpdateDriftLocked(position, delta);
    }

    _lastValheimPosition = position;
    _lastSampleTime = now;
  }

  void UpdateDriftLocked(Vector3 valheimPosition, Vector3 latestValheimDelta) {
    if (!_authorityStartSet) {
      return;
    }

    Vector3 valheimDelta = valheimPosition - _valheimStart;
    Vector3 authorityDelta = _latestAuthorityPosition - _authorityStart;
    float scale = AuthorityUnitsToValheimMetersScale();
    Vector3 authorityScaled = authorityDelta * scale;

    float drift = HorizontalDistance(valheimDelta, authorityScaled);
    _lastDriftMeters = drift;
    _maxDriftMeters = Mathf.Max(_maxDriftMeters, drift);
    _totalDriftMeters += drift;
    _driftSamples++;
    _lastLumberjacksDeltaUnits = HorizontalMagnitude(authorityDelta);
    _lastAuthorityScaledMeters = HorizontalMagnitude(authorityScaled);
    _lastDirectionErrorDegrees = DirectionErrorDegrees(latestValheimDelta, authorityDelta);
  }

  async Task RunReceiveLoop(CancellationTokenSource runCts, TelemetryCoordinator coordinator) {
    CancellationToken token = runCts.Token;
    ClientWebSocket socket = null;

    try {
      using ClientWebSocket localSocket = new();
      socket = localSocket;
      _socket = socket;

      using (CancellationTokenSource connectCts = new(ConnectTimeoutMs)) {
        await socket.ConnectAsync(new Uri(_gatewayUrl), connectCts.Token).ConfigureAwait(false);
      }

      SetStatus("connected", connected: true);

      while (!token.IsCancellationRequested && socket.State == WebSocketState.Open) {
        string message = await TryReceiveText(socket, token).ConfigureAwait(false);
        if (string.IsNullOrEmpty(message)) {
          continue;
        }

        string type = ExtractMessageType(message);
        NoteMessage(type);

        if (string.Equals(type, "session_started", StringComparison.OrdinalIgnoreCase)) {
          _playerId = ExtractJsonString(message, "player_id");
          _sessionStarted = true;
          _status = "session_started";
          await SendText(socket, BuildEnvelope("join_region", "{\"region_id\":\"" + JsonEscape(_regionId) + "\"}"), token)
              .ConfigureAwait(false);
        } else if (string.Equals(type, "world_snapshot", StringComparison.OrdinalIgnoreCase)) {
          _worldSnapshotReceived = true;
          Interlocked.Increment(ref _snapshotsReceived);
          if (TryExtractEntityPosition(message, _playerId, out Vector3 position)) {
            NoteAuthorityPosition(position, 0);
          }
          SetStatus("shadowing");
          coordinator?.RecordLumberjacksShadow(BuildStatusRow("world_snapshot"));
        } else if (string.Equals(type, "entity_update", StringComparison.OrdinalIgnoreCase)) {
          Interlocked.Increment(ref _entityUpdatesReceived);
          string entityId = ExtractJsonString(message, "entity_id");
          if (string.Equals(entityId, _playerId, StringComparison.OrdinalIgnoreCase)
              && TryExtractPosition(message, out Vector3 position)) {
            NoteAuthorityPosition(position, ExtractJsonInt(message, "last_input_seq"));
          }
        } else if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase)) {
          SetError(message.Length > 300 ? message.Substring(0, 300) : message);
        }
      }

      if (!token.IsCancellationRequested) {
        SetStatus("closed", connected: false);
      }
    } catch (OperationCanceledException) {
      if (ReferenceEquals(_cts, runCts)) {
        SetStatus("stopped", connected: false);
      }
    } catch (Exception exception) {
      if (ReferenceEquals(_cts, runCts)) {
        SetError(exception.GetType().Name + ": " + exception.Message);
        coordinator?.RecordLumberjacksShadow(BuildStatusRow("error"));
      }
    } finally {
      if (ReferenceEquals(_socket, socket)) {
        _socket = null;
      }
      if (ReferenceEquals(_cts, runCts) && runCts.IsCancellationRequested) {
        SetStatus("stopped", connected: false);
      }
    }
  }

  async Task RunSendLoop(CancellationToken token) {
    try {
      while (!token.IsCancellationRequested) {
        ClientWebSocket socket = _socket;
        if (socket == null || socket.State != WebSocketState.Open || !_pendingInputs.TryDequeue(out ShadowInput input)) {
          await Task.Delay(20, token).ConfigureAwait(false);
          continue;
        }

        string payload =
            "{\"direction\":" + input.Direction
            + ",\"speed_percent\":" + input.SpeedPercent
            + ",\"action_flags\":" + input.ActionFlags
            + ",\"input_seq\":" + input.Sequence
            + "}";

        await SendText(socket, BuildEnvelope("player_input", payload), token).ConfigureAwait(false);
        Interlocked.Increment(ref _inputsSent);
      }
    } catch (OperationCanceledException) {
      // Expected during shutdown.
    } catch (Exception exception) {
      SetError("send loop: " + exception.Message);
    }
  }

  void NoteAuthorityPosition(Vector3 position, int inputSeqEcho) {
    lock (_statsLock) {
      if (!_authorityStartSet) {
        _authorityStart = position;
        _authorityStartSet = true;
      }

      _latestAuthorityPosition = position;
      _selfAuthorityUpdates++;
      _lastInputSeqEcho = inputSeqEcho;
      _lastAuthorityUtc = DateTime.UtcNow;
      if (_status == "session_started" || _status == "connected") {
        _status = "shadowing";
      }
    }
  }

  byte SpeedPercent(float speedMetersPerSecond) {
    if (speedMetersPerSecond < Mathf.Max(0.0f, PluginConfig.LumberjacksShadowMinMoveMetersPerSecond.Value)) {
      return 0;
    }

    float maxValheimSpeed = Mathf.Max(0.1f, PluginConfig.LumberjacksShadowMaxValheimSpeedMetersPerSecond.Value);
    return (byte) Mathf.Clamp(Mathf.RoundToInt(speedMetersPerSecond / maxValheimSpeed * 100.0f), 0, 100);
  }

  static byte DirectionByte(Vector3 delta) {
    float degrees = Mathf.Atan2(delta.x, delta.z) * Mathf.Rad2Deg;
    if (degrees < 0.0f) {
      degrees += 360.0f;
    }

    return (byte) Mathf.Clamp(Mathf.RoundToInt(degrees / 360.0f * 255.0f), 0, 255);
  }

  static byte ActionFlags() {
    byte flags = 0;
    if (Input.GetKey(KeyCode.Space)) {
      flags |= 1;
    }
    if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) || Input.GetKey(KeyCode.C)) {
      flags |= 2;
    }
    if (Input.GetKey(KeyCode.E)) {
      flags |= 4;
    }
    return flags;
  }

  static float HorizontalMagnitude(Vector3 value) => Mathf.Sqrt(value.x * value.x + value.z * value.z);

  static float HorizontalDistance(Vector3 a, Vector3 b) {
    float dx = a.x - b.x;
    float dz = a.z - b.z;
    return Mathf.Sqrt(dx * dx + dz * dz);
  }

  static float DirectionErrorDegrees(Vector3 a, Vector3 b) {
    float aMag = HorizontalMagnitude(a);
    float bMag = HorizontalMagnitude(b);
    if (aMag < 0.001f || bMag < 0.001f) {
      return 0.0f;
    }

    float dot = Mathf.Clamp((a.x * b.x + a.z * b.z) / (aMag * bMag), -1.0f, 1.0f);
    return Mathf.Acos(dot) * Mathf.Rad2Deg;
  }

  static float AuthorityUnitsToValheimMetersScale() {
    float maxValheimSpeed = Mathf.Max(0.1f, PluginConfig.LumberjacksShadowMaxValheimSpeedMetersPerSecond.Value);
    float maxAuthoritySpeed = Mathf.Max(0.1f, PluginConfig.LumberjacksShadowLumberjacksMaxUnitsPerSecond.Value);
    return maxValheimSpeed / maxAuthoritySpeed;
  }

  static async Task SendText(ClientWebSocket socket, string text, CancellationToken token) {
    byte[] bytes = Encoding.UTF8.GetBytes(text);
    await socket.SendAsync(
        new ArraySegment<byte>(bytes),
        WebSocketMessageType.Text,
        true,
        token).ConfigureAwait(false);
  }

  static async Task<string> TryReceiveText(ClientWebSocket socket, CancellationToken token) {
    byte[] bytes = new byte[ReceiveBufferBytes];
    StringBuilder builder = new();

    WebSocketReceiveResult result;
    do {
      result = await socket.ReceiveAsync(new ArraySegment<byte>(bytes), token).ConfigureAwait(false);
      if (result.MessageType == WebSocketMessageType.Close) {
        return null;
      }

      if (result.MessageType != WebSocketMessageType.Text) {
        continue;
      }

      builder.Append(Encoding.UTF8.GetString(bytes, 0, result.Count));
    } while (!result.EndOfMessage);

    return builder.Length == 0 ? null : builder.ToString();
  }

  static bool TryExtractEntityPosition(string json, string entityId, out Vector3 position) {
    position = default;
    if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(entityId)) {
      return false;
    }

    int index = 0;
    while ((index = json.IndexOf("\"entity_id\"", index, StringComparison.OrdinalIgnoreCase)) >= 0) {
      int next = json.IndexOf("\"entity_id\"", index + 1, StringComparison.OrdinalIgnoreCase);
      string segment = next > index ? json.Substring(index, next - index) : json.Substring(index);
      string candidateId = ExtractJsonString(segment, "entity_id");
      if (string.Equals(candidateId, entityId, StringComparison.OrdinalIgnoreCase)) {
        return TryExtractPosition(segment, out position);
      }

      index += "\"entity_id\"".Length;
    }

    return false;
  }

  static bool TryExtractPosition(string json, out Vector3 position) {
    position = default;
    Match match = Regex.Match(
        json,
        "\"position\"\\s*:\\s*\\{\\s*\"x\"\\s*:\\s*(?<x>-?[0-9]+(?:\\.[0-9]+)?)\\s*,\\s*\"y\"\\s*:\\s*(?<y>-?[0-9]+(?:\\.[0-9]+)?)\\s*,\\s*\"z\"\\s*:\\s*(?<z>-?[0-9]+(?:\\.[0-9]+)?)",
        RegexOptions.IgnoreCase);
    if (!match.Success) {
      return false;
    }

    if (!TryParseFloat(match.Groups["x"].Value, out float x)
        || !TryParseFloat(match.Groups["y"].Value, out float y)
        || !TryParseFloat(match.Groups["z"].Value, out float z)) {
      return false;
    }

    position = new Vector3(x, y, z);
    return true;
  }

  static int ExtractJsonInt(string json, string propertyName) {
    Match match = Regex.Match(
        json,
        "\"" + Regex.Escape(propertyName) + "\"\\s*:\\s*(?<value>-?[0-9]+)",
        RegexOptions.IgnoreCase);
    return match.Success && int.TryParse(match.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
        ? value
        : 0;
  }

  static bool TryParseFloat(string value, out float result) =>
      float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

  static string BuildEnvelope(string type, string payloadJson) {
    return
        "{\"version\":1,"
        + "\"type\":\"" + JsonEscape(type) + "\","
        + "\"seq\":" + DateTime.UtcNow.Ticks + ","
        + "\"timestamp\":\"" + DateTimeOffset.UtcNow.ToString("o") + "\","
        + "\"payload\":" + payloadJson
        + "}";
  }

  static string ExtractMessageType(string json) => ExtractJsonString(json, "type");

  static string ExtractJsonString(string json, string propertyName) {
    if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(propertyName)) {
      return string.Empty;
    }

    Match match = Regex.Match(
        json,
        "\"" + Regex.Escape(propertyName) + "\"\\s*:\\s*\"(?<value>(?:\\\\.|[^\"])*)\"",
        RegexOptions.IgnoreCase);
    return match.Success ? match.Groups["value"].Value.Replace("\\\"", "\"").Replace("\\\\", "\\") : string.Empty;
  }

  static string NormalizeGatewayUrl(string gatewayUrl) {
    string env = Environment.GetEnvironmentVariable("COMFY_LUMBERJACKS_GATEWAY_URL");
    string value = !string.IsNullOrWhiteSpace(env) ? env : gatewayUrl;
    if (string.IsNullOrWhiteSpace(value)) {
      value = "ws://127.0.0.1:4000";
    }

    value = value.Trim();
    if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) {
      value = "ws://" + value.Substring("http://".Length);
    } else if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) {
      value = "wss://" + value.Substring("https://".Length);
    }

    return value;
  }

  static string JsonEscape(string value) {
    if (string.IsNullOrEmpty(value)) {
      return string.Empty;
    }

    return value
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("\r", "\\r")
        .Replace("\n", "\\n")
        .Replace("\t", "\\t");
  }

  void NoteMessage(string type) {
    lock (_statsLock) {
      _messagesReceived++;
      _lastMessageType = type;
      _lastMessageUtc = DateTime.UtcNow;
    }
  }

  void SetStatus(string status, bool? connected = null) {
    lock (_statsLock) {
      _status = status;
      if (connected.HasValue) {
        _connected = connected.Value;
      }
    }
  }

  void SetError(string error) {
    lock (_statsLock) {
      _status = "error";
      _lastError = error;
      _errors++;
    }
  }

  public void Dispose() {
    Stop();
    _cts?.Dispose();
    _cts = null;
  }

  sealed class ShadowInput {
    public byte Direction;
    public byte SpeedPercent;
    public byte ActionFlags;
    public ushort Sequence;
  }
}
