namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;

using UnityEngine;

public sealed class TelemetryCoordinator : IDisposable {
  readonly string _sessionId =
      DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
  readonly TelemetryLogWriter _logWriter = new();
  readonly ClientTelemetrySampler _clientTelemetrySampler = new();
  readonly ServerPulseBroadcaster _serverPulseBroadcaster = new();
  readonly BenchmarkRunner _benchmarkRunner = new();
  readonly HudRenderer _hudRenderer = new();

  bool _hudVisible = PluginConfig.ShowHudOnStart.Value;
  HudDetailLevel _hudDetailLevel = HudDetailLevel.Summary;
  NetworkSenseMode _mode = NetworkSenseMode.Auto;

  float _lastClientSampleAt;
  string _lastRegionId = string.Empty;

  ClientTelemetrySample _latestClientSample;
  ServerPulseSnapshot _latestServerPulse;
  ScoreSnapshot _latestScores;

  public void Update(float deltaTime) {
    HandleShortcuts();

    _clientTelemetrySampler.RecordFrame(deltaTime);

    if (_benchmarkRunner.Update(deltaTime, _latestClientSample, _sessionId) is BenchmarkResult benchmarkResult) {
      _logWriter.Write("benchmark-results.jsonl", benchmarkResult.ToDictionary());
      WriteEvent("benchmark_end", $"Benchmark complete. Tier: {benchmarkResult.RecommendedHeadroomTier}");
      MessageHud.instance?.ShowMessage(
          MessageHud.MessageType.TopLeft,
          $"Network benchmark: {benchmarkResult.RecommendedHeadroomTier} headroom");
    }

    if (Time.unscaledTime - _lastClientSampleAt >= PluginConfig.LiveSampleIntervalSeconds.Value) {
      _lastClientSampleAt = Time.unscaledTime;
      _latestClientSample = _clientTelemetrySampler.Capture(_sessionId, _mode, _latestServerPulse?.OwnerId);
      _latestScores = ScoreCalculator.Calculate(_latestClientSample, _latestServerPulse, _mode);
      _logWriter.Write("telemetry-client.jsonl", _latestClientSample.ToDictionary());

      if (_latestClientSample.RegionId != _lastRegionId) {
        WriteEvent("region_change", $"Region {(_lastRegionId == string.Empty ? "start" : _lastRegionId)} -> {_latestClientSample.RegionId}");
        _lastRegionId = _latestClientSample.RegionId;
      }
    }

    _serverPulseBroadcaster.Update(_sessionId, _mode, _logWriter);
  }

  public void DrawHud() {
    _hudRenderer.Draw(
        _hudVisible,
        _hudDetailLevel,
        _mode,
        _benchmarkRunner.IsRunning,
        _latestClientSample,
        _latestServerPulse,
        _latestScores);
  }

  public void HandleServerPulse(long senderId, ZPackage package) {
    package.SetPos(0);
    _latestServerPulse = ServerPulseSnapshot.ReadFrom(package);
    _latestScores = ScoreCalculator.Calculate(_latestClientSample, _latestServerPulse, _mode);
  }

  void HandleShortcuts() {
    if (PluginConfig.ToggleHudShortcut.Value.IsDown()) {
      _hudVisible = !_hudVisible;
      WriteEvent("hud_toggle", _hudVisible ? "HUD enabled" : "HUD disabled");
    }

    if (PluginConfig.CycleHudDetailShortcut.Value.IsDown()) {
      _hudDetailLevel = (HudDetailLevel) (((int) _hudDetailLevel + 1) % 3);
      WriteEvent("hud_detail_change", $"HUD detail: {_hudDetailLevel}");
    }

    if (PluginConfig.CycleModeShortcut.Value.IsDown()) {
      _mode = (NetworkSenseMode) (((int) _mode + 1) % 4);
      WriteEvent("mode_change", $"Mode changed to {_mode}");
      MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, $"Network mode: {_mode}");
    }

    if (PluginConfig.ToggleBenchmarkShortcut.Value.IsDown()) {
      bool started = _benchmarkRunner.Toggle();
      WriteEvent(started ? "benchmark_start" : "benchmark_cancel", started ? "Benchmark started" : "Benchmark cancelled");
      MessageHud.instance?.ShowMessage(
          MessageHud.MessageType.TopLeft,
          started ? "Network benchmark started." : "Network benchmark cancelled.");
    }
  }

  void WriteEvent(string eventName, string message) {
    Dictionary<string, object> payload = new() {
        ["timestamp_utc"] = DateTime.UtcNow.ToString("o"),
        ["session_id"] = _sessionId,
        ["event_name"] = eventName,
        ["message"] = message,
        ["mode"] = _mode.ToString(),
        ["region_id"] = _latestClientSample?.RegionId ?? string.Empty,
        ["player_id"] = _latestClientSample?.PlayerId ?? string.Empty,
        ["sample_source"] = "event_marker",
        ["build_version"] = ComfyNetworkSense.PluginVersion
    };

    _logWriter.Write("event-timeline.jsonl", payload);
  }

  public void Dispose() {
    _logWriter?.Dispose();
  }
}
