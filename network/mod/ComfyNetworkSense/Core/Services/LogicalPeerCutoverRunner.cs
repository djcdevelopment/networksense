namespace ComfyNetworkSense;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;

using HarmonyLib;
using UnityEngine;

/// <summary>
/// C7's Steam-free peer adapter. The Gateway announces authenticated logical peers;
/// this runner creates only Valheim's local bookkeeping shell. It never opens an
/// ISocket transport and never carries serialized ZRpc packages.
/// </summary>
public sealed class LogicalPeerCutoverRunner : IDisposable {
  const string ReceiptFileName = "logical-peer-cutover.jsonl";
  static readonly FieldInfo PeersField = AccessTools.Field(typeof(ZNet), "m_peers");
  static readonly FieldInfo WorldField = AccessTools.Field(typeof(ZNet), "m_world");
  static readonly FieldInfo NetTimeField = AccessTools.Field(typeof(ZNet), "m_netTime");
  static readonly FieldInfo ConnectionStatusField =
      AccessTools.Field(typeof(ZNet), "m_connectionStatus");
  static LogicalPeerCutoverRunner _active;

  readonly LumberjacksGameSessionRunner _gameSession;
  readonly WorldZoneCutoverRunner _worldZone;
  readonly ConcurrentQueue<CanonicalFrame> _frames = new();
  readonly Dictionary<ZRpc, LogicalPeer> _byRpc = new();
  readonly Dictionary<string, LogicalPeer> _byLogical =
      new(StringComparer.Ordinal);
  readonly TelemetryLogWriter _writer = new();

  string _remoteLogicalPeerId = string.Empty;
  long _remotePeerUid;
  bool _clientConstructed;
  bool _disposed;
  long _suppressedInvokes;

  public LogicalPeerCutoverRunner(
      LumberjacksGameSessionRunner gameSession,
      WorldZoneCutoverRunner worldZone) {
    _gameSession = gameSession;
    _worldZone = worldZone;
    LogicalPeerCutoverRunner previous = Interlocked.Exchange(ref _active, this);
    previous?.Dispose();
  }

  public static bool Selected {
    get {
      if (NativeAutotestRequest.ActiveSteamFreeColdJoin) return true;
      try {
        return ZNet.instance != null && ZNet.instance.IsServer() &&
            PluginConfig.LogicalPeerCutoverEnabled?.Value == true;
      } catch {
        return false;
      }
    }
  }

  public static void EnqueueCanonicalFrame(string type, string json) =>
      Volatile.Read(ref _active)?._frames.Enqueue(new(type, json));

  public void Update(float now) {
    if (_disposed) return;
    if (!Selected) {
      if (_byLogical.Count > 0) RemoveAll("cutover_disarmed");
      return;
    }
    DrainFrames();
    if (ZNet.instance == null || ZNet.instance.IsServer() || _clientConstructed ||
        _remotePeerUid == 0 || string.IsNullOrWhiteSpace(_remoteLogicalPeerId))
      return;
    if (!_worldZone.TryCreateLogicalWorld(
            out World world, out double networkTime,
            out string worldEpoch, out string detail)) {
      if (_worldZone.DescriptorRejected)
        Write("cold_join_failed", "reason=" + detail);
      return;
    }

    if (!ConstructPeer(
            _remoteLogicalPeerId, _remotePeerUid, "server", "Lumberjacks", world,
            networkTime))
      return;
    _clientConstructed = true;
    Write(
        "client_logical_peer_ready",
        "remote_peer_uid=" + _remotePeerUid
        + " remote_logical_peer_id=" + _remoteLogicalPeerId
        + " world_epoch=" + worldEpoch
        + " native_socket=false native_handshake=false");
  }

