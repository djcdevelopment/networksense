namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

using Unity.Profiling;
using UnityEngine;

public sealed class NetworkSensePerfProbe : IDisposable {
  static NetworkSensePerfProbe _active;

  readonly string _sessionId;
  readonly TelemetryLogWriter _writer;
  readonly Dictionary<string, ProfilerMarker> _markers = [];
  readonly object _markerSync = new();
  readonly object _engineLogSync = new();

  long _lastFrameTicks;
  float _lastEngineLogSampleAt;
  long _lastFrameLogCount;
  long _lastFrameWarningCount;
  long _lastSampleLogCount;
  long _lastSampleWarningCount;
  long _engineLogCount;
  long _engineWarningCount;
  long _engineErrorCount;
  string _latestEngineLogType = string.Empty;
  string _latestEngineLogMessage = string.Empty;
  string _latestEngineLogUtc = string.Empty;
  string _routeState = "idle";
  string _routeStopId = string.Empty;
  string _routePhase = string.Empty;
  string _latestRegionId = string.Empty;
  bool _benchmarkRunning;
  bool _disposed;

  public NetworkSensePerfProbe(string sessionId, TelemetryLogWriter writer) {
    _sessionId = sessionId;
    _writer = writer;
    Application.logMessageReceivedThreaded += HandleEngineLog;
  }

  public static NetworkSensePerfProbe Active => _active;

  public static void SetActive(NetworkSensePerfProbe probe) {
    _active = probe;
  }

  public static Section Measure(string sectionName) {
    NetworkSensePerfProbe probe = _active;
    return probe != null && probe.IsEnabled ? probe.StartSection(sectionName) : default;
  }

  public static void SetRouteState(string routeState, string routeStopId = "", string routePhase = "") {
    _active?.UpdateRouteState(routeState, routeStopId, routePhase);
  }

  public bool IsEnabled =>
      !_disposed
          && PluginConfig.PerfProbeEnabled != null
          && PluginConfig.PerfProbeEnabled.Value
          && PluginConfig.WriteTelemetryLogs.Value;

  public void SetRuntimeContext(string regionId, bool benchmarkRunning) {
    _latestRegionId = regionId ?? string.Empty;
    _benchmarkRunning = benchmarkRunning;
  }

  public string GetStatus(string regionId, bool benchmarkRunning) {
    SetRuntimeContext(regionId, benchmarkRunning);
    return
        $"NetworkSense perf: enabled={IsEnabled}, route={_routeState}/{_routeStopId}/{_routePhase}, "
            + $"region={_latestRegionId}, benchmark={(_benchmarkRunning ? "running" : "idle")}, "
            + $"engineLogs={Interlocked.Read(ref _engineLogCount)}, warnings={Interlocked.Read(ref _engineWarningCount)}, "
            + $"errors={Interlocked.Read(ref _engineErrorCount)}, writerQueue={_writer?.QueueDepth ?? 0}, "
            + $"dropped={_writer?.DroppedRows ?? 0}, written={_writer?.WrittenRows ?? 0}, faults={_writer?.FaultCount ?? 0}.";
  }

  public void Mark(string label) {
    if (!IsEnabled) {
      return;
    }

    _writer?.Write("perf-markers.jsonl", new Dictionary<string, object> {
        ["timestamp_utc"] = DateTime.UtcNow.ToString("o"),
        ["session_id"] = _sessionId,
        ["build_version"] = ComfyNetworkSense.PluginVersion,
        ["label"] = label ?? string.Empty,
        ["route_state"] = _routeState,
        ["route_stop_id"] = _routeStopId,
        ["route_phase"] = _routePhase,
        ["region_id"] = _latestRegionId,
        ["benchmark_running"] = _benchmarkRunning,
        ["writer_queue_depth"] = _writer?.QueueDepth ?? 0,
        ["writer_dropped_rows"] = _writer?.DroppedRows ?? 0
    });
  }

