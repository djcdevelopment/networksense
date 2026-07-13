namespace ComfyNetworkSense;

using System.Collections.Generic;

using System;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

using UnityEngine;

// P6/I5 handshake responder (server-side / am4 only). BEHAVIOUR-CHANGING, rollback flag.
//
// When armed, a Harmony prefix on ZNet.RPC_PeerInfo (see HandshakeResponderPatches) reads a
// SetPos(0) CLONE of the client's PeerInfo ZPackage, decodes the logical fields (the exact
// SendPeerInfo layout, NETCODE-HANDSHAKE-CONTRACT.md), and asks the Lumberjacks responder for
// the admission decision (POST /valheim/handshake/peerinfo). Lumberjacks owns the gate LOGIC
// (the I3/I4 boundary: mod does bytes, gateway does logic); the mod enforces the returned
// verdict: on reject it Invoke("Error", code) and SKIPS vanilla; on accept it lets vanilla run
// so the real ZNet.AddPeer transition happens and the client truly enters the world.
//
// "Lumberjacks-fronted" proof: stage a Lumberjacks window with a gate vanilla am4 lacks (a ban,
// a password, a full-server count) so a client am4 would admit is decided by Lumberjacks instead.
//
// Fail-safe: empty endpoint or handshakeResponderEnabled=false => never arms, the prefix is a
// pure pass-through. Fail-OPEN: any decode/HTTP error falls through to vanilla, so the responder
// can never lock a client out on its own fault. Save-safe: the handshake writes no persisted ZDO
// state (it runs pre-AddPeer), same class as the I2 pin / I3 redirect.
public sealed class HandshakeResponderRunner : IDisposable {
  const int HttpTimeoutMs = 2000;

  static volatile HandshakeResponderRunner _active;
  public static HandshakeResponderRunner Active => _active;

  readonly object _lock = new();
  TelemetryCoordinator _coordinator;
  string _endpoint = string.Empty;
  string _windowId = string.Empty;
  bool _running;
  bool _armedOnce;
  float _stopAt = -1.0f;
  long _decisions;
  long _accepted;
  long _rejected;
  long _failOpen;
  string _lastError = string.Empty;

  public bool IsRunning => _running;

  // Called every frame from ComfyNetworkSense.Update. The handshake fires at CONNECT time, before
  // any netcode-probe window, so the responder self-arms once (server + enabled) and then stays
  // armed for the whole session (rollback = handshakeResponderEnabled=false + restart), unlike the
  // player-window-coupled client runners. handshakeResponderActiveSeconds>0 caps the window; 0 = continuous.
  public void Update(float deltaTime, TelemetryCoordinator coordinator) {
    if (!_running) {
      if (!_armedOnce
          && PluginConfig.HandshakeResponderEnabled.Value
          && ZNet.instance != null && ZNet.instance.IsServer()) {
        Start(coordinator);
      }
      return;
    }
    if (_stopAt > 0.0f && Time.unscaledTime >= _stopAt) {
      StopInternal("handshake_auto_stop");
    }
  }

  public string Start(TelemetryCoordinator coordinator) {
    if (ZNet.instance == null || !ZNet.instance.IsServer()) {
      return "Handshake responder REFUSED: server-side (am4) only.";
    }
    string endpoint = PluginConfig.HandshakeResponderEndpoint.Value?.Trim().TrimEnd('/') ?? string.Empty;
    if (string.IsNullOrEmpty(endpoint)) {
      return "Handshake responder REFUSED: handshakeResponderEndpoint is empty (fail-safe).";
    }
    lock (_lock) {
      if (_running) {
        return StatusLineLocked();
      }
      _coordinator = coordinator;
      _endpoint = endpoint;
      _windowId = string.IsNullOrWhiteSpace(PluginConfig.HandshakeResponderWindowId.Value)
          ? "i5-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
          : PluginConfig.HandshakeResponderWindowId.Value.Trim();
      float activeSeconds = Math.Max(0.0f, PluginConfig.HandshakeResponderActiveSeconds.Value);
      _stopAt = activeSeconds > 0.0f ? Time.unscaledTime + activeSeconds : -1.0f;
      _decisions = _accepted = _rejected = _failOpen = 0;
      _lastError = string.Empty;
      _running = true;
      _armedOnce = true;
    }
    _active = this;
    ZLog.Log("[ComfyNetworkSense][handshake] ARMED window=" + _windowId + " endpoint=" + _endpoint
        + (_stopAt > 0.0f ? " activeSeconds=" + PluginConfig.HandshakeResponderActiveSeconds.Value : " continuous")
        + ". Rollback: handshakeResponderEnabled=false.");
    return "Handshake responder ARMED (server-side) window=" + _windowId + " endpoint=" + _endpoint + ".";
  }

