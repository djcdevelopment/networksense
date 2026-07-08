namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;

using UnityEngine;

public sealed class BenchmarkRunner {
  readonly List<float> _frameTimesMs = [];

  float _elapsedSeconds;
  bool _isRunning;

  public bool IsRunning => _isRunning;

  public bool Toggle() {
    using NetworkSensePerfProbe.Section section = NetworkSensePerfProbe.Measure("BenchmarkRunner.Toggle");

    if (_isRunning) {
      _isRunning = false;
      return false;
    }

    _elapsedSeconds = 0.0f;
    _frameTimesMs.Clear();
    _isRunning = true;
    return true;
  }

  public BenchmarkResult Update(float deltaTime, ClientTelemetrySample latestSample, string sessionId) {
    using NetworkSensePerfProbe.Section section = NetworkSensePerfProbe.Measure("BenchmarkRunner.Update");

    if (!_isRunning) {
      return null;
    }

    _elapsedSeconds += deltaTime;
    _frameTimesMs.Add(deltaTime * 1000.0f);

    if (_elapsedSeconds < PluginConfig.BenchmarkDurationSeconds.Value) {
      return null;
    }

    _isRunning = false;

    using (NetworkSensePerfProbe.Measure("BenchmarkRunner.Finalize")) {
      _frameTimesMs.Sort();

      float avgFrameTime = 0.0f;

      foreach (float frameTime in _frameTimesMs) {
        avgFrameTime += frameTime;
      }

      avgFrameTime /= Math.Max(1, _frameTimesMs.Count);

      int p95Index = Mathf.Clamp(Mathf.CeilToInt(_frameTimesMs.Count * 0.95f) - 1, 0, _frameTimesMs.Count - 1);
      float p95FrameTime = _frameTimesMs.Count > 0 ? _frameTimesMs[p95Index] : 0.0f;
      float avgFps = avgFrameTime > 0.0f ? 1000.0f / avgFrameTime : 0.0f;
      float cpuBoundEstimate = Mathf.Clamp01((p95FrameTime - 20.0f) / 60.0f);

      string densityContext =
          latestSample == null
              ? "unknown"
              : latestSample.NearbyBuildPieces >= 150
                  ? "dense_base"
                  : latestSample.NearbyBuildPieces >= 50 ? "mixed" : "open";

      string tier = cpuBoundEstimate <= 0.25f ? "HIGH" : cpuBoundEstimate <= 0.60f ? "MED" : "LOW";

      return new() {
          TimestampUtc = DateTime.UtcNow.ToString("o"),
          SessionId = sessionId,
          PlayerId = latestSample?.PlayerId ?? string.Empty,
          RegionId = latestSample?.RegionId ?? string.Empty,
          Mode = latestSample?.Mode ?? NetworkSenseMode.Solo.ToString(),
          SampleSource = "client_benchmark",
          BenchmarkType = "safe_state_frame_probe",
          DurationMs = _elapsedSeconds * 1000.0f,
          AvgFps = avgFps,
          P95FrameTimeMs = p95FrameTime,
          CpuBoundEstimate = cpuBoundEstimate,
          BaseDensityContext = densityContext,
          RecommendedHeadroomTier = tier
      };
    }
  }
}
