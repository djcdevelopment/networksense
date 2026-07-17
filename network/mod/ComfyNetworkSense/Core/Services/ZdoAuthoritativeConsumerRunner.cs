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
  const int MaxAppliesPerUpdate = 64;
  const int MaxApplyAttempts = 30;
  const int PollBatchMax = 1024;
  const int AckBatchMax = 1024;
  const int HttpTimeoutMs = 5000;
  const int MaxResponseBytes = 16 * 1024 * 1024;

  // Wall-clock ceiling on reading a response. This loop was size-bounded but never time-bounded, so
  // it carried the same defect the handshake did (M1 risk 10): ReceiveTimeout is per-Read, so a peer
  // trickling one byte under each timeout resets it forever and holds the read open indefinitely.
  // Off the main thread it cannot freeze the server, which made it quieter and worse: Poll runs
  // inside Task.Run and resets _polling in a finally a wedged read never reaches, so the CAS in
  // Update never admits another poll and the consumer stops polling FOR GOOD, with no error - the
  // only tell being poll_in_flight stuck true. And because a seat's liveness is inferred from this
  // very poll/ack traffic, a degraded Gateway would wedge the consumer and then expire the seat it
  // was meant to protect. Generous next to a healthy poll, so it cannot alter healthy timing; it is
  // a ceiling, not a schedule.
  const int ResponseDeadlineMs = 5000;

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
  string _state = "stopped";
  string _lastPollUtc = string.Empty;
  string _lastApplyUtc = string.Empty;
  string _lastAckUtc = string.Empty;
  string _lastTelemetryUtc = string.Empty;
  string _lastPollError = string.Empty;
  string _lastApplyError = string.Empty;
  string _lastAckError = string.Empty;
  string _lastTelemetryError = string.Empty;
  int _polling;
  int _acking;
  int _connectedPeers;
  float _nextPoll;
  long _polls;
  long _pollFailures;
  long _ackFailures;
  long _telemetryFailures;
  long _lastPolledCount;
  long _lastAppliedSeq;
  long _lastAcknowledgedSeq;
  long _applied;
  long _superseded;
  long _rejected;
  long _duplicates;
  long _retried;
  long _acknowledged;
  long _priorityTagged;
  long _priorityFastLaneApplied;
  string _lastPriorityTier = string.Empty;
  int _lastPriorityRank = int.MaxValue;

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
    // Deliberately mints NO consumer id. This used to be Guid.NewGuid(), which meant the client
    // chose the name it was filed under and the Gateway trusted it - a recipient you can select is
    // not an identity. The Gateway now derives the recipient from the credential presented
    // (ValheimZdoRedirectEndpoints /consumer), so there is nothing here to send and nothing to
    // forge. If a future change wants to name this consumer, that name belongs to the server.
    IsRunning = true; _nextPoll = 0;
    lock (_gate) _state = "waiting-for-peer";
    return "authoritative consumer armed";
  }

  public void Update(float now) {
    // Redirect envelopes represent the server-to-client delivery path.  The dedicated
    // server produces them; only an enrolled connected client may consume them.
    if (!IsRunning) return;
    int peers = ZNet.instance?.GetPeers()?.Count ?? 0;
    _connectedPeers = peers;
    if (ZNet.instance == null || ZNet.instance.IsServer() || peers == 0) {
      lock (_gate) _state = ZNet.instance != null && ZNet.instance.IsServer()
          ? "server-not-consumer" : "waiting-for-peer";
      return;
    }
    PromoteDueRetries(now);
    int processed = 0;
    while (processed++ < MaxAppliesPerUpdate && _queue.TryDequeue(out var envelope))
      Apply(envelope, now);

    lock (_gate) _state = _queue.IsEmpty && _pendingRetries.Count == 0
        ? "polling" : "draining";

    // Finish and acknowledge one bounded delivery batch before asking for the next.
    // This removes the old 1 Hz / 256-envelope ceiling and avoids repeatedly polling
    // the same oldest unacknowledged records while Unity is still applying them.
    if (ShouldFlushAcks() && Interlocked.CompareExchange(ref _acking, 1, 0) == 0)
      _ = Task.Run(FlushAcks);

    if (ReadyToPoll() && now >= _nextPoll
        && Interlocked.CompareExchange(ref _polling, 1, 0) == 0) {
      _nextPoll = now + (Interlocked.Read(ref _lastPolledCount) == 0 ? 0.5f : 0.05f);
      _ = Task.Run(Poll);
    }
  }

  void Poll() {
    try {
      string json = SendGet(_endpoint + "/valheim/zdo-redirect/pending/" + _window + "?limit=" + PollBatchMax);
      var response = ZdoRedirectEnvelopeCodec.Parse(json);
      ZdoRedirectEnvelopeCodec.Envelope[] envelopes =
          response.envelopes ?? Array.Empty<ZdoRedirectEnvelopeCodec.Envelope>();
      Interlocked.Increment(ref _polls);
      Interlocked.Exchange(ref _lastPolledCount, envelopes.Length);
      lock (_gate) {
        _lastPollUtc = DateTime.UtcNow.ToString("O");
        _lastPollError = string.Empty;
      }
      foreach (var envelope in envelopes) {
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
      Interlocked.Increment(ref _pollFailures);
      _retried++;
      lock (_gate) {
        _lastPollUtc = DateTime.UtcNow.ToString("O");
        _lastPollError = exception.GetType().Name + ": " + exception.Message;
        _state = "poll-error";
      }
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
      if (!string.IsNullOrWhiteSpace(envelope.priority_tier)) {
        _priorityTagged++;
        if (envelope.priority_rank <= 4) _priorityFastLaneApplied++;
        lock (_gate) {
          _lastPriorityTier = envelope.priority_tier;
          _lastPriorityRank = envelope.priority_rank;
        }
      }
      Interlocked.Exchange(ref _lastAppliedSeq, envelope.seq);
      lock (_gate) {
        _lastApplyUtc = DateTime.UtcNow.ToString("O");
        _lastApplyError = string.Empty;
      }
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
      lock (_gate) {
        _lastApplyUtc = DateTime.UtcNow.ToString("O");
        _lastApplyError = root.GetType().Name + ": " + root.Message;
        if (terminal) _state = "apply-rejected";
      }
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

    // RPC_ZDOData intentionally refuses an obsolete or equal revision. If the local
    // object is already monotonic in both revision dimensions, its post-call state is
    // the native receive funnel's decision and the envelope is safely reconciled.
    // This also handles a prior server epoch reusing a sequence/UID after the ZDO was
    // destroyed and replaced by a different prefab. A lower local revision is never
    // acknowledged: it remains a retry/terminal rejection.
    bool monotonicRevision = dataRevision >= (uint)expected.data_rev
        && ownerRevision >= (ushort)expected.owner_rev;
    if (monotonicRevision) return new(true, null);

    return new(false, $"readback mismatch uid={expected.uid_user}:{expected.uid_id} "
        + $"owner={owner}/{expected.owner} owner_rev={ownerRevision}/{expected.owner_rev} "
        + $"data_rev={dataRevision}/{expected.data_rev} prefab={prefab}/{expected.prefab}");
  }

  void QueueAckLocked(long seq) {
    if (_ackQueued.Add(seq)) _ackQueue.Enqueue(seq);
  }

  bool ShouldFlushAcks() {
    lock (_gate) {
      if (_ackQueue.IsEmpty) return false;
      return _ackQueue.Count >= AckBatchMax
          || (_queue.IsEmpty && _pendingRetries.Count == 0);
    }
  }

  bool ReadyToPoll() {
    lock (_gate) {
      return _queue.IsEmpty && _pendingRetries.Count == 0 && _ackQueue.IsEmpty
          && Interlocked.CompareExchange(ref _acking, 0, 0) == 0;
    }
  }

  void FlushAcks() {
    try {
      while (!_ackQueue.IsEmpty) {
        List<long> batch = new(AckBatchMax);
        while (batch.Count < AckBatchMax && _ackQueue.TryDequeue(out long seq)) batch.Add(seq);
        if (batch.Count == 0) break;
        try {
          string body = "[" + string.Join(",", batch) + "]";
          SendPost(_endpoint + "/valheim/zdo-redirect/ack/" + _window, body);
          lock (_gate) {
            foreach (long acknowledged in batch) _ackQueued.Remove(acknowledged);
          }
          Interlocked.Add(ref _acknowledged, batch.Count);
          Interlocked.Exchange(ref _lastAcknowledgedSeq, batch[batch.Count - 1]);
          lock (_gate) {
            _lastAckUtc = DateTime.UtcNow.ToString("O");
            _lastAckError = string.Empty;
          }
        } catch (Exception exception) {
          lock (_gate) {
            foreach (long failed in batch) _ackQueued.Remove(failed);
          }
          Interlocked.Increment(ref _ackFailures);
          _retried++;
          lock (_gate) {
            _lastAckUtc = DateTime.UtcNow.ToString("O");
            _lastAckError = exception.GetType().Name + ": " + exception.Message;
            _state = "ack-error";
          }
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
          ["mod_version"] = ComfyNetworkSense.PluginVersion,
          ["timestamp_utc"] = DateTime.UtcNow.ToString("O"),
          ["applied"] = Interlocked.Read(ref _applied),
          ["superseded"] = Interlocked.Read(ref _superseded),
          ["acknowledged"] = Interlocked.Read(ref _acknowledged),
          ["rejected"] = Interlocked.Read(ref _rejected),
          ["duplicates"] = Interlocked.Read(ref _duplicates),
          ["retried"] = Interlocked.Read(ref _retried),
          ["priority_tagged"] = Interlocked.Read(ref _priorityTagged),
          ["priority_fast_lane_applied"] = Interlocked.Read(ref _priorityFastLaneApplied),
          ["pending"] = pending
      };
      SendPost(_endpoint + "/valheim/zdo-redirect/consumer", JsonLineSerializer.Serialize(payload));
      lock (_gate) {
        _lastTelemetryUtc = DateTime.UtcNow.ToString("O");
        _lastTelemetryError = string.Empty;
      }
    } catch (Exception exception) {
      Interlocked.Increment(ref _telemetryFailures);
      string error = exception.GetType().Name + ": " + exception.Message;
      if (!string.Equals(error, _lastTelemetryError, StringComparison.Ordinal))
        ComfyNetworkSense.LogWarning("Authoritative consumer telemetry failed: " + error);
      lock (_gate) {
        _lastTelemetryUtc = DateTime.UtcNow.ToString("O");
        _lastTelemetryError = error;
      }
    }
  }

  static string SendGet(string url) => Send(url, "GET", string.Empty);
  static void SendPost(string url, string body) { Send(url, "POST", body); }

  static string Send(string url, string method, string body) {
    Uri uri = new(url);
    byte[] bytes = Encoding.UTF8.GetBytes(body ?? string.Empty);
    // The head stays here, byte-for-byte as before: the credential headers come from PluginConfig
    // (BepInEx-bound, so it cannot move into BoundedRawHttp) and the port-less Host is what the
    // Gateway has always been sent. Only the socket and the read loop are shared.
    string request = method + " " + uri.PathAndQuery + " HTTP/1.1\r\nHost: " + uri.Host
        + "\r\nContent-Type: application/json\r\nContent-Length: " + bytes.Length
        + (string.IsNullOrWhiteSpace(PluginConfig.LumberjacksClientAccessKey.Value) ? string.Empty : "\r\nX-Lumberjacks-Client-Key: " + PluginConfig.LumberjacksClientAccessKey.Value)
        + (string.IsNullOrWhiteSpace(PluginConfig.LumberjacksEnrollmentId.Value) ? string.Empty : "\r\nX-Lumberjacks-Enrollment-Id: " + PluginConfig.LumberjacksEnrollmentId.Value)
        + "\r\nConnection: close\r\n\r\n";
    byte[] header = Encoding.ASCII.GetBytes(request);
    string raw = BoundedRawHttp.SendBounded(
        uri, header, bytes, HttpTimeoutMs, ResponseDeadlineMs, MaxResponseBytes);
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
    lock (_gate) {
      int pending = _queue.Count + _ackQueue.Count + _pendingRetries.Count + _failed.Count;
      return new() {
        ["authoritative_enabled"] = IsRunning,
        ["state"] = _state,
        ["window_id"] = _window ?? string.Empty,
        ["connected_peers"] = _connectedPeers,
        ["poll_in_flight"] = _polling != 0,
        ["ack_in_flight"] = _acking != 0,
        ["polls"] = Interlocked.Read(ref _polls),
        ["poll_failures"] = Interlocked.Read(ref _pollFailures),
        ["last_polled_count"] = Interlocked.Read(ref _lastPolledCount),
        ["poll_batch_max"] = PollBatchMax,
        ["ack_batch_max"] = AckBatchMax,
        ["max_applies_per_update"] = MaxAppliesPerUpdate,
        ["last_poll_utc"] = _lastPollUtc,
        ["last_poll_error"] = _lastPollError,
        ["last_apply_utc"] = _lastApplyUtc,
        ["last_apply_error"] = _lastApplyError,
        ["last_applied_seq"] = Interlocked.Read(ref _lastAppliedSeq),
        ["last_ack_utc"] = _lastAckUtc,
        ["last_ack_error"] = _lastAckError,
        ["last_acknowledged_seq"] = Interlocked.Read(ref _lastAcknowledgedSeq),
        ["last_telemetry_utc"] = _lastTelemetryUtc,
        ["last_telemetry_error"] = _lastTelemetryError,
        ["ack_failures"] = Interlocked.Read(ref _ackFailures),
        ["telemetry_failures"] = Interlocked.Read(ref _telemetryFailures),
        ["queue_depth"] = _queue.Count,
        ["retry_depth"] = _pendingRetries.Count,
        ["failed_depth"] = _failed.Count,
        ["applied"] = Interlocked.Read(ref _applied),
        ["superseded"] = Interlocked.Read(ref _superseded),
        ["rejected"] = Interlocked.Read(ref _rejected),
        ["duplicates"] = Interlocked.Read(ref _duplicates),
        ["retried"] = Interlocked.Read(ref _retried),
        ["priority_tagged"] = Interlocked.Read(ref _priorityTagged),
        ["priority_fast_lane_applied"] = Interlocked.Read(ref _priorityFastLaneApplied),
        ["last_priority_tier"] = _lastPriorityTier,
        ["last_priority_rank"] = _lastPriorityRank,
        ["acknowledged"] = Interlocked.Read(ref _acknowledged),
        ["pending"] = pending
      };
    }
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

  public void Dispose() {
    IsRunning = false;
    lock (_gate) _state = "stopped";
  }
}