  public string Stop() => StopInternal("handshake_stop");

  string StopInternal(string eventName) {
    lock (_lock) {
      if (!_running) {
        return "Handshake responder is not running.";
      }
      _running = false;
      _active = null;
    }
    ZLog.Log("[ComfyNetworkSense][handshake] DISARMED (" + eventName + ") window=" + _windowId
        + " decisions=" + _decisions + " accepted=" + _accepted + " rejected=" + _rejected
        + " fail_open=" + _failOpen);
    return "Handshake responder disarmed.";
  }

  public string GetStatus() {
    lock (_lock) {
      return StatusLineLocked();
    }
  }

  public Dictionary<string, object> GetTelemetrySnapshot() {
    lock (_lock) {
      return new Dictionary<string, object> {
          ["handshake_accepted"] = _accepted,
          ["handshake_rejected"] = _rejected
      };
    }
  }

  string StatusLineLocked() => "Handshake responder " + (_running ? "ARMED" : "idle")
      + " window=" + _windowId + " decisions=" + _decisions + " accepted=" + _accepted
      + " rejected=" + _rejected + " fail_open=" + _failOpen
      + (string.IsNullOrEmpty(_lastError) ? string.Empty : " last_error=" + _lastError);

  // Synchronous admission decision. Runs on the Unity main thread inside the RPC_PeerInfo prefix,
  // so the whole call is bounded by HttpTimeoutMs and fail-open. Returns PassThrough on any fault
  // (unarmed, endpoint error, unparseable verdict) so vanilla handles the client normally.
  public HandshakeDecision Decide(
      long uid, string version, uint netVersion, Vector3 refPos,
      string playerName, string hostName, string passwordHash, bool ticketValid) {
    string endpoint, windowId;
    lock (_lock) {
      if (!_running) {
        return HandshakeDecision.PassThrough("not_armed");
      }
      endpoint = _endpoint;
      windowId = _windowId;
    }

    string connectionId = "live-" + SanitizeToken(uid.ToString(CultureInfo.InvariantCulture));
    // The password gate (F) and the steam-ticket gate (C) are real MD5+salt / Steamworks crypto that
    // only the in-game code can evaluate against the server's stored hash and the live salt (the
    // NETCODE-HANDSHAKE-CONTRACT "mod owns crypto" boundary). We therefore DELEGATE the password
    // check to vanilla: send an empty password_hash so Lumberjacks' Ordinal compare passes for an
    // accept-all (empty-password) context, and let vanilla's RPC_PeerInfo re-check the real hash on
    // the accept path. Lumberjacks still fronts version / blacklist / full / duplicate — the ban is
    // the discriminator vanilla am4 lacks. password_present is reported so the trace stays honest.
    bool passwordPresent = !string.IsNullOrEmpty(passwordHash);
    string body = "{"
        + "\"window_id\":\"" + JsonEscape(windowId) + "\","
        + "\"connection_id\":\"" + JsonEscape(connectionId) + "\","
        + "\"uid\":" + uid.ToString(CultureInfo.InvariantCulture) + ","
        + "\"version\":\"" + JsonEscape(version) + "\","
        + "\"net_version\":" + netVersion.ToString(CultureInfo.InvariantCulture) + ","
        + "\"ref_pos\":[" + Flt(refPos.x) + "," + Flt(refPos.y) + "," + Flt(refPos.z) + "],"
        + "\"player_name\":\"" + JsonEscape(playerName) + "\","
        + "\"host_name\":\"" + JsonEscape(hostName) + "\","
        + "\"password_hash\":\"\","
        + "\"ticket_valid\":" + (ticketValid ? "true" : "false")
        + "}";

    string responseBody;
    try {
      responseBody = PostForBody(endpoint + "/valheim/handshake/peerinfo", body);
    } catch (Exception exception) {
      lock (_lock) {
        _decisions++;
        _failOpen++;
        _lastError = "peerinfo: " + exception.GetType().Name + ": " + exception.Message;
      }
      ZLog.LogWarning("[ComfyNetworkSense][handshake] FAIL-OPEN (endpoint error) uid=" + uid
          + " player=" + playerName + " host=" + hostName + " : " + _lastError);
      return HandshakeDecision.PassThrough("endpoint_error");
    }

    bool accept = Regex.IsMatch(responseBody, "\"accept\"\\s*:\\s*true");
    if (accept) {
      lock (_lock) {
        _decisions++;
        _accepted++;
      }
      ZLog.Log("[ComfyNetworkSense][handshake] ACCEPT (Lumberjacks-decided) window=" + windowId
          + " uid=" + uid + " player=" + playerName + " host=" + hostName + " net_version=" + netVersion
          + " password_present=" + passwordPresent + " (password/ticket crypto delegated to vanilla)"
          + " -> letting vanilla complete AddPeer.");
      return HandshakeDecision.Accepted();
    }

    Match codeMatch = Regex.Match(responseBody, "\"error_code\"\\s*:\\s*(\\d+)");
    Match checkMatch = Regex.Match(responseBody, "\"failed_check\"\\s*:\\s*\"([^\"]*)\"");
    if (!codeMatch.Success) {
      // A 200 body with neither accept:true nor an error_code is not a verdict we can trust.
      lock (_lock) {
        _decisions++;
        _failOpen++;
        _lastError = "unparseable verdict";
      }
      ZLog.LogWarning("[ComfyNetworkSense][handshake] FAIL-OPEN (unparseable verdict) uid=" + uid
          + " body=" + Trim(responseBody, 200));
      return HandshakeDecision.PassThrough("unparseable_verdict");
    }

    int code = int.Parse(codeMatch.Groups[1].Value, CultureInfo.InvariantCulture);
    string failedCheck = checkMatch.Success ? checkMatch.Groups[1].Value : string.Empty;
    lock (_lock) {
      _decisions++;
      _rejected++;
    }
    ZLog.Log("[ComfyNetworkSense][handshake] REJECT (Lumberjacks-decided) window=" + windowId
        + " uid=" + uid + " player=" + playerName + " host=" + hostName + " password_present=" + passwordPresent
        + " code=" + code + " check=" + failedCheck + " -> Invoke(Error," + code + "), skip vanilla.");
    return HandshakeDecision.Reject(code, failedCheck);
  }