  void DrainFrames() {
    while (_frames.TryDequeue(out CanonicalFrame frame)) {
      string role = ExtractString(frame.Json, "role");
      string logicalPeer = ExtractString(frame.Json, "logical_peer_id");
      long peerUid = ExtractLong(frame.Json, "peer_uid");
      if (frame.Type == "valheim_logical_peer_attached") {
        if (!SafeToken(logicalPeer, 96) || peerUid == 0 ||
            role is not ("server" or "client")) {
          Write("logical_peer_frame_rejected", "reason=attach_fields_invalid");
          continue;
        }
        if (ZNet.instance == null) {
          _frames.Enqueue(frame);
          return;
        }
        if (ZNet.instance.IsServer()) {
          if (role != "client") continue;
          string character = ExtractString(frame.Json, "character");
          if (!ConstructPeer(
                  logicalPeer, peerUid, role,
                  string.IsNullOrWhiteSpace(character)
                      ? "LumberjacksClient" : character,
                  null, 0)) {
            _frames.Enqueue(frame);
            return;
          }
        } else {
          if (role != "server") continue;
          _remoteLogicalPeerId = logicalPeer;
          _remotePeerUid = peerUid;
          Write(
              "logical_server_announced",
              "peer_uid=" + peerUid + " logical_peer_id=" + logicalPeer);
        }
        Acknowledge(frame.Json);
        continue;
      }

      if (frame.Type == "valheim_logical_peer_detached") {
        if (string.Equals(
                logicalPeer, _remoteLogicalPeerId, StringComparison.Ordinal)) {
          _remoteLogicalPeerId = string.Empty;
          _remotePeerUid = 0;
        }
        RemovePeer(logicalPeer, "gateway_detached");
        Acknowledge(frame.Json);
        continue;
      }

      if (frame.Type == "valheim_logical_peer_control") {
        ApplyControl(frame.Json);
        Acknowledge(frame.Json);
      }
    }
  }

  bool ConstructPeer(
      string logicalPeerId,
      long peerUid,
      string role,
      string playerName,
      World world,
      double networkTime) {
    if (_byLogical.ContainsKey(logicalPeerId)) return true;
    List<ZNetPeer> peers = PeersField?.GetValue(ZNet.instance) as List<ZNetPeer>;
    if (peers == null || ZDOMan.instance == null || ZRoutedRpc.instance == null) {
      Write("logical_peer_construct_failed", "reason=valheim_managers_not_ready");
      return false;
    }

    LogicalSocket socket = new(logicalPeerId, peerUid);
    ZNetPeer peer = new(socket, string.Equals(role, "server", StringComparison.Ordinal)) {
        m_uid = peerUid,
        m_playerName = playerName ?? string.Empty,
        m_refPos = Vector3.zero
    };
    LogicalPeer logical = new(logicalPeerId, role, peer, socket);
    _byLogical[logicalPeerId] = logical;
    _byRpc[peer.m_rpc] = logical;
    peers.Add(peer);
    DirectControlCutoverRunner.RegisterPeer(peer);
    ZDOMan.instance.AddPeer(peer);
    ZRoutedRpc.instance.AddPeer(peer);

    if (!ZNet.instance.IsServer()) {
      WorldField?.SetValue(null, world);
      WorldGenerator.Initialize(world);
      NetTimeField?.SetValue(ZNet.instance, networkTime);
      ConnectionStatusField?.SetValue(null, ZNet.ConnectionStatus.Connected);
    }
    Write(
        "logical_peer_constructed",
        "role=" + role
        + " peer_uid=" + peerUid
        + " logical_peer_id=" + logicalPeerId
        + " socket_type=LogicalSocket native_socket=false");
    return true;
  }

  void ApplyControl(string json) {
    string sourceLogical = ExtractString(json, "source_logical_peer_id");
    string targetLogical = ExtractString(json, "target_logical_peer_id");
    string control = ExtractString(json, "control");
    long sourceUid = ExtractLong(json, "source_peer_uid");
    LogicalPeer peer = _byLogical.TryGetValue(sourceLogical, out LogicalPeer value)
        ? value : null;
    if (peer == null || peer.Peer.m_uid != sourceUid ||
        !string.Equals(
            targetLogical, _gameSession.LogicalPeerId, StringComparison.Ordinal)) {
      Write("logical_control_rejected", "reason=identity_mismatch");
      return;
    }
    if (control == "character_id" && ZNet.instance.IsServer()) {
      long user = ExtractLong(json, "zdo_user_id");
      uint id = (uint)ExtractLong(json, "zdo_id");
      if (user == 0 || id == 0) {
        Write("logical_control_rejected", "reason=character_id_invalid");
        return;
      }
      peer.Peer.m_characterID = new ZDOID(user, id);
      ZLog.Log(
          "Got character ZDOID from " + peer.Peer.m_playerName
          + " : " + peer.Peer.m_characterID);
      Write(
          "character_id_applied",
          "source_logical_peer_id=" + sourceLogical
          + " character_id=" + peer.Peer.m_characterID);
      return;
    }
    if (control == "disconnect") {
      RemovePeer(sourceLogical, "semantic_disconnect");
      return;
    }
    Write("logical_control_rejected", "reason=control_not_allowed");
  }

