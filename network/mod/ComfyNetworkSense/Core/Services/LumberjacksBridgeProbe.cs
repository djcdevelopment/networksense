namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

public sealed class LumberjacksBridgeProbe {
  const int ConnectTimeoutMs = 5000;
  const int ReceiveTimeoutMs = 10000;
  const int PostInputReceiveMs = 3500;

  int _running;

  public bool IsRunning => Interlocked.CompareExchange(ref _running, 0, 0) == 1;

  public string Start(string gatewayUrl, string regionId, int inputCount, TelemetryCoordinator coordinator) {
    if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) {
      return "Lumberjacks bridge probe is already running.";
    }

    gatewayUrl = NormalizeGatewayUrl(gatewayUrl);
    regionId = string.IsNullOrWhiteSpace(regionId) ? "region-spawn" : regionId.Trim();
    inputCount = Math.Max(0, Math.Min(inputCount, 200));

    _ = Task.Run(async () => {
      BridgeProbeResult result;
      try {
        result = await RunAsync(gatewayUrl, regionId, inputCount).ConfigureAwait(false);
      } catch (Exception exception) {
        result = BridgeProbeResult.Failed(gatewayUrl, regionId, exception);
      } finally {
        Interlocked.Exchange(ref _running, 0);
      }

      coordinator?.RecordLumberjacksBridgeProbe(result.ToDictionary());

      string message =
          $"Lumberjacks bridge probe {result.Status}: connected={result.Connected}, "
          + $"session={result.SessionStarted}, snapshot={result.WorldSnapshot}, "
          + $"inputs={result.InputsSent}, updates={result.EntityUpdates}, error={result.ErrorMessage}";
      ComfyNetworkSense.EnqueueMainThreadMessage(message);
      ComfyNetworkSense.LogInfo(message);
    });