  public void UpdateFrame(float unscaledDeltaTime) {
    if (!IsEnabled) {
      _lastFrameTicks = Stopwatch.GetTimestamp();
      return;
    }

    long nowTicks = Stopwatch.GetTimestamp();
    double wallMs = _lastFrameTicks > 0
        ? TicksToMilliseconds(nowTicks - _lastFrameTicks)
        : unscaledDeltaTime * 1000.0;
    _lastFrameTicks = nowTicks;

    double deltaMs = unscaledDeltaTime * 1000.0;
    long logCount = Interlocked.Read(ref _engineLogCount);
    long warningCount = Interlocked.Read(ref _engineWarningCount);
    long logsThisFrame = logCount - _lastFrameLogCount;
    long warningsThisFrame = warningCount - _lastFrameWarningCount;
    _lastFrameLogCount = logCount;
    _lastFrameWarningCount = warningCount;

    float hitchThreshold = Mathf.Max(1.0f, PluginConfig.PerfHitchThresholdMs.Value);
    float severeThreshold = Mathf.Max(hitchThreshold, PluginConfig.PerfSevereHitchThresholdMs.Value);
    double frameMs = Math.Max(deltaMs, wallMs);

    if (frameMs >= hitchThreshold) {
      WriteHitch(frameMs, deltaMs, wallMs, severeThreshold, logsThisFrame, warningsThisFrame);
    }

    if (PluginConfig.PerfEngineLogProbeEnabled.Value
        && Time.unscaledTime - _lastEngineLogSampleAt >= Mathf.Max(0.1f, PluginConfig.PerfSampleIntervalSeconds.Value)) {
      _lastEngineLogSampleAt = Time.unscaledTime;
      WriteEngineLogSample(logCount, warningCount);
    }
  }

  public void Dispose() {
    if (_disposed) {
      return;
    }

    _disposed = true;
    Application.logMessageReceivedThreaded -= HandleEngineLog;

    if (_active == this) {
      _active = null;
    }
  }

  Section StartSection(string sectionName) {
    ProfilerMarker marker = GetMarker(sectionName);
    marker.Begin();
    return new Section(this, sectionName, Stopwatch.GetTimestamp(), marker, true);
  }

  void CompleteSection(string sectionName, long startedTicks, ProfilerMarker marker) {
    marker.End();

    if (!IsEnabled) {
      return;
    }

    double elapsedMs = TicksToMilliseconds(Stopwatch.GetTimestamp() - startedTicks);
    if (elapsedMs < Mathf.Max(0.1f, PluginConfig.PerfSectionWarnThresholdMs.Value)) {
      return;
    }

    _writer?.Write("perf-sections.jsonl", new Dictionary<string, object> {
        ["timestamp_utc"] = DateTime.UtcNow.ToString("o"),
        ["session_id"] = _sessionId,
        ["build_version"] = ComfyNetworkSense.PluginVersion,
        ["section"] = sectionName,
        ["elapsed_ms"] = elapsedMs,
        ["route_state"] = _routeState,
        ["route_stop_id"] = _routeStopId,
        ["route_phase"] = _routePhase,
        ["region_id"] = _latestRegionId,
        ["benchmark_running"] = _benchmarkRunning,
        ["writer_queue_depth"] = _writer?.QueueDepth ?? 0,
        ["writer_dropped_rows"] = _writer?.DroppedRows ?? 0
    });
  }

  ProfilerMarker GetMarker(string sectionName) {
    lock (_markerSync) {
      if (_markers.TryGetValue(sectionName, out ProfilerMarker marker)) {
        return marker;
      }

      marker = new ProfilerMarker("ComfyNetworkSense." + sectionName);
      _markers[sectionName] = marker;
      return marker;
    }
  }

  void UpdateRouteState(string routeState, string routeStopId, string routePhase) {
    _routeState = string.IsNullOrWhiteSpace(routeState) ? "idle" : routeState;
    _routeStopId = routeStopId ?? string.Empty;
    _routePhase = routePhase ?? string.Empty;
  }

  void HandleEngineLog(string condition, string stackTrace, LogType type) {
    Interlocked.Increment(ref _engineLogCount);
    if (type is LogType.Warning or LogType.Error or LogType.Exception or LogType.Assert) {
      Interlocked.Increment(ref _engineWarningCount);
    }
    if (type is LogType.Error or LogType.Exception or LogType.Assert) {
      Interlocked.Increment(ref _engineErrorCount);
    }

    lock (_engineLogSync) {
      _latestEngineLogType = type.ToString();
      _latestEngineLogMessage = TrimMessage(condition);
      _latestEngineLogUtc = DateTime.UtcNow.ToString("o");
    }
  }

