namespace ComfyNetworkSense;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

/// <summary>
/// Evidence and enforcement boundary for the Valheim networking funnels that must reach zero.
/// Harmony patches call <see cref="Observe"/> before the native method. Counts are always retained
/// in memory; disk rows are sampled onto TelemetryLogWriter's background queue so a hot socket poll
/// cannot turn the ledger itself into a main-thread hitch.
/// </summary>
public sealed class NativeNetworkLedger : IDisposable {
  const string FileName = "native-network-use.jsonl";
  const float SummaryIntervalSeconds = 2.0f;

  static readonly object ContextLock = new();
  static NativeNetworkLedger _active;
  static string _requestedRunId = string.Empty;
  static string _requestedClient = string.Empty;
  static int _poisonOverride = -1;

  readonly string _sessionId =
      DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
      + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
  readonly TelemetryLogWriter _writer = new();
  readonly ConcurrentDictionary<string, RunCounters> _runCounters =
      new(StringComparer.Ordinal);

  string _lastRole = "starting";
  float _nextSummaryAt;
  bool _disposed;

  public NativeNetworkLedger() {
    NativeNetworkLedger previous = Interlocked.Exchange(ref _active, this);
    previous?.Dispose();
    WriteLifecycle("started");
  }

  public static NativeNetworkLedger Active => Volatile.Read(ref _active);

  /// <summary>
  /// Records one invocation and returns true when poison mode requires the caller to skip the
  /// original native method.
  /// </summary>
  public static bool Observe(string funnel, string direction, string messageClass) {
    NativeNetworkLedger active = Active;
    return active != null && active.RecordUse(funnel, direction, messageClass);
  }

  public static void Stage(string stage, string detail = "") {
    Active?.RecordStage(stage, detail);
  }

  public static void SetRunContext(string runId, string client) {
    lock (ContextLock) {
      _requestedRunId = SafeToken(runId, 80) ? runId.Trim() : string.Empty;
      _requestedClient = SafeToken(client, 32) ? client.Trim() : string.Empty;
    }
  }

  public static void SetPoisonOverride(bool enabled) =>
      Interlocked.Exchange(ref _poisonOverride, enabled ? 1 : 0);

  public void Update(float now) {
    if (_disposed || !LedgerEnabled() || now < _nextSummaryAt) {
      return;
    }
    _nextSummaryAt = now + SummaryIntervalSeconds;
    WriteSummary("periodic");
  }

  public Dictionary<string, object> Snapshot() {
    string runId = EffectiveRunId();
    RunCounters counters = CountersFor(runId);
    Dictionary<string, object> result = new() {
        ["ledger_enabled"] = LedgerEnabled(),
        ["poison_enabled"] = PoisonEnabled(),
        ["native_total"] = Interlocked.Read(ref counters.NativeTotal),
        ["poison_trips"] = Interlocked.Read(ref counters.PoisonTrips),
        ["stage_rows"] = Interlocked.Read(ref counters.StageRows),
        ["writer_queue_depth"] = _writer.QueueDepth,
        ["writer_dropped_rows"] = _writer.DroppedRows,
        ["writer_faults"] = _writer.FaultCount,
        ["run_id"] = runId,
        ["client"] = EffectiveClient()
    };
    foreach (KeyValuePair<string, long> pair in counters.FunnelCounts) {
      result["funnel_" + pair.Key] = pair.Value;
    }
    return result;
  }

  bool RecordUse(string funnel, string direction, string messageClass) {
    if (_disposed || !LedgerEnabled()) {
      return false;
    }

    string safeFunnel = SafeName(funnel, "unknown");
    string safeDirection = SafeName(direction, "unknown");
    string safeMessageClass = SafeName(messageClass, safeFunnel);
    string runId = EffectiveRunId();
    RunCounters counters = CountersFor(runId);
    long total = Interlocked.Increment(ref counters.NativeTotal);
    long funnelCount =
        counters.FunnelCounts.AddOrUpdate(safeFunnel, 1L, (_, value) => value + 1L);
    bool blocked = PoisonEnabled();
    if (blocked) {
      Interlocked.Increment(ref counters.PoisonTrips);
    }

    // Hot Recv polling can run every frame. First-four + powers-of-two makes the boundary legible
    // without generating a new disk write per call. Periodic summaries carry the exact counts.
    if (funnelCount <= 4 || IsPowerOfTwo(funnelCount)) {
      Dictionary<string, object> row = BaseRow("native_use", runId);
      row["funnel"] = safeFunnel;
      row["direction"] = safeDirection;
      row["message_class"] = safeMessageClass;
      row["funnel_count"] = funnelCount;
      row["native_total"] = total;
      row["poison_enabled"] = blocked;
      row["blocked"] = blocked;
      row["thread_id"] = Thread.CurrentThread.ManagedThreadId;
      _writer.Write(FileName, row);

      string marker = "[ComfyNetworkSense][native-ledger] "
          + (blocked ? "BLOCKED" : "OBSERVED")
          + " funnel=" + safeFunnel
          + " direction=" + safeDirection
          + " count=" + funnelCount
          + " run=" + runId;
      if (blocked) {
        ZLog.LogWarning(marker);
      } else if (funnelCount == 1) {
        ZLog.Log(marker);
      }
    }
    return blocked;
  }

