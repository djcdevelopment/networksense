namespace ComfyNetworkSense;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using HarmonyLib;

/// <summary>Applies queued Lumberjacks ZDO envelopes through Valheim's native receive funnel.</summary>
public sealed class ZdoAuthoritativeConsumerRunner : IDisposable {
  readonly ConcurrentQueue<ZdoRedirectEnvelopeCodec.Envelope> _queue = new();
  readonly HashSet<long> _seen = new();
  readonly object _gate = new();
  MethodInfo _rpc;
  string _endpoint;
  string _window;
  int _polling;
  float _nextPoll;
  long _applied;
  long _rejected;
  long _duplicates;
  long _retried;

  public bool IsRunning { get; private set; }
  public long Applied => _applied;
  public long Rejected => _rejected;
  public long Duplicates => _duplicates;
  public long Retried => _retried;

  public string Start(string endpoint, string windowId) {
    if (IsRunning) return "authoritative consumer already running";
    _rpc = AccessTools.Method(typeof(ZDOMan), "RPC_ZDOData", new[] { typeof(ZRpc), typeof(ZPackage) });
    if (_rpc == null) return "RPC_ZDOData reflection handle unavailable";
    _endpoint = endpoint.TrimEnd('/'); _window = windowId;
    IsRunning = true; _nextPoll = 0; return "authoritative consumer armed";
  }

  public void Update(float now) {
    if (!IsRunning || ZNet.instance == null || !ZNet.instance.IsServer()) return;
    if (now >= _nextPoll && Interlocked.CompareExchange(ref _polling, 1, 0) == 0) {
      _nextPoll = now + 1f; _ = Task.Run(Poll);
    }
    while (_queue.TryDequeue(out var envelope)) Apply(envelope);
  }

  async Task Poll() {
    try {
      using WebClient client = new();
      string json = client.DownloadString(_endpoint + "/valheim/zdo-redirect/pending/" + _window + "?limit=64");
      var response = ZdoRedirectEnvelopeCodec.Parse(json);
      foreach (var envelope in response.envelopes ?? Array.Empty<ZdoRedirectEnvelopeCodec.Envelope>()) {
        if (envelope.seq is null) continue;
        lock (_gate) { if (!_seen.Add(envelope.seq.Value)) { _duplicates++; continue; } }
        _queue.Enqueue(envelope);
      }
    } catch (Exception exception) {
      _retried++;
      ComfyNetworkSense.LogWarning("Authoritative consumer poll failed: " + exception.GetType().Name + ": " + exception.Message);
    }
    finally { Interlocked.Exchange(ref _polling, 0); }
  }

  void Apply(ZdoRedirectEnvelopeCodec.Envelope envelope) {
    try {
      ZPackage packet = ZdoRedirectEnvelopeCodec.BuildPacket(envelope);
      ZRpc applyRpc = null;
      foreach (ZNetPeer peer in ZNet.instance.GetPeers()) {
        if (peer?.m_rpc != null) { applyRpc = peer.m_rpc; break; }
      }
      if (applyRpc == null) throw new InvalidOperationException("no connected peer RPC available");
      _rpc.Invoke(ZDOMan.instance, new object[] { applyRpc, packet });
      _applied++;
      Ack(envelope.seq.Value, true);
    } catch (Exception exception) {
      _rejected++; _retried++;
      ComfyNetworkSense.LogWarning("Authoritative consumer apply failed: " + exception.GetType().Name + ": " + exception.Message);
    }
  }

  void Ack(long seq, bool applied) {
    try {
      using WebClient client = new();
      string body = "[" + seq.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
      client.Headers[HttpRequestHeader.ContentType] = "application/json";
      client.UploadString(_endpoint + "/valheim/zdo-redirect/ack/" + _window, "POST", body);
    } catch (Exception exception) {
      _retried++;
      ComfyNetworkSense.LogWarning("Authoritative consumer ack failed: " + exception.GetType().Name + ": " + exception.Message);
    }
  }

  public Dictionary<string, object> Snapshot() => new() {
      ["authoritative_enabled"] = IsRunning,
      ["applied"] = _applied,
      ["rejected"] = _rejected,
      ["duplicates"] = _duplicates,
      ["retried"] = _retried,
      ["pending"] = _queue.Count
  };

  public void Dispose() { IsRunning = false; }
}
