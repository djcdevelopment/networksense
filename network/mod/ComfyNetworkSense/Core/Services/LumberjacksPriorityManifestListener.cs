namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

public sealed class LumberjacksPriorityManifestListener : IDisposable {
  const int ConnectTimeoutMs = 5000;
  const int ReceiveBufferBytes = 65536;

  readonly object _statsLock = new();

  const int UdpTokenBytes = 8;
  const int UdpHeaderBytes = 6;
  const int PriorityManifestObjectTypeId = 17;

  CancellationTokenSource _cts;
  Task _receiveTask;
  ClientWebSocket _socket;
  UdpClient _udpClient;
  int _datagramObjectsReceived;

  string _gatewayUrl = "ws://127.0.0.1:4000";
  string _regionId = "region-spawn";
  string _playerId = string.Empty;
  string _status = "idle";
  string _lastError = string.Empty;
  string _lastManifestId = string.Empty;

  int _messagesReceived;
  int _manifestsReceived;
  int _lastTotalInputObjects;
  int _lastUniqueObjects;
  int _lastReliableCount;
  int _lastDatagramCount;
  int _lastDeferredCount;
  int _errors;
  bool _connected;
  bool _sessionStarted;

  public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;

  public string Start(string gatewayUrl, string regionId, TelemetryCoordinator coordinator) {
    if (IsRunning) {
      return $"Lumberjacks priority manifest listener already running: {GetStatus()}";
    }

    _gatewayUrl = NormalizeGatewayUrl(gatewayUrl);
    _regionId = string.IsNullOrWhiteSpace(regionId) ? "region-spawn" : regionId.Trim();
    _status = "starting";
    _lastError = string.Empty;
    _lastManifestId = string.Empty;
    _messagesReceived = 0;
    _manifestsReceived = 0;
    _datagramObjectsReceived = 0;
    _lastTotalInputObjects = 0;
    _lastUniqueObjects = 0;
    _lastReliableCount = 0;
    _lastDatagramCount = 0;
    _lastDeferredCount = 0;
    _errors = 0;
    _connected = false;
    _sessionStarted = false;

    _cts = new CancellationTokenSource();
    _receiveTask = Task.Run(() => RunReceiveLoop(_cts.Token, coordinator));

    coordinator?.RecordLumberjacksPriorityManifestListen(BuildStatusRow("start"));
    return $"Lumberjacks priority manifest listener starting: {_gatewayUrl} region={_regionId}.";
  }

  public string Stop(TelemetryCoordinator coordinator = null) {
    if (!IsRunning) {
      return "Lumberjacks priority manifest listener is not running.";
    }

    _status = "stopping";
    try {
      _cts.Cancel();
      _socket?.Abort();
      _udpClient?.Close();
    } catch {
      // Best-effort shutdown.
    }

    coordinator?.RecordLumberjacksPriorityManifestListen(BuildStatusRow("stop"));
    return "Lumberjacks priority manifest listener stopping.";
  }

  public string GetStatus() {
    lock (_statsLock) {
      return
          $"Lumberjacks priority manifest listener: status={_status}, connected={_connected}, session={_sessionStarted}, "
          + $"messages={_messagesReceived}, manifests={_manifestsReceived}, last_manifest={_lastManifestId}, "
          + $"total_input_objects={_lastTotalInputObjects}, unique_objects={_lastUniqueObjects}, "
          + $"reliable={_lastReliableCount}, datagram={_lastDatagramCount}, "
          + $"datagram_received={Volatile.Read(ref _datagramObjectsReceived)}, deferred={_lastDeferredCount}, error={_lastError}";
    }
  }

  public IDictionary<string, object> BuildStatusRow(string eventType) {
    lock (_statsLock) {
      return new Dictionary<string, object> {
          ["event"] = eventType,
          ["gateway_url"] = _gatewayUrl,
          ["region_id"] = _regionId,
          ["status"] = _status,
          ["connected"] = _connected,
          ["session_started"] = _sessionStarted,
          ["player_id"] = _playerId,
          ["messages_received"] = _messagesReceived,
          ["manifests_received"] = _manifestsReceived,
          ["manifest_id"] = _lastManifestId,
          ["total_input_objects"] = _lastTotalInputObjects,
          ["unique_objects"] = _lastUniqueObjects,
          ["reliable_count"] = _lastReliableCount,
          ["datagram_count"] = _lastDatagramCount,
          ["datagram_objects_received"] = Volatile.Read(ref _datagramObjectsReceived),
          ["deferred_count"] = _lastDeferredCount,
          ["errors"] = _errors,
          ["last_error"] = _lastError,
          ["claim"] = "Observes both the reliable priority_manifest broadcast and the datagram-lane priority_manifest_object frames; does not write ZDOs, claim object ownership, or alter vanilla replication."
      };
    }
  }

