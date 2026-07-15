namespace ComfyNetworkSense;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using HarmonyLib;

/// <summary>Applies queued Lumberjacks ZDO envelopes through Valheim's native receive funnel.</summary>
public sealed class ZdoAuthoritativeConsumerRunner : IDisposable {
  readonly ConcurrentQueue<ZdoRedirectEnvelopeCodec.Envelope> _queue = new();
  readonly ConcurrentQueue<long> _ackQueue = new();
  readonly HashSet<long> _seen = new();
  readonly HashSet<long> _queued = new();
  readonly HashSet<long> _ackQueued = new();
  readonly object _gate = new();
  MethodInfo _rpc;
  string _endpoint;
  string _window;
  int _polling;
  int _acking;
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
    // Redirect envelopes represent the server-to-client delivery path.  The dedicated
    // server produces them; only an enrolled connected client may consume them.
    if (!IsRunning || ZNet.instance == null || ZNet.instance.IsServer()
        || ZNet.instance.GetPeers() == null || ZNet.instance.GetPeers().Count == 0) return;
    if (now >= _nextPoll && Interlocked.CompareExchange(ref _polling, 1, 0) == 0) {
      _nextPoll = now + 1f; _ = Task.Run(Poll);
    }
    if (!_ackQueue.IsEmpty && Interlocked.CompareExchange(ref _acking, 1, 0) == 0)
      _ = Task.Run(FlushAcks);
    while (_queue.TryDequeue(out var envelope)) Apply(envelope);
  }

  void Poll() {
    try {
      string json = SendGet(_endpoint + "/valheim/zdo-redirect/pending/" + _window + "?limit=64");
      var response = ZdoRedirectEnvelopeCodec.Parse(json);
      foreach (var envelope in response.envelopes ?? Array.Empty<ZdoRedirectEnvelopeCodec.Envelope>()) {
        if (envelope.seq == 0) continue;
        lock (_gate) {
          if (_seen.Contains(envelope.seq)) {
            _duplicates++;
            QueueAckLocked(envelope.seq);
            continue;
          }
          if (!_queued.Add(envelope.seq)) { _duplicates++; continue; }
        }
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
      ZDO applied = ZDOMan.instance.GetZDO(new ZDOID(envelope.uid_user, (uint)envelope.uid_id));
      if (applied == null || applied.GetOwner() != envelope.owner
          || applied.OwnerRevision != (ushort)envelope.owner_rev
          || applied.DataRevision != (uint)envelope.data_rev
          || (envelope.prefab != 0 && applied.GetPrefab() != envelope.prefab))
        throw new InvalidOperationException("vanilla receive funnel did not produce the expected ZDO readback");
      lock (_gate) {
        _queued.Remove(envelope.seq);
        _seen.Add(envelope.seq);
        QueueAckLocked(envelope.seq);
      }
      _applied++;
    } catch (Exception exception) {
      lock (_gate) { _queued.Remove(envelope.seq); }
      _rejected++; _retried++;
      Exception root = exception is TargetInvocationException tie && tie.InnerException != null
          ? tie.InnerException : exception;
      ComfyNetworkSense.LogWarning("Authoritative consumer apply failed: " + root.GetType().Name + ": " + root.Message);
    }
  }

  void QueueAckLocked(long seq) {
    if (_ackQueued.Add(seq)) _ackQueue.Enqueue(seq);
  }

  void FlushAcks() {
    try {
      while (_ackQueue.TryDequeue(out long seq)) {
        try {
          string body = "[" + seq.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
          SendPost(_endpoint + "/valheim/zdo-redirect/ack/" + _window, body);
          lock (_gate) { _ackQueued.Remove(seq); }
        } catch (Exception exception) {
          lock (_gate) { _ackQueued.Remove(seq); }
          _retried++;
          ComfyNetworkSense.LogWarning("Authoritative consumer ack failed: " + exception.GetType().Name + ": " + exception.Message);
          break;
        }
      }
    } finally {
      Interlocked.Exchange(ref _acking, 0);
    }
  }

  static string SendGet(string url) => Send(url, "GET", string.Empty);
  static void SendPost(string url, string body) { Send(url, "POST", body); }

  static string Send(string url, string method, string body) {
    Uri uri = new(url);
    byte[] bytes = Encoding.UTF8.GetBytes(body ?? string.Empty);
    using TcpClient client = new();
    client.Connect(uri.Host, uri.Port);
    using NetworkStream stream = client.GetStream();
    string request = method + " " + uri.PathAndQuery + " HTTP/1.1\r\nHost: " + uri.Host
        + "\r\nContent-Type: application/json\r\nContent-Length: " + bytes.Length
        + "\r\nConnection: close\r\n\r\n";
    byte[] header = Encoding.ASCII.GetBytes(request);
    stream.Write(header, 0, header.Length);
    if (bytes.Length > 0) stream.Write(bytes, 0, bytes.Length);
    stream.Flush();
    byte[] buffer = new byte[8192];
    StringBuilder response = new();
    int read;
    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
      response.Append(Encoding.UTF8.GetString(buffer, 0, read));
    string raw = response.ToString();
    int split = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
    if (!raw.StartsWith("HTTP/1.1 2", StringComparison.Ordinal))
      throw new InvalidOperationException("HTTP request failed: " + (raw.Split('\n')[0] ?? ""));
    if (split < 0) return string.Empty;
    string headers = raw.Substring(0, split);
    string payload = raw.Substring(split + 4);
    if (headers.IndexOf("transfer-encoding: chunked", StringComparison.OrdinalIgnoreCase) >= 0)
      return DecodeChunked(payload);
    return payload;
  }

  static string DecodeChunked(string payload) {
    StringBuilder result = new();
    int offset = 0;
    while (offset < payload.Length) {
      int end = payload.IndexOf("\r\n", offset, StringComparison.Ordinal);
      if (end < 0) break;
      int size = Convert.ToInt32(payload.Substring(offset, end - offset).Trim(), 16);
      if (size == 0) break;
      offset = end + 2;
      if (offset + size > payload.Length) break;
      result.Append(payload, offset, size);
      offset += size + 2;
    }
    return result.ToString();
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
