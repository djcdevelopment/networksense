namespace ComfyNetworkSense;

using System.Collections.Generic;

public sealed class BenchmarkResult {
  public string TimestampUtc { get; set; }
  public string SessionId { get; set; }
  public string PlayerId { get; set; }
  public string RegionId { get; set; }
  public string Mode { get; set; }
  public string SampleSource { get; set; }
  public string BenchmarkType { get; set; }
  public float DurationMs { get; set; }
  public float AvgFps { get; set; }
  public float P95FrameTimeMs { get; set; }
  public float CpuBoundEstimate { get; set; }
  public string BaseDensityContext { get; set; }
  public string RecommendedHeadroomTier { get; set; }

  public Dictionary<string, object> ToDictionary() {
    return new() {
        ["timestamp_utc"] = TimestampUtc,
        ["session_id"] = SessionId,
        ["player_id"] = PlayerId,
        ["region_id"] = RegionId,
        ["mode"] = Mode,
        ["sample_source"] = SampleSource,
        ["benchmark_type"] = BenchmarkType,
        ["duration_ms"] = DurationMs,
        ["avg_fps"] = AvgFps,
        ["p95_frame_time_ms"] = P95FrameTimeMs,
        ["cpu_bound_estimate"] = CpuBoundEstimate,
        ["base_density_context"] = BaseDensityContext,
        ["recommended_headroom_tier"] = RecommendedHeadroomTier
    };
  }
}
