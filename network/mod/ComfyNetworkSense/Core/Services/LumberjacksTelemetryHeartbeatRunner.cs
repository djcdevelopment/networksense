namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using UnityEngine;

/// <summary>Publishes an aggregate, identity-free server heartbeat to Lumberjacks.</summary>
public sealed class LumberjacksTelemetryHeartbeatRunner : IDisposable {
  int _inFlight;
  float _lastSentAt = -10000.0f;
  string _lastError = string.Empty;

  public void Update(float deltaTime, TelemetryCoordinator coordinator) {
    if (!PluginConfig.LumberjacksTelemetryHeartbeatEnabled.Value ||
        !IsDedicatedServer() ||
        Time.unscaledTime - _lastSentAt < Math.Max(1.0f, PluginConfig.LumberjacksTelemetryHeartbeatIntervalSeconds.Value) ||
        Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0) {
      return;
    }

    _lastSentAt = Time.unscaledTime;
    Dictionary<string, object> payload = coordinator.GetLumberjacksTelemetryHeartbeat();
    _ = Task.Run(() => Post(payload));
  }

  void Post(Dictionary<string, object> payload) {
    try {
      string endpoint = NormalizeEndpoint(PluginConfig.LumberjacksGatewayUrl.Value);
      SendHttpPostViaSocket(endpoint + "/valheim/telemetry/heartbeat",
          JsonLineSerializer.Serialize(payload), PluginConfig.LumberjacksTelemetryKey.Value);
      _lastError = string.Empty;
    } catch (Exception exception) {
      string message = exception.GetType().Name + ": " + exception.Message;
      if (!string.Equals(message, _lastError, StringComparison.Ordinal)) {
        _lastError = message;
        ComfyNetworkSense.LogWarning("Lumberjacks telemetry heartbeat failed: " + message);
      }
    } finally {
      Interlocked.Exchange(ref _inFlight, 0);
    }
  }

  static bool IsDedicatedServer() => ZNet.instance != null && ZNet.instance.IsServer() && ZNet.instance.IsDedicated();

  static string NormalizeEndpoint(string value) {
    string endpoint = string.IsNullOrWhiteSpace(value) ? "http://127.0.0.1:4000" : value.Trim().TrimEnd('/');
    if (endpoint.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)) {
      endpoint = "http://" + endpoint.Substring(5);
    } else if (endpoint.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)) {
      endpoint = "https://" + endpoint.Substring(6);
    }
    return endpoint;
  }

  static void SendHttpPostViaSocket(string url, string jsonBody, string key) {
    Uri uri = new(url);
    if (uri.Scheme != "http") {
      throw new NotSupportedException("heartbeat endpoint must be http (got '" + uri.Scheme + "')");
    }

    byte[] body = Encoding.UTF8.GetBytes(jsonBody);
    string headers = "POST " + uri.PathAndQuery + " HTTP/1.1\r\n"
        + "Host: " + uri.Host + ":" + uri.Port.ToString(CultureInfo.InvariantCulture) + "\r\n"
        + "Content-Type: application/json\r\n"
        + "Content-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n"
        + (string.IsNullOrWhiteSpace(key) ? string.Empty : "X-Lumberjacks-Telemetry-Key: " + key + "\r\n")
        + "Connection: close\r\n\r\n";

    byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
    // Socket + bounded read shared via BoundedRawHttp (ADR 0003) instead of a bespoke copy of it;
    // the head above — with its credential header out of PluginConfig — stays here, the same
    // caller-builds-head split the consumer and handshake runners use. SendBounded throws on connect
    // timeout, an over-size body, or one slower than the deadline.
    const int connectTimeoutMs = 5000, responseDeadlineMs = 5000, maxResponseBytes = 64 * 1024;
    string raw = BoundedRawHttp.SendBounded(
        uri, headerBytes, body, connectTimeoutMs, responseDeadlineMs, maxResponseBytes);
    string status = raw.Length == 0 ? string.Empty : raw.Split('\n')[0].Trim();
    string[] parts = status.Split(' ');
    if (parts.Length < 2 || parts[1].Length == 0 || parts[1][0] != '2') {
      throw new Exception("http status: " + (status.Length == 0 ? "(no response)" : status));
    }
  }

  public void Dispose() { }
}