  // Raw-socket HTTP POST that READS THE RESPONSE BODY (unlike the redirect fire-and-forget POST):
  // Valheim's server Mono runtime has an empty WebRequest prefix table, so WebRequest.Create throws
  // "URI prefix not recognized" (ADR 0003 / valheim-server-mono-http-trap). Reads until the server
  // closes the connection (Connection: close). Throws on connect timeout / non-2xx so Decide fails open.
  static string PostForBody(string url, string jsonBody) {
    Uri uri = new(url);
    if (uri.Scheme != "http") {
      throw new NotSupportedException("handshake endpoint must be http (got '" + uri.Scheme + "')");
    }
    byte[] bodyBytes = Encoding.UTF8.GetBytes(jsonBody);
    string head =
        "POST " + uri.PathAndQuery + " HTTP/1.1\r\n"
        + "Host: " + uri.Host + ":" + uri.Port.ToString(CultureInfo.InvariantCulture) + "\r\n"
        + "Content-Type: application/json\r\n"
        + "Accept: application/json\r\n"
        + "Content-Length: " + bodyBytes.Length.ToString(CultureInfo.InvariantCulture) + "\r\n"
        + "Connection: close\r\n\r\n";
    byte[] headBytes = Encoding.ASCII.GetBytes(head);

    using TcpClient client = new();
    IAsyncResult connect = client.BeginConnect(uri.Host, uri.Port, null, null);
    if (!connect.AsyncWaitHandle.WaitOne(HttpTimeoutMs)) {
      throw new TimeoutException("connect timeout to " + uri.Host + ":" + uri.Port);
    }
    client.EndConnect(connect);
    client.SendTimeout = HttpTimeoutMs;
    client.ReceiveTimeout = HttpTimeoutMs;

    using NetworkStream stream = client.GetStream();
    stream.Write(headBytes, 0, headBytes.Length);
    stream.Write(bodyBytes, 0, bodyBytes.Length);
    stream.Flush();

    using MemoryStream memory = new();
    byte[] buffer = new byte[1024];
    int read;
    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) {
      memory.Write(buffer, 0, read);
    }
    string raw = Encoding.UTF8.GetString(memory.ToArray());

