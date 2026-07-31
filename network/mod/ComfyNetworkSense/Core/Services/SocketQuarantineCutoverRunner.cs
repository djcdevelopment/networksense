namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

using UnityEngine;

/// <summary>
/// C7's early falsifier. It closes the already-established native client socket after
/// C1 and C5 are ready, then preserves only the minimal Valheim peer shell needed by
/// the local scene while the real session remains Lumberjacks-owned. No native byte
/// traffic is tunneled or replayed.
/// </summary>
public sealed class SocketQuarantineCutoverRunner : IDisposable {
  const string ReceiptFileName = "socket-quarantine-cutover.jsonl";
  static SocketQuarantineCutoverRunner _active;

  readonly LumberjacksGameSessionRunner _gameSession;
  readonly WorldZoneCutoverRunner _worldZone;
  readonly TelemetryLogWriter _writer = new();

  ZNetPeer _peer;
  ZRpc _rpc;
  ZSteamSocket _socket;
  string _actionId = string.Empty;
  string _connectionId = string.Empty;
  string _logicalPeerId = string.Empty;
  float _holdStartedAt;
  float _passAt;
  long _nativeBaseline;
  long _rpcUpdateSuppressed;
  long _rpcConnectedVirtualized;
  long _rpcInvokeSuppressed;
  long _socketSendSuppressed;
  long _socketRecvSuppressed;
  bool _started;
  bool _terminal;
  bool _success;
  bool _midpointWritten;
  string _detail = string.Empty;
  bool _disposed;

  public SocketQuarantineCutoverRunner(
      LumberjacksGameSessionRunner gameSession,
      WorldZoneCutoverRunner worldZone) {
    _gameSession = gameSession;
    _worldZone = worldZone;
    SocketQuarantineCutoverRunner previous =
        Interlocked.Exchange(ref _active, this);
    previous?.Dispose();
  }

  public static bool Selected =>
      PluginConfig.SocketQuarantineCutoverEnabled?.Value == true ||
      NativeAutotestRequest.ActiveSocketQuarantineCutover;

  public bool Begin(
      string actionId, float holdSeconds, out string detail) {
    detail = string.Empty;
    if (_disposed || !Selected) {
      detail = "socket_quarantine_cutover_not_enabled";
      return false;
    }
    if (_started) {
      detail = string.Equals(_actionId, actionId, StringComparison.Ordinal)
          ? "socket_quarantine_already_started"
          : "another_socket_quarantine_active";
      return string.Equals(_actionId, actionId, StringComparison.Ordinal);
    }
    if (!SafeToken(actionId, 80) ||
        holdSeconds is < 60.0f or > 120.0f) {
      detail = "socket_quarantine_parameters_invalid";
      return false;
    }
    if (_gameSession?.WebSocketConnected != true ||
        string.IsNullOrWhiteSpace(_gameSession.ConnectionId) ||
        string.IsNullOrWhiteSpace(_gameSession.LogicalPeerId)) {
      detail = "lumberjacks_session_not_ready";
      return false;
    }
    if (_worldZone?.DescriptorAccepted != true) {
      detail = "lumberjacks_world_descriptor_not_ready";
      return false;
    }
    if (ZNet.instance == null || ZNet.instance.IsServer() ||
        Player.m_localPlayer == null || ZNetScene.instance == null ||
        ZoneSystem.instance == null ||
        ZNet.GetConnectionStatus() != ZNet.ConnectionStatus.Connected) {
      detail = "valheim_scene_not_ready";
      return false;
    }

    ZNetPeer serverPeer = null;
    foreach (ZNetPeer candidate in ZNet.instance.GetPeers()) {
      if (candidate?.m_server == true && candidate.IsReady()) {
        serverPeer = candidate;
        break;
      }
    }
    if (serverPeer?.m_rpc == null ||
        serverPeer.m_socket is not ZSteamSocket steamSocket ||
        !steamSocket.IsConnected()) {
      detail = "connected_native_server_socket_not_found";
      return false;
    }

    _actionId = actionId;
    _peer = serverPeer;
    _rpc = serverPeer.m_rpc;
    _socket = steamSocket;
    _connectionId = _gameSession.ConnectionId;
    _logicalPeerId = _gameSession.LogicalPeerId;
    _started = true;
    Write(
        "quarantine_arming",
        "socket_type=ZSteamSocket"
        + " native_peer_uid=" + _peer.m_uid
        + " character_id=" + ZNet.instance.LocalPlayerCharacterID
        + " connection_id=" + _connectionId
        + " logical_peer_id=" + _logicalPeerId);

    // Mark the exact RPC/socket before Close. Its flush enters the same Harmony
    // quarantine and therefore cannot leak a final native packet after arming.
    _socket.Close();
    if (_socket.IsConnected()) {
      Fail("native_socket_close_failed");
      detail = _detail;
      return false;
    }

    NativeNetworkLedger.SetPoisonOverride(true);
    _nativeBaseline = NativeTotal();
    if (_nativeBaseline < 0) {
      Fail("native_ledger_unavailable");
      detail = _detail;
      return false;
    }
    _holdStartedAt = Time.unscaledTime;
    _passAt = _holdStartedAt + holdSeconds;
    Write(
        "quarantine_started",
        "hold_seconds="
        + holdSeconds.ToString("0.###", CultureInfo.InvariantCulture)
        + " underlying_socket_connected=false"
        + " native_total_baseline=" + _nativeBaseline
        + " connection_id=" + _connectionId
        + " logical_peer_id=" + _logicalPeerId);
    detail = "native_socket_closed_logical_peer_held";
    return true;
  }

