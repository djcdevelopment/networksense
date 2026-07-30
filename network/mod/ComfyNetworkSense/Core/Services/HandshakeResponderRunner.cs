namespace ComfyNetworkSense;

using System.Collections.Generic;

using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

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
  const int ResponseDeadlineMs = 2000;
  const int MaxResponseBytes = 64 * 1024;
  const double PendingDeadlineSeconds = 5.0;
  const int MaxCompletionsPerFrame = 8;

  static volatile HandshakeResponderRunner _active;
  public static HandshakeResponderRunner Active => _active;

  readonly object _lock = new();
  readonly Dictionary<ZRpc, PendingHandshake> _pending = new();
  readonly HashSet<ZRpc> _vanillaResume = new();
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
  long _deferred;
  long _completedOffThread;
  long _pendingTimedOut;
  int _generation;
  string _lastError = string.Empty;

  public bool IsRunning => _running;

  // Called every frame from ComfyNetworkSense.Update. The handshake fires at CONNECT time, before
  // any netcode-probe window, so the responder self-arms once (server + enabled) and then stays
  // armed for the whole session (rollback = handshakeResponderEnabled=false + restart), unlike the
  // player-window-coupled client runners. handshakeResponderActiveSeconds>0 caps the window; 0 = continuous.
  public void Update(float deltaTime, TelemetryCoordinator coordinator) {
    DrainCompleted();
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
      _deferred = _completedOffThread = _pendingTimedOut = 0;
      _lastError = string.Empty;
      _running = true;
      _armedOnce = true;
      _generation++;
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
      _generation++;
    }
    ZLog.Log("[ComfyNetworkSense][handshake] DISARMED (" + eventName + ") window=" + _windowId
        + " decisions=" + _decisions + " accepted=" + _accepted + " rejected=" + _rejected
        + " fail_open=" + _failOpen + " pending=" + _pending.Count);
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
          ["handshake_rejected"] = _rejected,
          ["handshake_deferred"] = _deferred,
          ["handshake_offthread_completed"] = _completedOffThread,
          ["handshake_pending_timeouts"] = _pendingTimedOut,
          ["handshake_pending"] = _pending.Count
      };
    }
  }

  string StatusLineLocked() => "Handshake responder " + (_running ? "ARMED" : "idle")
      + " window=" + _windowId + " decisions=" + _decisions + " accepted=" + _accepted
      + " rejected=" + _rejected + " fail_open=" + _failOpen
      + " deferred=" + _deferred + " offthread_completed=" + _completedOffThread
      + " pending_timeouts=" + _pendingTimedOut + " pending=" + _pending.Count
      + (string.IsNullOrEmpty(_lastError) ? string.Empty : " last_error=" + _lastError);

  // Called only by the RPC_PeerInfo prefix on Unity's main thread. It copies primitive request
  // state, owns this RPC invocation, and returns immediately. No Unity/Valheim object crosses into
  // the worker except as an opaque reference retained for the later main-thread replay.
  public bool TryDefer(
      ZRpc rpc,
      byte[] packageBytes,
      long uid,
      string version,
      uint netVersion,
      Vector3 refPos,
      string playerName,
      string hostName,
      string passwordHash,
      bool ticketValid) {
    if (rpc == null || packageBytes == null) {
      return false;
    }

    string endpoint;
    string windowId;
    bool strictMode;
    int generation;
    lock (_lock) {
      if (!_running) {
        return false;
      }
      endpoint = _endpoint;
      windowId = _windowId;
      strictMode = PluginConfig.HandshakeResponderStrictMode.Value;
      generation = _generation;
    }

    // A retransmitted PeerInfo while authority is pending is still owned by this deferred call.
    if (_pending.ContainsKey(rpc)) {
      return true;
    }

    HandshakeRequest request = new(
        endpoint,
        windowId,
        strictMode,
        generation,
        uid,
        version,
        netVersion,
        refPos.x,
        refPos.y,
        refPos.z,
        playerName,
        hostName,
        !string.IsNullOrEmpty(passwordHash),
        ticketValid);

    Task<HandshakeDecision> decisionTask;
    try {
      decisionTask = Task.Run(() => FetchDecision(request));
    } catch (Exception exception) {
      lock (_lock) {
        _lastError = "defer: " + exception.GetType().Name + ": " + exception.Message;
      }
      return false;
    }

    _pending.Add(rpc, new PendingHandshake(
        rpc, packageBytes, request, decisionTask, DateTime.UtcNow));
    lock (_lock) {
      _deferred++;
    }
    ZLog.Log("[ComfyNetworkSense][handshake] DEFERRED off-thread window=" + windowId
        + " uid=" + uid + " player=" + playerName + " host=" + hostName + ".");
    return true;
  }

  // Consumed by the Harmony prefix during the one-shot main-thread replay.
  public bool ConsumeVanillaResume(ZRpc rpc) =>
      rpc != null && _vanillaResume.Remove(rpc);

  HandshakeDecision FetchDecision(HandshakeRequest request) {
    string connectionId = "live-"
        + SanitizeToken(request.Uid.ToString(CultureInfo.InvariantCulture));
    // The password gate (F) and the steam-ticket gate (C) are real MD5+salt / Steamworks crypto that
    // only the in-game code can evaluate against the server's stored hash and the live salt (the
    // NETCODE-HANDSHAKE-CONTRACT "mod owns crypto" boundary). We therefore DELEGATE the password
    // check to vanilla: send an empty password_hash so Lumberjacks' Ordinal compare passes for an
    // accept-all (empty-password) context, and let vanilla's RPC_PeerInfo re-check the real hash on
    // the accept path. Lumberjacks still fronts version / blacklist / full / duplicate — the ban is
    // the discriminator vanilla am4 lacks. password_present is reported so the trace stays honest.
    string body = "{"
        + "\"window_id\":\"" + JsonEscape(request.WindowId) + "\","
        + "\"connection_id\":\"" + JsonEscape(connectionId) + "\","
        + "\"uid\":" + request.Uid.ToString(CultureInfo.InvariantCulture) + ","
        + "\"version\":\"" + JsonEscape(request.Version) + "\","
        + "\"net_version\":" + request.NetVersion.ToString(CultureInfo.InvariantCulture) + ","
        + "\"ref_pos\":[" + Flt(request.RefX) + "," + Flt(request.RefY) + "," + Flt(request.RefZ) + "],"
        + "\"player_name\":\"" + JsonEscape(request.PlayerName) + "\","
        + "\"host_name\":\"" + JsonEscape(request.HostName) + "\","
        + "\"password_hash\":\"\","
        + "\"ticket_valid\":" + (request.TicketValid ? "true" : "false") + ","
        // The MOD'S OWN identity, not the joining client's. version/net_version above describe the
        // player connecting; these describe the build answering. Additive fields are safe on this
        // wire (the verdict is regex-matched, not deserialized), and a Gateway that does not know
        // them ignores them. Absence is meaningful: a build older than this field sends neither, so
        // the Gateway can spot a stale mod without the mod's cooperation.
        + "\"mod_version\":\"" + JsonEscape(ComfyNetworkSense.PluginVersion) + "\","
        + "\"mod_release_id\":\"" + JsonEscape(ComfyNetworkSense.ReleaseId) + "\""
        + "}";

    string responseBody;
    try {
      responseBody = PostForBody(request.Endpoint + "/valheim/handshake/peerinfo", body);
    } catch (Exception exception) {
      string detail = "peerinfo: " + exception.GetType().Name + ": " + exception.Message;
      return AuthorityUnavailable(request.StrictMode, "endpoint_error", detail);
    }

    bool accept = Regex.IsMatch(responseBody, "\"accept\"\\s*:\\s*true");
    if (accept) {
      return HandshakeDecision.Accepted();
    }

    Match codeMatch = Regex.Match(responseBody, "\"error_code\"\\s*:\\s*(\\d+)");
    Match checkMatch = Regex.Match(responseBody, "\"failed_check\"\\s*:\\s*\"([^\"]*)\"");
    if (!codeMatch.Success) {
      // A 200 body with neither accept:true nor an error_code is not a verdict we can trust.
      return AuthorityUnavailable(
          request.StrictMode,
          "unparseable_verdict",
          "unparseable verdict: " + Trim(responseBody, 200));
    }

    int code = int.Parse(codeMatch.Groups[1].Value, CultureInfo.InvariantCulture);
    string failedCheck = checkMatch.Success ? checkMatch.Groups[1].Value : string.Empty;
    return HandshakeDecision.Reject(code, failedCheck);
  }

  static HandshakeDecision AuthorityUnavailable(bool strict, string reason, string detail) {
    if (!strict) {
      return HandshakeDecision.PassThrough(reason, detail);
    }
    return HandshakeDecision.Reject(
        (int) ValheimConnectionStatus.ErrorConnectFailed,
        "strict_authority_unavailable",
        "strict_authority_unavailable",
        detail);
  }

  void DrainCompleted() {
    if (_pending.Count == 0) {
      return;
    }

    DateTime now = DateTime.UtcNow;
    List<ZRpc> ready = new();
    foreach (KeyValuePair<ZRpc, PendingHandshake> pair in _pending) {
      if (pair.Value.DecisionTask.IsCompleted
          || (now - pair.Value.StartedUtc).TotalSeconds >= PendingDeadlineSeconds) {
        ready.Add(pair.Key);
        if (ready.Count >= MaxCompletionsPerFrame) {
          break;
        }
      }
    }

    foreach (ZRpc rpc in ready) {
      if (!_pending.TryGetValue(rpc, out PendingHandshake pending)) {
        continue;
      }
      _pending.Remove(rpc);

      HandshakeDecision decision;
      bool timedOut = !pending.DecisionTask.IsCompleted;
      if (timedOut) {
        decision = AuthorityUnavailable(
            pending.Request.StrictMode,
            "pending_timeout",
            "authority task exceeded " + PendingDeadlineSeconds.ToString("0.0", CultureInfo.InvariantCulture)
                + " seconds");
      } else {
        try {
          decision = pending.DecisionTask.GetAwaiter().GetResult();
        } catch (Exception exception) {
          decision = AuthorityUnavailable(
              pending.Request.StrictMode,
              "worker_fault",
              exception.GetType().Name + ": " + exception.Message);
        }
      }

      bool enforce;
      lock (_lock) {
        enforce = _running
            && _generation == pending.Request.Generation
            && string.Equals(_windowId, pending.Request.WindowId, StringComparison.Ordinal);
        if (enforce) {
          _decisions++;
          if (timedOut) {
            _pendingTimedOut++;
          } else {
            _completedOffThread++;
          }
          if (decision.IsAccept) {
            _accepted++;
          } else if (decision.IsPassThrough) {
            _failOpen++;
          } else {
            _rejected++;
          }
          if (!string.IsNullOrEmpty(decision.Detail)) {
            _lastError = decision.Detail;
          }
        }
      }

      if (!enforce) {
        ResumeVanilla(pending, "responder_disarmed");
      } else {
        ApplyDecision(pending, decision);
      }
    }
  }

  void ApplyDecision(PendingHandshake pending, HandshakeDecision decision) {
    HandshakeRequest request = pending.Request;
    long authorityWaitMs = Math.Max(
        0L, (long) (DateTime.UtcNow - pending.StartedUtc).TotalMilliseconds);
    if (decision.IsAccept) {
      ZLog.Log("[ComfyNetworkSense][handshake] ACCEPT (Lumberjacks-decided, off-thread) window="
          + request.WindowId + " uid=" + request.Uid + " player=" + request.PlayerName
          + " host=" + request.HostName + " net_version=" + request.NetVersion
          + " password_present=" + request.PasswordPresent + " authority_wait_ms=" + authorityWaitMs
          + " (password/ticket crypto delegated to vanilla) -> resuming vanilla AddPeer on main thread.");
      ResumeVanilla(pending, "accepted");
      return;
    }

    if (decision.IsPassThrough) {
      ZLog.LogWarning("[ComfyNetworkSense][handshake] FAIL-OPEN (" + decision.Reason + ") uid="
          + request.Uid + " player=" + request.PlayerName + " host=" + request.HostName
          + " authority_wait_ms=" + authorityWaitMs + " : " + decision.Detail
          + " -> resuming vanilla on main thread.");
      ResumeVanilla(pending, decision.Reason);
      return;
    }

    try {
      if (string.Equals(
          decision.FailedCheck, "strict_authority_unavailable", StringComparison.Ordinal)) {
        ZLog.LogWarning("[ComfyNetworkSense][handshake] FAIL-CLOSED (" + decision.Reason + ") uid="
            + request.Uid + " player=" + request.PlayerName + " host=" + request.HostName
            + " authority_wait_ms=" + authorityWaitMs
            + " -> Invoke(Error," + decision.ErrorCode + "), skip vanilla. "
            + decision.FailedCheck + " : " + decision.Detail);
      } else {
        ZLog.Log("[ComfyNetworkSense][handshake] REJECT (Lumberjacks-decided, off-thread) window="
            + request.WindowId + " uid=" + request.Uid + " player=" + request.PlayerName
            + " host=" + request.HostName + " password_present=" + request.PasswordPresent
            + " authority_wait_ms=" + authorityWaitMs
            + " code=" + decision.ErrorCode + " check=" + decision.FailedCheck
            + " -> Invoke(Error," + decision.ErrorCode + "), skip vanilla.");
      }
      pending.Rpc.Invoke("Error", decision.ErrorCode);
    } catch (Exception exception) {
      lock (_lock) {
        _lastError = "reject apply: " + exception.GetType().Name + ": " + exception.Message;
      }
      ZLog.LogWarning("[ComfyNetworkSense][handshake] main-thread reject apply failed: "
          + exception.GetType().Name + ": " + exception.Message);
    }
  }

  void ResumeVanilla(PendingHandshake pending, string reason) {
    try {
      _vanillaResume.Add(pending.Rpc);
      HandshakeResponderPatches.ResumeVanilla(pending.Rpc, pending.PackageBytes);
    } catch (Exception exception) {
      lock (_lock) {
        _lastError = "vanilla resume (" + reason + "): "
            + exception.GetType().Name + ": " + exception.Message;
      }
      ZLog.LogWarning("[ComfyNetworkSense][handshake] main-thread vanilla resume failed ("
          + reason + "): " + exception.GetType().Name + ": " + exception.Message);
    } finally {
      _vanillaResume.Remove(pending.Rpc);
    }
  }

  // The transport stays Unity-free and runs only inside FetchDecision's Task. These values bound
  // authority latency; PendingDeadlineSeconds separately bounds how long the main-thread state
  // machine will retain a peer if a worker fails to return.
  static string PostForBody(string url, string jsonBody) =>
      BoundedRawHttp.PostForBody(url, jsonBody, HttpTimeoutMs, ResponseDeadlineMs, MaxResponseBytes);

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

  readonly struct HandshakeRequest {
    public readonly string Endpoint;
    public readonly string WindowId;
    public readonly bool StrictMode;
    public readonly int Generation;
    public readonly long Uid;
    public readonly string Version;
    public readonly uint NetVersion;
    public readonly float RefX;
    public readonly float RefY;
    public readonly float RefZ;
    public readonly string PlayerName;
    public readonly string HostName;
    public readonly bool PasswordPresent;
    public readonly bool TicketValid;

    public HandshakeRequest(
        string endpoint,
        string windowId,
        bool strictMode,
        int generation,
        long uid,
        string version,
        uint netVersion,
        float refX,
        float refY,
        float refZ,
        string playerName,
        string hostName,
        bool passwordPresent,
        bool ticketValid) {
      Endpoint = endpoint ?? string.Empty;
      WindowId = windowId ?? string.Empty;
      StrictMode = strictMode;
      Generation = generation;
      Uid = uid;
      Version = version ?? string.Empty;
      NetVersion = netVersion;
      RefX = refX;
      RefY = refY;
      RefZ = refZ;
      PlayerName = playerName ?? string.Empty;
      HostName = hostName ?? string.Empty;
      PasswordPresent = passwordPresent;
      TicketValid = ticketValid;
    }
  }

  sealed class PendingHandshake {
    public readonly ZRpc Rpc;
    public readonly byte[] PackageBytes;
    public readonly HandshakeRequest Request;
    public readonly Task<HandshakeDecision> DecisionTask;
    public readonly DateTime StartedUtc;

    public PendingHandshake(
        ZRpc rpc,
        byte[] packageBytes,
        HandshakeRequest request,
        Task<HandshakeDecision> decisionTask,
        DateTime startedUtc) {
      Rpc = rpc;
      PackageBytes = packageBytes;
      Request = request;
      DecisionTask = decisionTask;
      StartedUtc = startedUtc;
    }
  }

  public void Dispose() {
    if (_running) {
      StopInternal("handshake_dispose");
    }
  }
}

