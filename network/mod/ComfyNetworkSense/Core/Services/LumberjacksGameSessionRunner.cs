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
  const string ReceiptFileName = "lumberjacks-game-session.jsonl";

  readonly object _gate = new();
  readonly ConcurrentQueue<SessionEvent> _events = new();
  readonly ConcurrentQueue<string> _outbound = new();
  readonly TelemetryLogWriter _writer = new();

  CancellationTokenSource _cts;
  Task _connectionTask;
  ClientWebSocket _socket;
  UdpClient _udp;
  SessionProbe _probe;
  string _state = "idle";
  string _lastError = string.Empty;
  string _serverInstanceId = string.Empty;
  string _worldId = string.Empty;
  string _connectionId = string.Empty;
  string _resumeToken = string.Empty;
  long _resumeEpoch = -1;
  long _lastServerSequence;
  long _nextClientSequence;
  int _outboundCount;
  float _nextConnectAt;
  bool _webSocketConnected;
  bool _udpReady;
  bool _disposed;

  public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;
  public bool WebSocketConnected { get { lock (_gate) return _webSocketConnected; } }
  public bool UdpReady { get { lock (_gate) return _udpReady; } }
  public string State { get { lock (_gate) return _state; } }
  public string LastError { get { lock (_gate) return _lastError; } }

  public void Update(float now) {
    if (_disposed) return;
    if (!ShouldRun()) {
      if (IsRunning) Stop();
      return;
    }
    if (!IsRunning && now >= _nextConnectAt) Start(now);
    DrainEvents();
    EvaluateProbeDeadline(now);
  }

  public bool BeginProbe(
      string actionId,
      string mode,
      float deadlineSeconds,
      out string detail) {
    detail = string.Empty;
    if (!SafeToken(actionId, 80) || mode is not ("resume" or "withhold_receipt")) {
      detail = "probe_parameters_invalid";
      return false;
    }

    string runId = NativeAutotestRequest.ActiveRunId;
    if (!SafeToken(runId, 80)) {
      detail = "native_autotest_run_missing";
      return false;
    }

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
          InitialResumeEpoch = _resumeEpoch
      };
      if (!TryQueue(BuildEnvelope(
              "valheim_session_probe",
              NextClientSequence(),
              "\"run_id\":\"" + Escape(runId)
              + "\",\"probe_id\":\"" + Escape(_probe.ProbeId)
              + "\",\"mode\":\"" + Escape(mode) + "\""))) {
        _probe = null;
        detail = "client_send_queue_full";
        return false;
      }
      WriteReceipt("probe_started", actionId,
          "mode=" + mode + " connection_id=" + _connectionId
          + " resume_epoch=" + _resumeEpoch);
      return true;
    }
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
          ["resume_epoch"] = _resumeEpoch,
          ["last_server_sequence"] = _lastServerSequence,
          ["next_client_sequence"] = _nextClientSequence,
          ["outbound_queue_depth"] = Volatile.Read(ref _outboundCount),
          ["probe_state"] = _probe == null
              ? "none"
              : (_probe.Terminal ? (_probe.Success ? "passed" : "failed") : "running"),
          ["probe_detail"] = _probe?.Detail ?? string.Empty,
          ["last_error"] = _lastError
      };
    }
  }

  void Start(float now) {
    Stop();
    _nextConnectAt = now + 2.0f;
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
    SetState("idle", false, false, string.Empty);
  }

  async Task RunConnections(CancellationToken token) {
    while (!token.IsCancellationRequested) {
      try {
        await RunOneConnection(token).ConfigureAwait(false);
      } catch (OperationCanceledException) when (token.IsCancellationRequested) {
        break;
      } catch (Exception exception) {
        _events.Enqueue(new SessionEvent(
            "connection_error", 0, string.Empty,
            exception.GetType().Name + ":" + exception.Message));
      } finally {
        try { _udp?.Close(); } catch { }
        _udp = null;
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
    byte[] buffer = new byte[ReceiveBufferBytes];
    try {
      while (!token.IsCancellationRequested && socket.State == WebSocketState.Open) {
        ReceivedFrame frame = await ReceiveFrame(socket, buffer, token).ConfigureAwait(false);
        if (frame == null) break;
        if (frame.Type != WebSocketMessageType.Text) continue;
        string text = Encoding.UTF8.GetString(frame.Data);
        string type = ExtractJsonString(text, "type");
        if (string.Equals(type, "session_started", StringComparison.OrdinalIgnoreCase)) {
          HandleSessionStartedWorker(text);
          continue;
        }
        if (string.Equals(type, "valheim_control_request", StringComparison.OrdinalIgnoreCase)) {
          if (HandleControlRequestWorker(text, socket)) break;
          continue;
        }
        if (string.Equals(type, "valheim_control_receipt", StringComparison.OrdinalIgnoreCase)) {
          HandleControlReceiptWorker(text);
        }
      }
    } finally {
      connectionCts.Cancel();
      try { await sender.ConfigureAwait(false); } catch { }
    }
  }

  async Task RunOutgoing(ClientWebSocket socket, CancellationToken token) {
    while (!token.IsCancellationRequested && socket.State == WebSocketState.Open) {
      if (!_outbound.TryDequeue(out string text)) {
        await Task.Delay(10, token).ConfigureAwait(false);
        continue;
      }
      Interlocked.Decrement(ref _outboundCount);
      byte[] bytes = Encoding.UTF8.GetBytes(text);
      await socket.SendAsync(
          new ArraySegment<byte>(bytes),
          WebSocketMessageType.Text,
          true,
          token).ConfigureAwait(false);
    }
  }

  void HandleSessionStartedWorker(string text) {
    string serverInstance = ExtractJsonString(text, "server_instance_id");
    string world = ExtractJsonString(text, "world_id");
    string connection = ExtractJsonString(text, "client_connection_id");
    string resumeToken = ExtractJsonString(text, "resume_token");
    long epoch = ExtractJsonLong(text, "resume_epoch");
    int udpPort = (int)ExtractJsonLong(text, "udp_port");
    string udpToken = ExtractJsonString(text, "udp_token");
    bool resumed = ExtractJsonBool(text, "resumed");
    if (!SafeToken(connection, 80) || string.IsNullOrWhiteSpace(resumeToken)
        || string.IsNullOrWhiteSpace(serverInstance) || string.IsNullOrWhiteSpace(world)) {
      throw new InvalidDataException("session_started missing durable game-session fields");
    }

    string previousConnection;
    long previousEpoch;
    lock (_gate) {
      previousConnection = _connectionId;
      previousEpoch = _resumeEpoch;
      _serverInstanceId = serverInstance;
      _worldId = world;
      _connectionId = connection;
      _resumeToken = resumeToken;
      _resumeEpoch = epoch;
    }
    if (!string.IsNullOrEmpty(previousConnection)
        && (!resumed || !string.Equals(previousConnection, connection, StringComparison.Ordinal)
            || epoch <= previousEpoch)) {
      throw new InvalidDataException("resume did not preserve connection id and advance epoch");
    }

    try {
      _udp?.Close();
      _udp = null;
      if (udpPort > 0 && ulong.TryParse(udpToken, out _)) {
        UdpClient udp = new();
        udp.Connect(new Uri(NormalizeGatewayUrl()).Host, udpPort);
        _udp = udp;
      }
    } catch {
      _udp = null;
    }
    _events.Enqueue(new SessionEvent(
        "session_started", epoch, connection,
        "resumed=" + (resumed ? "true" : "false")
        + " server_instance_id=" + serverInstance
        + " world_id=" + world
        + " udp_ready=" + (_udp != null ? "true" : "false")));
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

    TryQueue(BuildEnvelope(
        "reliable_ack",
        NextClientSequence(),
        "\"through_sequence\":" + sequence.ToString(CultureInfo.InvariantCulture)));
    if (shouldRespond) {
      long clientSequence = NextClientSequence();
      TryQueue(BuildEnvelope(
          "valheim_control_response",
          clientSequence,
          "\"probe_id\":\"" + Escape(probeId)
          + "\",\"request_sequence\":" + sequence.ToString(CultureInfo.InvariantCulture)
          + ",\"client_sequence\":" + clientSequence.ToString(CultureInfo.InvariantCulture)));
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
    TryQueue(BuildEnvelope(
        "reliable_ack",
        NextClientSequence(),
        "\"through_sequence\":" + sequence.ToString(CultureInfo.InvariantCulture)));
    _events.Enqueue(new SessionEvent(
        "probe_passed", sequence, connectionId,
        "request_sequence=" + requestSequence
        + " response_count=" + responseCount
        + " resume_epoch=" + epoch));
  }

  void DrainEvents() {
    while (_events.TryDequeue(out SessionEvent item)) {
      switch (item.Kind) {
        case "session_started":
          SetState("connected", true, _udp != null, string.Empty);
          break;
        case "socket_closed":
          SetState("reconnecting", false, false, string.Empty);
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
      }
      WriteReceipt(item.Kind, _probe?.ActionId ?? string.Empty,
          "sequence=" + item.Sequence + " connection_id=" + item.ConnectionId
          + (string.IsNullOrEmpty(item.Detail) ? string.Empty : " " + item.Detail));
    }
  }

  void EvaluateProbeDeadline(float now) {
    lock (_gate) {
      if (_probe == null || _probe.Terminal || now <= _probe.DeadlineAt) return;
      _probe.Terminal = true;
      if (_probe.Mode == "withhold_receipt" && _probe.ResponseSent) {
        _probe.Success = true;
        _probe.Detail = "bounded_receipt_timeout_no_native_fallback";
        WriteReceipt("expected_timeout", _probe.ActionId, _probe.Detail);
      } else {
        _probe.Success = false;
        _probe.Detail = "probe_deadline_exceeded";
        WriteReceipt("probe_failed", _probe.ActionId, _probe.Detail);
      }
    }
  }

  bool TryQueue(string frame) {
    int depth = Interlocked.Increment(ref _outboundCount);
    if (depth > MaxOutboundFrames) {
      Interlocked.Decrement(ref _outboundCount);
      return false;
    }
    _outbound.Enqueue(frame);
    return true;
  }

  long NextClientSequence() {
    lock (_gate) return ++_nextClientSequence;
  }

  bool ShouldRun() {
    if (PluginConfig.LumberjacksGameSessionEnabled?.Value != true) return false;
    if (ZNet.instance == null || ZNet.instance.IsServer() || Player.m_localPlayer == null)
      return false;
    return !string.IsNullOrWhiteSpace(PluginConfig.LumberjacksEnrollmentId.Value)
        && !string.IsNullOrWhiteSpace(PluginConfig.LumberjacksClientAccessKey.Value);
  }

  string GatewayUrl(string resumeToken) {
    string value = NormalizeGatewayUrl();
    if (string.IsNullOrEmpty(resumeToken)) return value;
    return value + (value.Contains("?") ? "&" : "?")
        + "resume=" + Uri.EscapeDataString(resumeToken);
  }

  static string NormalizeGatewayUrl() {
    string value = NativeAutotestRequest.ActiveGatewayUrl;
    if (string.IsNullOrWhiteSpace(value))
      value = Environment.GetEnvironmentVariable("COMFY_LUMBERJACKS_GATEWAY_URL");
    if (string.IsNullOrWhiteSpace(value)) value = PluginConfig.LumberjacksGatewayUrl.Value;
    value = (value ?? "ws://127.0.0.1:4000").Trim();
    if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
      return "wss://" + value.Substring(8);
    if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
      return "ws://" + value.Substring(7);
    return value;
  }

  static string BuildEnvelope(string type, long sequence, string payloadFields) =>
      "{\"version\":1,\"type\":\"" + Escape(type)
      + "\",\"seq\":" + sequence.ToString(CultureInfo.InvariantCulture)
      + ",\"timestamp\":\"" + DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture)
      + "\",\"payload\":{" + payloadFields + "}}";

  void WriteReceipt(string state, string actionId, string detail) {
    Dictionary<string, object> row = new() {
        ["schema_version"] = 1,
        ["timestamp_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        ["state"] = state,
        ["run_id"] = NativeAutotestRequest.ActiveRunId,
        ["client"] = NativeAutotestRequest.ActiveClient,
        ["action_id"] = actionId ?? string.Empty,
        ["connection_id"] = _connectionId,
        ["resume_epoch"] = _resumeEpoch,
        ["detail"] = detail ?? string.Empty
    };
    _writer.Write(ReceiptFileName, row);
    ComfyNetworkSense.LogInfo(
        "LUMBERJACKS_SESSION state=" + SafeMarker(state)
        + " run_id=" + SafeMarker(NativeAutotestRequest.ActiveRunId)
        + " client=" + SafeMarker(NativeAutotestRequest.ActiveClient)
        + " action=" + SafeMarker(actionId)
        + " detail=" + SafeMarker(detail));
  }

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
    return match.Success ? match.Groups["value"].Value : string.Empty;
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
    _writer.Dispose();
  }

  sealed class SessionProbe {
    public string ActionId;
    public string ProbeId;
    public string RunId;
    public string Mode;
    public string InitialConnectionId;
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

  sealed class SessionEvent {
    public readonly string Kind;
    public readonly long Sequence;
    public readonly string ConnectionId;
    public readonly string Detail;
    public SessionEvent(string kind, long sequence, string connectionId, string detail) {
      Kind = kind;
      Sequence = sequence;
      ConnectionId = connectionId;
      Detail = detail;
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
}