  public void Update(float now) {
    if (!_started || _terminal || _disposed) return;

    string broken = BrokenInvariant();
    if (!string.IsNullOrEmpty(broken)) {
      Fail(broken);
      return;
    }

    float held = now - _holdStartedAt;
    if (!_midpointWritten && held >= 30.0f) {
      _midpointWritten = true;
      Write(
          "quarantine_midpoint",
          "held_ms=" + Milliseconds(held)
          + " native_total_delta=" + (NativeTotal() - _nativeBaseline)
          + " rpc_update_suppressed="
          + Interlocked.Read(ref _rpcUpdateSuppressed));
    }
    if (now < _passAt) return;

    long nativeDelta = NativeTotal() - _nativeBaseline;
    if (nativeDelta != 0) {
      Fail("native_funnel_activity_after_quarantine delta=" + nativeDelta);
      return;
    }
    if (Interlocked.Read(ref _rpcUpdateSuppressed) <= 0 ||
        Interlocked.Read(ref _rpcConnectedVirtualized) <= 0 ||
        Interlocked.Read(ref _socketSendSuppressed) <= 0) {
      Fail(
          "quarantine_interception_not_exercised"
          + " rpc_update=" + Interlocked.Read(ref _rpcUpdateSuppressed)
          + " rpc_connected="
          + Interlocked.Read(ref _rpcConnectedVirtualized)
          + " socket_send="
          + Interlocked.Read(ref _socketSendSuppressed));
      return;
    }

    _terminal = true;
    _success = true;
    _detail =
        "native_socket_quarantined_scene_held"
        + " held_ms=" + Milliseconds(now - _holdStartedAt)
        + " native_total_delta=0"
        + " rpc_update_suppressed="
        + Interlocked.Read(ref _rpcUpdateSuppressed)
        + " rpc_connected_virtualized="
        + Interlocked.Read(ref _rpcConnectedVirtualized)
        + " rpc_invoke_suppressed="
        + Interlocked.Read(ref _rpcInvokeSuppressed)
        + " socket_send_suppressed="
        + Interlocked.Read(ref _socketSendSuppressed)
        + " socket_recv_suppressed="
        + Interlocked.Read(ref _socketRecvSuppressed)
        + " native_fallback=false";
    Write("quarantine_passed", _detail);
  }

  public bool TryGetResult(
      string actionId,
      out bool terminal,
      out bool success,
      out string detail) {
    if (!_started ||
        !string.Equals(_actionId, actionId, StringComparison.Ordinal)) {
      terminal = false;
      success = false;
      detail = "socket_quarantine_not_found";
      return false;
    }
    terminal = _terminal;
    success = _success;
    detail = _detail;
    return true;
  }