// Mirrored from the decompiled ZNet.ConnectionStatus (assembly_valheim 0.221.12,
// ZNet.decompiled.cs:23-38) - the same table Lumberjacks mirrors in ValheimHandshakeService.cs.
// Kept here so a reject reads as its meaning rather than as a bare int: per the handshake contract,
// no int means no reject, and the int is the ONLY part of a verdict the player ever sees.
public enum ValheimConnectionStatus {
  None = 0,
  Connecting = 1,
  Connected = 2,
  ErrorVersion = 3,
  ErrorDisconnected = 4,
  ErrorConnectFailed = 5,
  ErrorPassword = 6,
  ErrorAlreadyConnected = 7,
  ErrorBanned = 8,
  ErrorFull = 9,
  ErrorPlatformExcluded = 10,
  ErrorCrossplayPrivilege = 11,
  ErrorKicked = 12,
}

// The verdict the RPC_PeerInfo prefix enforces. PassThrough => let vanilla handle the client.
public readonly struct HandshakeDecision {
  public readonly bool IsPassThrough;
  public readonly bool IsAccept;
  public readonly int ErrorCode;
  public readonly string FailedCheck;
  public readonly string Reason;
  public readonly string Detail;

  HandshakeDecision(
      bool passThrough,
      bool accept,
      int errorCode,
      string failedCheck,
      string reason,
      string detail) {
    IsPassThrough = passThrough;
    IsAccept = accept;
    ErrorCode = errorCode;
    FailedCheck = failedCheck ?? string.Empty;
    Reason = reason ?? string.Empty;
    Detail = detail ?? string.Empty;
  }

  public static HandshakeDecision PassThrough(string reason) =>
      new(true, false, 0, string.Empty, reason, string.Empty);

  public static HandshakeDecision PassThrough(string reason, string detail) =>
      new(true, false, 0, string.Empty, reason, detail);

  public static HandshakeDecision Accepted() =>
      new(false, true, 0, string.Empty, "accept", string.Empty);

  public static HandshakeDecision Reject(int code, string failedCheck) =>
      new(false, false, code, failedCheck, "reject", string.Empty);

  public static HandshakeDecision Reject(
      int code, string failedCheck, string reason, string detail) =>
      new(false, false, code, failedCheck, reason, detail);
}