  async Task RunReceiveLoop(CancellationToken token, TelemetryCoordinator coordinator) {
    try {
      using ClientWebSocket socket = new();
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
        NoteMessage();

        if (string.Equals(type, "session_started", StringComparison.OrdinalIgnoreCase)) {
          _playerId = ExtractJsonString(message, "player_id");
          _sessionStarted = true;
          _status = "listening";
          await SendText(socket, BuildEnvelope("join_region", "{\"region_id\":\"" + JsonEscape(_regionId) + "\"}"), token)
              .ConfigureAwait(false);

          string udpTokenText = ExtractJsonString(message, "udp_token");
          int udpPort = ExtractJsonInt(message, "udp_port");
          if (ulong.TryParse(udpTokenText, out ulong udpToken) && udpPort > 0) {
            string host = TryGetHost(_gatewayUrl, out string parsedHost) ? parsedHost : "127.0.0.1";
            _ = Task.Run(() => RunUdpListener(host, udpPort, udpToken, token), token);
          }
        } else if (string.Equals(type, "priority_manifest", StringComparison.OrdinalIgnoreCase)) {
          Interlocked.Increment(ref _manifestsReceived);
          _lastManifestId = ExtractJsonString(message, "manifest_id");
          _lastTotalInputObjects = ExtractJsonInt(message, "total_input_objects");
          _lastUniqueObjects = ExtractJsonInt(message, "unique_objects");
          _lastReliableCount = ExtractJsonInt(message, "reliable_count");
          _lastDatagramCount = ExtractJsonInt(message, "datagram_count");
          _lastDeferredCount = ExtractJsonInt(message, "deferred_count");
          coordinator?.RecordLumberjacksPriorityManifestListen(BuildStatusRow("priority_manifest"));
          _ = Task.Run(() => ReportDatagramSummaryAfterDelay(coordinator, token), token);
        } else if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase)) {
          SetError(message.Length > 300 ? message.Substring(0, 300) : message);
        }
      }

      if (!token.IsCancellationRequested) {
        SetStatus("closed", connected: false);
      }
    } catch (OperationCanceledException) {
      SetStatus("stopped", connected: false);
    } catch (Exception exception) {
      SetError(exception.GetType().Name + ": " + exception.Message);
      coordinator?.RecordLumberjacksPriorityManifestListen(BuildStatusRow("error"));
    } finally {
      _socket = null;
      if (_cts != null && _cts.IsCancellationRequested) {
        SetStatus("stopped", connected: false);
      }
    }
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

  async Task RunUdpListener(string host, int port, ulong udpToken, CancellationToken token) {
    UdpClient client = null;
    try {
      client = new UdpClient();
      byte[] handshake = new byte[UdpTokenBytes + UdpHeaderBytes];
      Array.Copy(BitConverter.GetBytes(udpToken), handshake, UdpTokenBytes);
      await client.SendAsync(handshake, handshake.Length, host, port).ConfigureAwait(false);
      _udpClient = client;

      while (!token.IsCancellationRequested) {
        Task<UdpReceiveResult> receiveTask = client.ReceiveAsync();
        Task finished = await Task.WhenAny(receiveTask, Task.Delay(500, token)).ConfigureAwait(false);
        if (finished != receiveTask) {
          continue;
        }

        UdpReceiveResult result;
        try {
          result = await receiveTask.ConfigureAwait(false);
        } catch (ObjectDisposedException) {
          break;
        } catch (SocketException) {
          continue;
        }

        byte[] data = result.Buffer;
        if (data.Length < UdpTokenBytes + UdpHeaderBytes) {
          continue;
        }

        ulong headerValue = 0;
        for (int i = 0; i < UdpHeaderBytes; i++) {
          headerValue = (headerValue << 8) | data[UdpTokenBytes + i];
        }
        int typeId = (int) ((headerValue >> 38) & 0x3F);
        if (typeId != PriorityManifestObjectTypeId) {
          continue;
        }

        Interlocked.Increment(ref _datagramObjectsReceived);
      }
    } catch (OperationCanceledException) {
      // Expected during listener shutdown.
    } catch (Exception exception) {
      SetError("udp: " + exception.GetType().Name + ": " + exception.Message);
    } finally {
      client?.Close();
      if (ReferenceEquals(_udpClient, client)) {
        _udpClient = null;
      }
    }
  }

  async Task ReportDatagramSummaryAfterDelay(TelemetryCoordinator coordinator, CancellationToken token) {
    try {
      await Task.Delay(3000, token).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      return;
    }

    coordinator?.RecordLumberjacksPriorityManifestListen(BuildStatusRow("datagram_summary"));
  }

  static bool TryGetHost(string gatewayUrl, out string host) {
    try {
      host = new Uri(gatewayUrl).Host;
      return !string.IsNullOrWhiteSpace(host);
    } catch {
      host = null;
      return false;
    }
  }

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

  static int ExtractJsonInt(string json, string propertyName) {
    if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(propertyName)) {
      return 0;
    }

    Match match = Regex.Match(
        json,
        "\"" + Regex.Escape(propertyName) + "\"\\s*:\\s*(?<value>-?[0-9]+)",
        RegexOptions.IgnoreCase);
    return match.Success && int.TryParse(match.Groups["value"].Value, out int value) ? value : 0;
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

  void NoteMessage() {
    lock (_statsLock) {
      _messagesReceived++;
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
}