    string statusLine = raw.Length == 0 ? string.Empty : raw.Split('\n')[0].Trim();
    string[] parts = statusLine.Split(' ');
    if (parts.Length < 2 || parts[1].Length == 0 || parts[1][0] != '2') {
      throw new Exception("http status: " + (statusLine.Length == 0 ? "(no response)" : statusLine));
    }
    int split = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
    // Regex on the raw body tolerates a single-chunk transfer-encoding frame around small JSON.
    return split >= 0 ? raw.Substring(split + 4) : raw;
  }

  static string Flt(float value) => value.ToString("R", CultureInfo.InvariantCulture);

  static string Trim(string value, int max) =>
      string.IsNullOrEmpty(value) ? string.Empty : (value.Length <= max ? value : value.Substring(0, max));

  static string SanitizeToken(string value) {
    StringBuilder builder = new(value.Length);
    foreach (char c in value) {
      builder.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' ? c : '-');
    }
    return builder.Length == 0 ? "x" : builder.ToString();
  }

  static string JsonEscape(string value) {
    if (string.IsNullOrEmpty(value)) {
      return string.Empty;
    }
    StringBuilder builder = new(value.Length + 8);
    foreach (char c in value) {
      switch (c) {
        case '"': builder.Append("\\\""); break;
        case '\\': builder.Append("\\\\"); break;
        case '\b': builder.Append("\\b"); break;
        case '\f': builder.Append("\\f"); break;
        case '\n': builder.Append("\\n"); break;
        case '\r': builder.Append("\\r"); break;
        case '\t': builder.Append("\\t"); break;
        default:
          if (c < 0x20 || c > 0x7e) {
            builder.Append("\\u").Append(((int) c).ToString("x4", CultureInfo.InvariantCulture));
          } else {
            builder.Append(c);
          }
          break;
      }
    }
    return builder.ToString();
  }

  public void Dispose() {
    if (_running) {
      StopInternal("handshake_dispose");
    }
  }
}

// The verdict the RPC_PeerInfo prefix enforces. PassThrough => let vanilla handle the client.
public readonly struct HandshakeDecision {
  public readonly bool IsPassThrough;
  public readonly bool IsAccept;
  public readonly int ErrorCode;
  public readonly string FailedCheck;
  public readonly string Reason;

  HandshakeDecision(bool passThrough, bool accept, int errorCode, string failedCheck, string reason) {
    IsPassThrough = passThrough;
    IsAccept = accept;
    ErrorCode = errorCode;
    FailedCheck = failedCheck ?? string.Empty;
    Reason = reason ?? string.Empty;
  }

  public static HandshakeDecision PassThrough(string reason) => new(true, false, 0, string.Empty, reason);
  public static HandshakeDecision Accepted() => new(false, true, 0, string.Empty, "accept");
  public static HandshakeDecision Reject(int code, string failedCheck) => new(false, false, code, failedCheck, "reject");
}