  void RemovePeer(string logicalPeerId, string reason) {
    if (!_byLogical.TryGetValue(logicalPeerId, out LogicalPeer logical)) return;
    _byLogical.Remove(logicalPeerId);
    _byRpc.Remove(logical.Peer.m_rpc);
    logical.Socket.Close();
    try { ZDOMan.instance?.RemovePeer(logical.Peer); } catch { }
    try { ZRoutedRpc.instance?.RemovePeer(logical.Peer); } catch { }
    List<ZNetPeer> peers = PeersField?.GetValue(ZNet.instance) as List<ZNetPeer>;
    peers?.Remove(logical.Peer);
    try { logical.Peer.Dispose(); } catch { }
    if (ZNet.instance != null && !ZNet.instance.IsServer()) {
      _clientConstructed = false;
      ConnectionStatusField?.SetValue(
          null, ZNet.ConnectionStatus.ErrorDisconnected);
    }
    Write(
        "logical_peer_removed",
        "logical_peer_id=" + logicalPeerId + " reason=" + reason);
  }

  void RemoveAll(string reason) {
    string[] logicalPeerIds = new string[_byLogical.Count];
    _byLogical.Keys.CopyTo(logicalPeerIds, 0);
    foreach (string logicalPeerId in logicalPeerIds)
      RemovePeer(logicalPeerId, reason);
  }