  void WriteHitch(
      double frameMs,
      double deltaMs,
      double wallMs,
      float severeThreshold,
      long logsThisFrame,
      long warningsThisFrame) {
    string latestType;
    string latestMessage;
    string latestUtc;
    lock (_engineLogSync) {
      latestType = _latestEngineLogType;
      latestMessage = _latestEngineLogMessage;
      latestUtc = _latestEngineLogUtc;
    }

    _writer?.Write("perf-hitches.jsonl", new Dictionary<string, object> {
        ["timestamp_utc"] = DateTime.UtcNow.ToString("o"),
        ["session_id"] = _sessionId,
        ["build_version"] = ComfyNetworkSense.PluginVersion,
        ["frame_ms"] = frameMs,
        ["frame_unscaled_delta_ms"] = deltaMs,
        ["frame_wall_ms"] = wallMs,
        ["hitch_level"] = frameMs >= severeThreshold ? "severe" : "hitch",
        ["route_state"] = _routeState,
        ["route_stop_id"] = _routeStopId,
        ["route_phase"] = _routePhase,
        ["region_id"] = _latestRegionId,
        ["benchmark_running"] = _benchmarkRunning,
        ["engine_logs_this_frame"] = logsThisFrame,
        ["engine_warnings_this_frame"] = warningsThisFrame,
        ["engine_log_count_total"] = Interlocked.Read(ref _engineLogCount),
        ["engine_warning_count_total"] = Interlocked.Read(ref _engineWarningCount),
        ["engine_error_count_total"] = Interlocked.Read(ref _engineErrorCount),
        ["latest_engine_log_utc"] = latestUtc,
        ["latest_engine_log_type"] = latestType,
        ["latest_engine_log_message"] = latestMessage,
        ["gc_total_memory_bytes"] = GC.GetTotalMemory(false),
        ["gc_gen0_count"] = GC.CollectionCount(0),
        ["gc_gen1_count"] = GC.CollectionCount(1),
        ["gc_gen2_count"] = GC.CollectionCount(2),
        ["writer_queue_depth"] = _writer?.QueueDepth ?? 0,
        ["writer_dropped_rows"] = _writer?.DroppedRows ?? 0,
        ["world_zdos"] = frameMs >= severeThreshold ? GetWorldZdoCount() : 0
    });
  }

  void WriteEngineLogSample(long logCount, long warningCount) {
    long logsSinceSample = logCount - _lastSampleLogCount;
    long warningsSinceSample = warningCount - _lastSampleWarningCount;
    _lastSampleLogCount = logCount;
    _lastSampleWarningCount = warningCount;

    if (logsSinceSample <= 0 && warningsSinceSample <= 0) {
      return;
    }

    string latestType;
    string latestMessage;
    string latestUtc;
    lock (_engineLogSync) {
      latestType = _latestEngineLogType;
      latestMessage = _latestEngineLogMessage;
      latestUtc = _latestEngineLogUtc;
    }

    _writer?.Write("perf-engine-log.jsonl", new Dictionary<string, object> {
        ["timestamp_utc"] = DateTime.UtcNow.ToString("o"),
        ["session_id"] = _sessionId,
        ["build_version"] = ComfyNetworkSense.PluginVersion,
        ["logs_since_last_sample"] = logsSinceSample,
        ["warnings_since_last_sample"] = warningsSinceSample,
        ["log_count_total"] = logCount,
        ["warning_count_total"] = warningCount,
        ["error_count_total"] = Interlocked.Read(ref _engineErrorCount),
        ["latest_engine_log_utc"] = latestUtc,
        ["latest_engine_log_type"] = latestType,
        ["latest_engine_log_message"] = latestMessage,
        ["route_state"] = _routeState,
        ["route_stop_id"] = _routeStopId,
        ["route_phase"] = _routePhase,
        ["region_id"] = _latestRegionId
    });
  }

  static int GetWorldZdoCount() {
    if (PluginConfig.PerfWorldZdoCountOnSevereHitchEnabled == null
        || !PluginConfig.PerfWorldZdoCountOnSevereHitchEnabled.Value) {
      return 0;
    }

    try {
      return ZDOMan.instance != null ? ZDOMan.instance.NrOfObjects() : 0;
    } catch {
      return 0;
    }
  }

  static double TicksToMilliseconds(long ticks) =>
      ticks * 1000.0 / Stopwatch.Frequency;

  static string TrimMessage(string value) {
    if (string.IsNullOrEmpty(value)) {
      return string.Empty;
    }

    value = value.Replace("\r", " ").Replace("\n", " ");
    return value.Length <= 240 ? value : value.Substring(0, 240);
  }

  public readonly struct Section : IDisposable {
    readonly NetworkSensePerfProbe _probe;
    readonly string _sectionName;
    readonly long _startedTicks;
    readonly ProfilerMarker _marker;
    readonly bool _active;

    public Section(
        NetworkSensePerfProbe probe,
        string sectionName,
        long startedTicks,
        ProfilerMarker marker,
        bool active) {
      _probe = probe;
      _sectionName = sectionName;
      _startedTicks = startedTicks;
      _marker = marker;
      _active = active;
    }

    public void Dispose() {
      if (_active) {
        _probe?.CompleteSection(_sectionName, _startedTicks, _marker);
      }
    }
  }
}
