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
  const int MaxAppliesPerUpdate = 64;
  const int MaxApplyAttempts = 30;
  const int PollBatchMax = 256;

  readonly ConcurrentQueue<ZdoRedirectEnvelopeCodec.Envelope> _queue = new();
  readonly ConcurrentQueue<long> _ackQueue = new();
  readonly HashSet<long> _seen = new();
  readonly HashSet<long> _queued = new();
  readonly HashSet<long> _failed = new();
  readonly HashSet<long> _ackQueued = new();
  readonly Dictionary<long, int> _applyAttempts = new();
  readonly Dictionary<long, PendingRetry> _pendingRetries = new();
  readonly object _gate = new();
  MethodInfo _rpc;
  string _endpoint;
  string _window;
  string _consumerId;
  string _lastTelemetryError = string.Empty;
  int _polling;
  int _acking;
  float _nextPoll;
  long _applied;
  long _superseded;
  long _rejected;
  long _duplicates;
  long _retried;
  long _acknowledged;

  public bool IsRunning { get; private set; }
  public long Applied => _applied;
  public long Superseded => _superseded;
  public long Rejected => _rejected;
  public long Duplicates => _duplicates;
  public long Retried => _retried;

  public string Start(string endpoint, string windowId) {
    if (IsRunning) return "authoritative consumer already running";
    _rpc = AccessTools.Method(typeof(ZDOMan), "RPC_ZDOData", new[] { typeof(ZRpc), typeof(ZPackage) });
    if (_rpc == null) return "RPC_ZDOData reflection handle unavailable";
    _endpoint = endpoint.TrimEnd('/'); _window = windowId;
    _consumerId = Guid.NewGuid().ToString("N").Substring(0, 12);
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

    PromoteDueRetries(now);
    int processed = 0;
    while (processed++ < MaxAppliesPerUpdate && _queue.TryDequeue(out var envelope))
      Apply(envelope, now);
  }

  void Poll() {
    try {
      string json = SendGet(_endpoint + "/valheim/zdo-redirect/pending/" + _window + "?limit=" + PollBatchMax);
      var response = ZdoRedirectEnvelopeCodec.Parse(json);
      foreach (var envelope in response.envelopes ?? Array.Empty<ZdoRedirectEnvelopeCodec.Envelope>()) {
        if (envelope.seq == 0) continue;
        lock (_gate) {
          if (_seen.Contains(envelope.seq)) {
            _duplicates++;
            QueueAckLocked(envelope.seq);
            continue;
          }
          if (_failed.Contains(envelope.seq) || !_queued.Add(envelope.seq)) continue;
        }
        _queue.Enqueue(envelope);
      }
    } catch (Exception exception) {
      _retried++;
      ComfyNetworkSense.LogWarning("Authoritative consumer poll failed: " + exception.GetType().Name + ": " + exception.Message);
    }
    finally {
      PostTelemetry();
      Interlocked.Exchange(ref _polling, 0);
    }
  }

  void Apply(ZdoRedirectEnvelopeCodec.Envelope envelope, float now) {
    try {
      ZPackage packet = ZdoRedirectEnvelopeCodec.BuildPacket(envelope);
      ZRpc applyRpc = null;
      foreach (ZNetPeer peer in ZNet.instance.GetPeers()) {
        if (peer?.m_rpc != null) { applyRpc = peer.m_rpc; break; }
      }
      if (applyRpc == null) throw new InvalidOperationException("no connected peer RPC available");
      _rpc.Invoke(ZDOMan.instance, new object[] { applyRpc, packet });
      ZDO applied = ZDOMan.instance.GetZDO(new ZDOID(envelope.uid_user, (uint)envelope.uid_id));
      ReadbackResult readback = ValidateReadback(envelope, applied);
      if (readback.Error != null) throw new InvalidOperationException(readback.Error);
      lock (_gate) {
        _queued.Remove(envelope.seq);
        _applyAttempts.Remove(envelope.seq);
        _pendingRetries.Remove(envelope.seq);
        _seen.Add(envelope.seq);
        QueueAckLocked(envelope.seq);
      }
      if (readback.Superseded) _superseded++;
      else _applied++;
    } catch (Exception exception) {
      Exception root = exception is TargetInvocationException tie && tie.InnerException != null
          ? tie.InnerException : exception;
      int attempt;
      bool terminal;
      lock (_gate) {
        attempt = _applyAttempts.TryGetValue(envelope.seq, out int previous) ? previous + 1 : 1;
        _applyAttempts[envelope.seq] = attempt;
        terminal = attempt >= MaxApplyAttempts;
        if (terminal) {
          _queued.Remove(envelope.seq);
          _pendingRetries.Remove(envelope.seq);
          _failed.Add(envelope.seq);
        } else {
          float delay = Math.Min(2.0f, 0.05f * (1 << Math.Min(attempt, 5)));
          _pendingRetries[envelope.seq] = new PendingRetry(envelope, now + delay);
        }
      }
      _retried++;
      if (terminal) _rejected++;
      if (attempt == 1 || attempt % 5 == 0 || terminal) {
        ComfyNetworkSense.LogWarning(
            $"Authoritative consumer apply {(terminal ? "rejected" : "retry")}: "
            + $"seq={envelope.seq} attempt={attempt}/{MaxApplyAttempts} "
            + root.GetType().Name + ": " + root.Message);
      }
    }
  }

  void PromoteDueRetries(float now) {
    List<long> dueSequences = new();
    List<ZdoRedirectEnvelopeCodec.Envelope> dueEnvelopes = new();
    lock (_gate) {
      foreach (KeyValuePair<long, PendingRetry> pair in _pendingRetries) {
        if (pair.Value.DueAt > now) continue;
        dueSequences.Add(pair.Key);
        dueEnvelopes.Add(pair.Value.Envelope);
        if (dueEnvelopes.Count >= MaxAppliesPerUpdate) break;
      }
      foreach (long sequence in dueSequences) _pendingRetries.Remove(sequence);
    }
    foreach (ZdoRedirectEnvelopeCodec.Envelope envelope in dueEnvelopes) _queue.Enqueue(envelope);
  }

  static ReadbackResult ValidateReadback(ZdoRedirectEnvelopeCodec.Envelope expected, ZDO actual) {
    if (actual == null)
      return new(false, $"readback missing uid={expected.uid_user}:{expected.uid_id}");
    long owner = actual.GetOwner();
    int ownerRevision = actual.OwnerRevision;
    long dataRevision = actual.DataRevision;
    int prefab = actual.GetPrefab();
    if (owner == expected.owner && ownerRevision == expected.owner_rev
        && dataRevision == expected.data_rev
        && (expected.prefab == 0 || prefab == expected.prefab)) return new(false, null);

    // RPC_ZDOData intentionally refuses an obsolete revision. A matching ZDO with
    // either monotonic revision already ahead is safely reconciled: applying the
    // envelope would roll state backward, while acknowledging it lets the queue drain.
    bool samePrefab = expected.prefab == 0 || prefab == expected.prefab;
    bool newerRevision = dataRevision > (uint)expected.data_rev
        || ownerRevision > (ushort)expected.owner_rev;
    if (samePrefab && newerRevision) return new(true, null);

    return new(false, $"readback mismatch uid={expected.uid_user}:{expected.uid_id} "
        + $"owner={owner}/{expected.owner} owner_rev={ownerRevision}/{expected.owner_rev} "
        + $"data_rev={dataRevision}/{expected.data_rev} prefab={prefab}/{expected.prefab}");
  }

  void QueueAckLocked(long seq) {
    if (_ackQueued.Add(seq)) _ackQueue.Enqueue(seq);
  }

  void FlushAcks() {
    try {
      while (!_ackQueue.IsEmpty) {
        List<long> batch = new(256);
        while (batch.Count < 256 && _ackQueue.TryDequeue(out long seq)) batch.Add(seq);
        if (batch.Count == 0) break;
        try {
          string body = "[" + string.Join(",", batch) + "]";
          SendPost(_endpoint + "/valheim/zdo-redirect/ack/" + _window, body);
          lock (_gate) {
            foreach (long acknowledged in batch) _ackQueued.Remove(acknowledged);
          }
          Interlocked.Add(ref _acknowledged, batch.Count);
        } catch (Exception exception) {
          lock (_gate) {
            foreach (long failed in batch) _ackQueued.Remove(failed);
          }
          _retried++;
          ComfyNetworkSense.LogWarning("Authoritative consumer ack failed: " + exception.GetType().Name + ": " + exception.Message);
          break;
        }
      }
    } finally {
      Interlocked.Exchange(ref _acking, 0);
    }
  }

  void PostTelemetry() {
    try {
      int pending;
      lock (_gate) pending = _queue.Count + _ackQueue.Count + _pendingRetries.Count + _failed.Count;
      Dictionary<string, object> payload = new() {
          ["window_id"] = _window,
          ["consumer_id"] = _consumerId,
          ["mod_version"] = ComfyNetworkSense.PluginVersion,
          ["timestamp_utc"] = DateTime.UtcNow.ToString("O"),
          ["applied"] = Interlocked.Read(ref _applied),
          ["superseded"] = Interlocked.Read(ref _superseded),
          ["acknowledged"] = Interlocked.Read(ref _acknowledged),
          ["rejected"] = Interlocked.Read(ref _rejected),
          ["duplicates"] = Interlocked.Read(ref _duplicates),
          ["retried"] = Interlocked.Read(ref _retried),
          ["pending"] = pending
      };
      SendPost(_endpoint + "/valheim/zdo-redirect/consumer", JsonLineSerializer.Serialize(payload));
      _lastTelemetryError = string.Empty;
    } catch (Exception exception) {
      string error = exception.GetType().Name + ": " + exception.Message;
      if (!string.Equals(error, _lastTelemetryError, StringComparison.Ordinal))
        ComfyNetworkSense.LogWarning("Authoritative consumer telemetry failed: " + error);
      _lastTelemetryError = error;
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

  public Dictionary<string, object> Snapshot() {
    int pending;
    lock (_gate) pending = _queue.Count + _ackQueue.Count + _pendingRetries.Count + _failed.Count;
    return new() {
      ["authoritative_enabled"] = IsRunning,
      ["applied"] = _applied,
      ["superseded"] = _superseded,
      ["rejected"] = _rejected,
      ["duplicates"] = _duplicates,
      ["retried"] = _retried,
      ["acknowledged"] = _acknowledged,
      ["pending"] = pending
    };
  }

  sealed class PendingRetry {
    public readonly ZdoRedirectEnvelopeCodec.Envelope Envelope;
    public readonly float DueAt;
    public PendingRetry(ZdoRedirectEnvelopeCodec.Envelope envelope, float dueAt) {
      Envelope = envelope;
      DueAt = dueAt;
    }
  }

  sealed class ReadbackResult {
    public readonly bool Superseded;
    public readonly string Error;
    public ReadbackResult(bool superseded, string error) {
      Superseded = superseded;
      Error = error;
    }
  }

  public void Dispose() { IsRunning = false; }
}