  string BrokenInvariant() {
    if (_socket == null || _socket.IsConnected())
      return "native_socket_reconnected";
    if (_gameSession?.WebSocketConnected != true)
      return "lumberjacks_session_disconnected";
    if (!string.Equals(
            _gameSession.LogicalPeerId, _logicalPeerId,
            StringComparison.Ordinal))
      return "logical_peer_changed";
    if (ZNet.instance == null || ZNet.instance.IsServer())
      return "znet_client_missing";
    if (!ZNet.instance.GetPeers().Contains(_peer))
      return "logical_peer_shell_removed";
    if (!_peer.IsReady())
      return "logical_peer_shell_not_ready";
    if (ZNet.GetConnectionStatus() != ZNet.ConnectionStatus.Connected)
      return "valheim_connection_status_changed:"
          + ZNet.GetConnectionStatus();
    if (Player.m_localPlayer == null || ZNetScene.instance == null ||
        ZoneSystem.instance == null)
      return "valheim_scene_lost";
    return string.Empty;
  }

  void Fail(string detail) {
    _terminal = true;
    _success = false;
    _detail = detail ?? "socket_quarantine_failed";
    Write(
        "quarantine_failed",
        _detail
        + " held_ms=" + Milliseconds(Time.unscaledTime - _holdStartedAt)
        + " native_total_delta=" + (NativeTotal() - _nativeBaseline)
        + " native_fallback=false");
  }

  public static bool SuppressSocketSend(ZSteamSocket socket) {
    SocketQuarantineCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !ReferenceEquals(active._socket, socket))
      return false;
    Interlocked.Increment(ref active._socketSendSuppressed);
    return true;
  }

  public static bool SuppressSocketReceive(ZSteamSocket socket) {
    SocketQuarantineCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !ReferenceEquals(active._socket, socket))
      return false;
    Interlocked.Increment(ref active._socketRecvSuppressed);
    return true;
  }

  public static bool SuppressNativeInvoke(ZRpc rpc, string method) {
    SocketQuarantineCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !ReferenceEquals(active._rpc, rpc))
      return false;
    long count = Interlocked.Increment(ref active._rpcInvokeSuppressed);
    if (count <= 4 || IsPowerOfTwo(count)) {
      active.Write(
          "native_rpc_invoke_suppressed",
          "count=" + count + " method=" + SafeMarker(method));
    }
    return true;
  }

  public static bool TryVirtualizeRpcUpdate(
      ZRpc rpc, ref ZRpc.ErrorCode result) {
    SocketQuarantineCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !ReferenceEquals(active._rpc, rpc))
      return false;
    Interlocked.Increment(ref active._rpcUpdateSuppressed);
    result = ZRpc.ErrorCode.Success;
    return true;
  }

  public static void VirtualizeRpcConnected(ZRpc rpc, ref bool result) {
    SocketQuarantineCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !ReferenceEquals(active._rpc, rpc))
      return;
    Interlocked.Increment(ref active._rpcConnectedVirtualized);
    result = true;
  }

  public static void VirtualizeConnectionStatus(
      ref ZNet.ConnectionStatus result) {
    SocketQuarantineCutoverRunner active = Volatile.Read(ref _active);
    if (active == null || !active._started ||
        active._terminal || active._disposed)
      return;
    result = ZNet.ConnectionStatus.Connected;
  }

  void Write(string state, string detail) {
    Dictionary<string, object> row = new() {
        ["schema_version"] = 1,
        ["timestamp_utc"] =
            DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        ["state"] = state ?? string.Empty,
        ["run_id"] = NativeAutotestRequest.ActiveRunId,
        ["client"] = NativeAutotestRequest.ActiveClient,
        ["action_id"] = _actionId,
        ["detail"] = detail ?? string.Empty
    };
    _writer.Write(ReceiptFileName, row);
    ComfyNetworkSense.LogInfo(
        "SOCKET_QUARANTINE state=" + SafeMarker(state)
        + " run_id=" + SafeMarker(NativeAutotestRequest.ActiveRunId)
        + " client=" + SafeMarker(NativeAutotestRequest.ActiveClient)
        + " action=" + SafeMarker(_actionId)
        + " detail=" + SafeMarker(detail));
  }

  static long NativeTotal() {
    Dictionary<string, object> snapshot =
        NativeNetworkLedger.Active?.Snapshot();
    return snapshot != null &&
        snapshot.TryGetValue("native_total", out object value)
            ? Convert.ToInt64(value, CultureInfo.InvariantCulture)
            : -1;
  }

  static long Milliseconds(float seconds) =>
      (long)Math.Round(Math.Max(0.0, seconds) * 1000.0);

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
}