  void RecordStage(string stage, string detail) {
    if (_disposed || !LedgerEnabled()) {
      return;
    }

    string runId = EffectiveRunId();
    RunCounters counters = CountersFor(runId);
    long now = Stopwatch.GetTimestamp();
    string prior;
    double elapsedMs;
    lock (counters.StageLock) {
      prior = counters.LastStage;
      elapsedMs = counters.LastStageTimestamp <= 0
          ? 0.0
          : (now - counters.LastStageTimestamp) * 1000.0 / Stopwatch.Frequency;
      counters.LastStage = SafeName(stage, "unknown");
      counters.LastStageTimestamp = now;
    }

    Interlocked.Increment(ref counters.StageRows);
    Dictionary<string, object> row = BaseRow("connection_stage", runId);
    row["stage"] = SafeName(stage, "unknown");
    row["prior_stage"] = prior;
    row["elapsed_since_prior_stage_ms"] = elapsedMs;
    row["detail"] = SafeDetail(detail);
    row["native_total"] = Interlocked.Read(ref counters.NativeTotal);
    row["poison_enabled"] = PoisonEnabled();
    row["thread_id"] = Thread.CurrentThread.ManagedThreadId;
    _writer.Write(FileName, row);
  }

  void WriteSummary(string reason) {
    Dictionary<string, object> row = BaseRow("summary");
    row["reason"] = reason;
    foreach (KeyValuePair<string, object> pair in Snapshot()) {
      row[pair.Key] = pair.Value;
    }
    _writer.Write(FileName, row);
  }

  void WriteLifecycle(string state) {
    Dictionary<string, object> row = BaseRow("lifecycle");
    row["state"] = state;
    row["ledger_enabled"] = LedgerEnabled();
    row["poison_enabled"] = PoisonEnabled();
    _writer.Write(FileName, row);
  }

  Dictionary<string, object> BaseRow(string eventName, string runId = null) {
    string role = Role();
    if (role is not ("starting" or "unknown")) {
      _lastRole = role;
    } else if (!string.IsNullOrEmpty(_lastRole)) {
      role = _lastRole;
    }
    return new Dictionary<string, object> {
        ["schema_version"] = 1,
        ["event"] = eventName,
        ["timestamp_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        ["session_id"] = _sessionId,
        ["run_id"] = string.IsNullOrEmpty(runId) ? EffectiveRunId() : runId,
        ["client"] = EffectiveClient(),
        ["role"] = role
    };
  }

  RunCounters CountersFor(string runId) =>
      _runCounters.GetOrAdd(
          string.IsNullOrEmpty(runId) ? _sessionId : runId,
          static _ => new RunCounters());

  string EffectiveRunId() {
    lock (ContextLock) {
      if (!string.IsNullOrEmpty(_requestedRunId)) {
        return _requestedRunId;
      }
    }
    string configured = PluginConfig.NativeNetworkEvidenceRunId?.Value;
    return SafeToken(configured, 80) ? configured.Trim() : _sessionId;
  }

  static string EffectiveClient() {
    lock (ContextLock) {
      return _requestedClient;
    }
  }

  static string Role() {
    try {
      ZNet znet = ZNet.instance;
      if (znet == null) {
        return "starting";
      }
      if (znet.IsServer() && znet.IsDedicated()) {
        return "dedicated_server";
      }
      return znet.IsServer() ? "host" : "client";
    } catch {
      return "unknown";
    }
  }

  static bool LedgerEnabled() =>
      PluginConfig.NativeNetworkLedgerEnabled?.Value != false;

  static bool PoisonEnabled() {
    int requestOverride = Volatile.Read(ref _poisonOverride);
    return requestOverride >= 0
        ? requestOverride != 0
        : PluginConfig.NativeNetworkPoisonEnabled?.Value == true;
  }

  static bool IsPowerOfTwo(long value) =>
      value > 0 && (value & (value - 1)) == 0;

  sealed class RunCounters {
    public readonly ConcurrentDictionary<string, long> FunnelCounts =
        new(StringComparer.Ordinal);
    public readonly object StageLock = new();
    public long NativeTotal;
    public long PoisonTrips;
    public long StageRows;
    public long LastStageTimestamp;
    public string LastStage = string.Empty;
  }

  static bool SafeToken(string value, int maxLength) {
    if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength) {
      return false;
    }
    foreach (char c in value) {
      if (!char.IsLetterOrDigit(c) && c != '-' && c != '_' && c != '.') {
        return false;
      }
    }
    return true;
  }

  static string SafeName(string value, string fallback) {
    if (string.IsNullOrWhiteSpace(value)) {
      return fallback;
    }
    char[] safe = new char[Math.Min(value.Length, 80)];
    int length = 0;
    foreach (char c in value) {
      if (length >= safe.Length) {
        break;
      }
      if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.') {
        safe[length++] = c;
      }
    }
    return length == 0 ? fallback : new string(safe, 0, length);
  }

  static string SafeDetail(string value) {
    if (string.IsNullOrWhiteSpace(value)) {
      return string.Empty;
    }
    string trimmed = value.Trim();
    if (trimmed.Length > 160) {
      trimmed = trimmed.Substring(0, 160);
    }
    return trimmed.Replace('\r', '_').Replace('\n', '_').Replace('\t', '_');
  }

  public void Dispose() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    WriteSummary("disposed");
    Interlocked.CompareExchange(ref _active, null, this);
    _writer.Dispose();
  }
}