    return $"Lumberjacks bridge probe started: {gatewayUrl} region={regionId} inputs={inputCount}.";
  }

  static async Task<BridgeProbeResult> RunAsync(string gatewayUrl, string regionId, int inputCount) {
    BridgeProbeResult result = new() {
        GatewayUrl = gatewayUrl,
        RegionId = regionId,
        InputTarget = inputCount,
        StartedUtc = DateTime.UtcNow.ToString("o")
    };

    using ClientWebSocket socket = new();

    using (CancellationTokenSource connectCts = new(ConnectTimeoutMs)) {
      await socket.ConnectAsync(new Uri(gatewayUrl), connectCts.Token).ConfigureAwait(false);
    }

    result.Connected = true;

    string sessionMessage = await ReceiveUntilType(socket, "session_started", ReceiveTimeoutMs).ConfigureAwait(false);
    result.SessionStarted = !string.IsNullOrEmpty(sessionMessage);
    result.PlayerId = ExtractJsonString(sessionMessage, "player_id");
    result.UdpTokenSeen = !string.IsNullOrEmpty(ExtractJsonString(sessionMessage, "udp_token"));

    await SendText(socket, BuildEnvelope("join_region", "{\"region_id\":\"" + JsonEscape(regionId) + "\"}")).ConfigureAwait(false);

    string snapshotMessage = await ReceiveUntilType(socket, "world_snapshot", ReceiveTimeoutMs).ConfigureAwait(false);
    result.WorldSnapshot = !string.IsNullOrEmpty(snapshotMessage);
    result.WorldSnapshotBytes = snapshotMessage?.Length ?? 0;
    result.InitialEntityCount = CountOccurrences(snapshotMessage, "\"entity_id\"");

    ushort sequence = 1;
    for (int index = 0; index < inputCount; index++) {
      byte direction = (byte) ((index * 23) % 255);
      byte speed = 80;
      string payload =
          "{\"direction\":" + direction
          + ",\"speed_percent\":" + speed
          + ",\"action_flags\":0"
          + ",\"input_seq\":" + sequence
          + "}";
      await SendText(socket, BuildEnvelope("player_input", payload)).ConfigureAwait(false);
      result.InputsSent++;
      sequence++;
      await Task.Delay(50).ConfigureAwait(false);
    }

    DateTime stopAt = DateTime.UtcNow.AddMilliseconds(PostInputReceiveMs);
    while (DateTime.UtcNow < stopAt && socket.State == WebSocketState.Open) {
      string message = await TryReceiveText(socket, 500).ConfigureAwait(false);
      if (string.IsNullOrEmpty(message)) {
        continue;
      }

      string type = ExtractMessageType(message);
      if (string.Equals(type, "entity_update", StringComparison.OrdinalIgnoreCase)) {
        result.EntityUpdates++;
      } else if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase)) {
        result.Errors++;
        if (string.IsNullOrEmpty(result.ErrorMessage)) {
          result.ErrorMessage = message.Length > 300 ? message.Substring(0, 300) : message;
        }
      }
    }

    try {
      if (socket.State == WebSocketState.Open) {
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "probe complete", CancellationToken.None)
            .ConfigureAwait(false);
      }
    } catch {
      // Best-effort close during a diagnostic probe.
    }

    result.CompletedUtc = DateTime.UtcNow.ToString("o");
    result.Status = result.Connected && result.SessionStarted && result.WorldSnapshot && result.InputsSent == inputCount
        ? (result.EntityUpdates > 0 ? "pass_sidecar_protocol" : "partial_no_entity_update")
        : "fail_protocol";
    result.IntegrationClaim = "Valheim plugin process can act as a Lumberjacks JSON WebSocket protocol client.";
    result.NotProven = "Valheim ZDO transport replacement; Valheim physics authority replacement; Steam/PlayFab socket replacement.";
    return result;
  }

  static async Task SendText(ClientWebSocket socket, string text) {
    byte[] bytes = Encoding.UTF8.GetBytes(text);
    await socket.SendAsync(
        new ArraySegment<byte>(bytes),
        WebSocketMessageType.Text,
        true,
        CancellationToken.None).ConfigureAwait(false);
  }

  static async Task<string> ReceiveUntilType(ClientWebSocket socket, string expectedType, int timeoutMs) {
    DateTime stopAt = DateTime.UtcNow.AddMilliseconds(timeoutMs);
    while (DateTime.UtcNow < stopAt && socket.State == WebSocketState.Open) {
      string message = await TryReceiveText(socket, 500).ConfigureAwait(false);
      if (string.IsNullOrEmpty(message)) {
        continue;
      }

      if (string.Equals(ExtractMessageType(message), expectedType, StringComparison.OrdinalIgnoreCase)) {
        return message;
      }
    }

    return null;
  }

  static async Task<string> TryReceiveText(ClientWebSocket socket, int timeoutMs) {
    byte[] bytes = new byte[32768];
    StringBuilder builder = new();

    using CancellationTokenSource cts = new(timeoutMs);
    try {
      WebSocketReceiveResult result;
      do {
        result = await socket.ReceiveAsync(new ArraySegment<byte>(bytes), cts.Token).ConfigureAwait(false);
        if (result.MessageType == WebSocketMessageType.Close) {
          return null;
        }

        if (result.MessageType != WebSocketMessageType.Text) {
          continue;
        }

        builder.Append(Encoding.UTF8.GetString(bytes, 0, result.Count));
      } while (!result.EndOfMessage);
    } catch (OperationCanceledException) {
      return null;
    } catch (WebSocketException exception) {
      return "{\"type\":\"error\",\"payload\":{\"message\":\"" + JsonEscape(exception.Message) + "\"}}";
    }

    return builder.Length == 0 ? null : builder.ToString();
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

  static int CountOccurrences(string value, string needle) {
    if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(needle)) {
      return 0;
    }

    int count = 0;
    int index = 0;
    while ((index = value.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase)) >= 0) {
      count++;
      index += needle.Length;
    }
    return count;
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

  sealed class BridgeProbeResult {
    public string GatewayUrl;
    public string RegionId;
    public int InputTarget;
    public string StartedUtc;
    public string CompletedUtc;
    public string Status = "fail_exception";
    public bool Connected;
    public bool SessionStarted;
    public bool UdpTokenSeen;
    public string PlayerId = string.Empty;
    public bool WorldSnapshot;
    public int WorldSnapshotBytes;
    public int InitialEntityCount;
    public int InputsSent;
    public int EntityUpdates;
    public int Errors;
    public string ErrorMessage = string.Empty;
    public string IntegrationClaim = string.Empty;
    public string NotProven = string.Empty;

    public static BridgeProbeResult Failed(string gatewayUrl, string regionId, Exception exception) {
      return new BridgeProbeResult {
          GatewayUrl = gatewayUrl,
          RegionId = regionId,
          StartedUtc = DateTime.UtcNow.ToString("o"),
          CompletedUtc = DateTime.UtcNow.ToString("o"),
          Status = "fail_exception",
          ErrorMessage = exception.GetType().Name + ": " + exception.Message,
          IntegrationClaim = "No live Lumberjacks bridge claim; probe failed before protocol completion.",
          NotProven = "Valheim ZDO transport replacement; Valheim physics authority replacement; Steam/PlayFab socket replacement."
      };
    }

    public IDictionary<string, object> ToDictionary() {
      return new Dictionary<string, object> {
          ["gateway_url"] = GatewayUrl,
          ["region_id"] = RegionId,
          ["input_target"] = InputTarget,
          ["started_utc"] = StartedUtc,
          ["completed_utc"] = CompletedUtc,
          ["status"] = Status,
          ["connected"] = Connected,
          ["session_started"] = SessionStarted,
          ["udp_token_seen"] = UdpTokenSeen,
          ["player_id"] = PlayerId,
          ["world_snapshot"] = WorldSnapshot,
          ["world_snapshot_bytes"] = WorldSnapshotBytes,
          ["initial_entity_count"] = InitialEntityCount,
          ["inputs_sent"] = InputsSent,
          ["entity_updates"] = EntityUpdates,
          ["errors"] = Errors,
          ["error_message"] = ErrorMessage,
          ["integration_claim"] = IntegrationClaim,
          ["not_proven"] = NotProven
      };
    }
  }
}