  public static bool SuppressClientConnect() {
    LogicalPeerCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !Selected) return false;
    active.Write(
        "native_client_connect_suppressed",
        "source=request_manifest native_target=false");
    return true;
  }

  public static bool TryHandleInvoke(
      ZRpc rpc, string method, object[] parameters) {
    LogicalPeerCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !Selected ||
        !active._byRpc.TryGetValue(rpc, out LogicalPeer remote))
      return false;

    long count = Interlocked.Increment(ref active._suppressedInvokes);
    if (string.Equals(method, "CharacterID", StringComparison.Ordinal) &&
        ZNet.instance != null && !ZNet.instance.IsServer() &&
        parameters?.Length > 0 && parameters[0] is ZDOID characterId &&
        !characterId.IsNone()) {
      string fields =
          "\"target_logical_peer_id\":\"" + Escape(remote.LogicalPeerId) + "\""
          + ",\"control\":\"character_id\""
          + ",\"zdo_user_id\":"
          + characterId.UserID.ToString(CultureInfo.InvariantCulture)
          + ",\"zdo_id\":"
          + characterId.ID.ToString(CultureInfo.InvariantCulture);
      bool queued = active._gameSession.TryQueueLogicalPeerControl(fields);
      active.Write(
          queued ? "character_id_queued" : "character_id_queue_failed",
          "target_logical_peer_id=" + remote.LogicalPeerId
          + " character_id=" + characterId);
      return true;
    }
    if (string.Equals(method, "Disconnect", StringComparison.Ordinal)) {
      bool queued = active._gameSession.TryQueueLogicalPeerControl(
          "\"target_logical_peer_id\":\"" + Escape(remote.LogicalPeerId)
          + "\",\"control\":\"disconnect\"");
      active.Write(
          queued ? "disconnect_queued" : "disconnect_queue_failed",
          "target_logical_peer_id=" + remote.LogicalPeerId);
      return true;
    }
    if (count <= 4 || IsPowerOfTwo(count))
      active.Write(
          "logical_native_invoke_suppressed",
          "count=" + count + " method=" + SafeMarker(method)
          + " semantic_fallback=false");
    return true;
  }

  public static bool TryVirtualizeRpcUpdate(
      ZRpc rpc, ref ZRpc.ErrorCode result) {
    LogicalPeerCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !Selected || !active._byRpc.ContainsKey(rpc))
      return false;
    result = ZRpc.ErrorCode.Success;
    return true;
  }

  public static void VirtualizeConnectionStatus(
      ref ZNet.ConnectionStatus result) {
    LogicalPeerCutoverRunner active = Volatile.Read(ref _active);
    if (active != null && Selected && active._clientConstructed &&
        ZNet.instance != null && !ZNet.instance.IsServer())
      result = ZNet.ConnectionStatus.Connected;
  }

  void Acknowledge(string json) {
    long sequence = ExtractLong(json, "seq");
    if (sequence > 0)
      _gameSession.QueueReliableAck(sequence);
  }

  void Write(string state, string detail) {
    bool server = false;
    try { server = ZNet.instance != null && ZNet.instance.IsServer(); } catch { }
    string runId = NativeAutotestRequest.ActiveRunId;
    if (server && string.IsNullOrWhiteSpace(runId))
      runId = PluginConfig.NativeNetworkEvidenceRunId?.Value ?? string.Empty;
    _writer.Write(
        ReceiptFileName,
        new Dictionary<string, object> {
            ["schema_version"] = 1,
            ["timestamp_utc"] =
                DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["state"] = state,
            ["run_id"] = runId,
            ["client"] = server ? "server" : NativeAutotestRequest.ActiveClient,
            ["detail"] = detail ?? string.Empty
        });
    ComfyNetworkSense.LogInfo(
        "LOGICAL_PEER_CUTOVER state=" + SafeMarker(state)
        + " run_id=" + SafeMarker(runId)
        + " detail=" + SafeMarker(detail));
  }

  static string ExtractString(string json, string name) {
    Match match = Regex.Match(
        json ?? string.Empty,
        "\"" + Regex.Escape(name)
        + "\"\\s*:\\s*\"(?<value>(?:\\\\.|[^\"])*)\"",
        RegexOptions.IgnoreCase);
    return match.Success
        ? match.Groups["value"].Value.Replace("\\\"", "\"").Replace("\\\\", "\\")
        : string.Empty;
  }

  static long ExtractLong(string json, string name) {
    Match match = Regex.Match(
        json ?? string.Empty,
        "\"" + Regex.Escape(name) + "\"\\s*:\\s*(?<value>-?[0-9]+)",
        RegexOptions.IgnoreCase);
    return match.Success &&
        long.TryParse(
            match.Groups["value"].Value, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out long value)
        ? value : 0;
  }

  static bool SafeToken(string value, int max) {
    if (string.IsNullOrWhiteSpace(value) || value.Length > max) return false;
    foreach (char c in value)
      if (!char.IsLetterOrDigit(c) && c is not '-' and not '_' and not '.')
        return false;
    return true;
  }

  static string Escape(string value) =>
      (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");

  static string SafeMarker(string value) =>
      string.IsNullOrWhiteSpace(value)
          ? "none"
          : value.Trim().Replace(' ', '_').Replace('\t', '_')
              .Replace('\r', '_').Replace('\n', '_');

  static bool IsPowerOfTwo(long value) =>
      value > 0 && (value & (value - 1)) == 0;

  public void Dispose() {
    if (_disposed) return;
    _disposed = true;
    Interlocked.CompareExchange(ref _active, null, this);
    _writer.Dispose();
  }

  readonly struct CanonicalFrame {
    public readonly string Type;
    public readonly string Json;
    public CanonicalFrame(string type, string json) {
      Type = type ?? string.Empty;
      Json = json ?? string.Empty;
    }
  }

  sealed class LogicalPeer {
    public readonly string LogicalPeerId;
    public readonly string Role;
    public readonly ZNetPeer Peer;
    public readonly LogicalSocket Socket;
    public LogicalPeer(
        string logicalPeerId, string role, ZNetPeer peer, LogicalSocket socket) {
      LogicalPeerId = logicalPeerId;
      Role = role;
      Peer = peer;
      Socket = socket;
    }
  }

  sealed class LogicalSocket : ISocket {
    readonly string _logicalPeerId;
    readonly long _peerUid;
    bool _connected = true;

    public LogicalSocket(string logicalPeerId, long peerUid) {
      _logicalPeerId = logicalPeerId;
      _peerUid = peerUid;
    }

    public bool IsConnected() => _connected;
    public void Send(ZPackage pkg) =>
        throw new InvalidOperationException(
            "serialized ZRpc traffic is forbidden on a logical peer");
    public ZPackage Recv() => null;
    public int GetSendQueueSize() => 0;
    public int GetCurrentSendRate() => 0;
    public bool IsHost() => false;
    public void Dispose() => _connected = false;
    public bool GotNewData() => false;
    public void Close() => _connected = false;
    public string GetEndPointString() => "lumberjacks:" + _logicalPeerId;
    public void GetAndResetStats(out int totalSent, out int totalRecv) {
      totalSent = 0;
      totalRecv = 0;
    }
    public void GetConnectionQuality(
        out float localQuality, out float remoteQuality, out int ping,
        out float outByteSec, out float inByteSec) {
      localQuality = 1;
      remoteQuality = 1;
      ping = 0;
      outByteSec = 0;
      inByteSec = 0;
    }
    public ISocket Accept() => null;
    public int GetHostPort() => 0;
    public bool Flush() => true;
    public string GetHostName() => "logical-" + _peerUid;
    public void VersionMatch() { }
  }
}
