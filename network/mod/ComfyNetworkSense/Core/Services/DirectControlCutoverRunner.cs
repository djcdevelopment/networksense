namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

using UnityEngine;

/// <summary>
/// C2a's selected post-join direct ZRpc control. The dedicated server continues attempting the
/// native pulse while cutover is armed, but the ZRpc.Invoke prefix suppresses that exact method.
/// The client handler remains registered as the negative-control tripwire.
/// </summary>
public sealed class DirectControlCutoverRunner : IDisposable {
  public const string NativePulseMethod = "ComfyNetworkSense_CutoverDirectPulse";
  const string ReceiptFileName = "direct-control-cutover.jsonl";
  const float PulseIntervalSeconds = 1.0f;

  static DirectControlCutoverRunner _active;

  readonly TelemetryLogWriter _writer = new();
  float _nextPulseAt;
  long _nextSequence;
  long _attempted;
  long _suppressed;
  long _nativeReceived;
  bool _disposed;

  public DirectControlCutoverRunner() {
    DirectControlCutoverRunner previous = Interlocked.Exchange(ref _active, this);
    previous?.Dispose();
  }

  public void Update(float now) {
    if (_disposed || PluginConfig.DirectControlCutoverEnabled?.Value != true
        || ZNet.instance == null || !ZNet.instance.IsServer()
        || now < _nextPulseAt) return;
    _nextPulseAt = now + PulseIntervalSeconds;

    string runId = CurrentRunId();
    foreach (ZNetPeer peer in ZNet.instance.GetPeers()) {
      if (peer?.m_rpc == null) continue;
      long sequence = Interlocked.Increment(ref _nextSequence);
      Interlocked.Increment(ref _attempted);
      Write("native_attempted", runId, sequence, peer.m_uid, string.Empty);
      peer.m_rpc.Invoke(NativePulseMethod, runId, sequence);
    }
  }

  public static void RegisterPeer(ZNetPeer peer) {
    if (peer?.m_rpc == null) return;
    peer.m_rpc.Register<string, long>(
        NativePulseMethod,
        new Action<ZRpc, string, long>(ReceiveNativePulse));
    DirectControlCutoverRunner active = Volatile.Read(ref _active);
    active?.Write(
        "native_handler_registered",
        CurrentRunId(),
        0,
        peer.m_uid,
        "negative_control_tripwire_ready");
  }

  public static bool SuppressNativeInvoke(string method, object[] parameters) {
    if (!string.Equals(method, NativePulseMethod, StringComparison.Ordinal)
        || PluginConfig.DirectControlCutoverEnabled?.Value != true
        || ZNet.instance == null || !ZNet.instance.IsServer()) return false;

    string runId = parameters != null && parameters.Length > 0
        ? parameters[0] as string ?? CurrentRunId()
        : CurrentRunId();
    long sequence = 0;
    if (parameters != null && parameters.Length > 1) {
      try { sequence = Convert.ToInt64(parameters[1], CultureInfo.InvariantCulture); }
      catch { }
    }
    DirectControlCutoverRunner active = Volatile.Read(ref _active);
    if (active != null) {
      Interlocked.Increment(ref active._suppressed);
      active.Write("native_suppressed", runId, sequence, 0, "before_zrpc_invoke");
    }
    return true;
  }

  static void ReceiveNativePulse(ZRpc rpc, string runId, long sequence) {
    DirectControlCutoverRunner active = Volatile.Read(ref _active);
    if (active != null) {
      Interlocked.Increment(ref active._nativeReceived);
      active.Write("native_received", runId, sequence, 0, "unexpected_native_fallback");
    }
    NativeNetworkLedger.Observe(
        "direct_control_pulse_receive", "inbound", NativePulseMethod);
    LumberjacksGameSessionRunner.NotifyNativeDirectPulse(runId, sequence);
  }

  public IDictionary<string, object> Snapshot() =>
      new Dictionary<string, object> {
          ["enabled"] = PluginConfig.DirectControlCutoverEnabled?.Value == true,
          ["attempted"] = Interlocked.Read(ref _attempted),
          ["suppressed"] = Interlocked.Read(ref _suppressed),
          ["native_received"] = Interlocked.Read(ref _nativeReceived),
          ["next_sequence"] = Interlocked.Read(ref _nextSequence)
      };

  void Write(string state, string runId, long sequence, long peerId, string detail) {
    _writer.Write(
        ReceiptFileName,
        new Dictionary<string, object> {
            ["schema_version"] = 1,
            ["timestamp_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["state"] = state,
            ["run_id"] = SafeToken(runId, 80) ? runId : "unscoped",
            ["role"] = Role(),
            ["sequence"] = sequence,
            ["peer_present"] = peerId != 0,
            ["detail"] = detail ?? string.Empty
        });
  }

  static string CurrentRunId() {
    string request = NativeAutotestRequest.ActiveRunId;
    if (SafeToken(request, 80)) return request;
    string configured = PluginConfig.NativeNetworkEvidenceRunId?.Value;
    return SafeToken(configured, 80) ? configured.Trim() : "unscoped";
  }

  static string Role() {
    try {
      if (ZNet.instance == null) return "starting";
      return ZNet.instance.IsServer() ? "server" : "client";
    } catch {
      return "unknown";
    }
  }

  static bool SafeToken(string value, int maxLength) {
    if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength) return false;
    foreach (char character in value)
      if (!char.IsLetterOrDigit(character)
          && character is not '-' and not '_' and not '.')
        return false;
    return true;
  }

  public void Dispose() {
    if (_disposed) return;
    _disposed = true;
    Interlocked.CompareExchange(ref _active, null, this);
    _writer.Dispose();
  }
}
