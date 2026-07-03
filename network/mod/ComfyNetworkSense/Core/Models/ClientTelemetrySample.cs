namespace ComfyNetworkSense;

using System.Collections.Generic;

public sealed class ClientTelemetrySample {
  public string TimestampUtc { get; set; }
  public string SessionId { get; set; }
  public string SampleId { get; set; }
  public string PlayerId { get; set; }
  public string PlayerName { get; set; }
  public string RegionId { get; set; }
  public string ClusterId { get; set; }
  public string Mode { get; set; }
  public string OwnerId { get; set; }
  public string SampleSource { get; set; }
  public string BuildVersion { get; set; }
  public float RttMs { get; set; }
  public float JitterMs { get; set; }
  public float Fps { get; set; }
  public float FrameTimeMs { get; set; }
  public float FrameTimeP95Ms { get; set; }
  public float CpuBoundEstimate { get; set; }
  public float BytesInPerSec { get; set; }
  public float BytesOutPerSec { get; set; }
  public float PacketsInPerSec { get; set; }
  public float PacketsOutPerSec { get; set; }
  public float TimeSinceLastAuthoritativeUpdateMs { get; set; }
  public float TimeSinceLastNearbyEntityUpdateMs { get; set; }
  public int CorrectionCountRecent { get; set; }
  public float CorrectionMagnitudeAvg { get; set; }
  public int NearbyPlayers { get; set; }
  public int NearbyEntities { get; set; }
  public int NearbyBuildPieces { get; set; }
  public bool DangerNearby { get; set; }

  public Dictionary<string, object> ToDictionary() {
    return new() {
        ["timestamp_utc"] = TimestampUtc,
        ["session_id"] = SessionId,
        ["sample_id"] = SampleId,
        ["player_id"] = PlayerId,
        ["player_name"] = PlayerName,
        ["region_id"] = RegionId,
        ["cluster_id"] = ClusterId,
        ["mode"] = Mode,
        ["owner_id"] = OwnerId,
        ["sample_source"] = SampleSource,
        ["build_version"] = BuildVersion,
        ["rtt_ms"] = RttMs,
        ["jitter_ms"] = JitterMs,
        ["fps"] = Fps,
        ["frame_time_ms"] = FrameTimeMs,
        ["frame_time_p95_ms"] = FrameTimeP95Ms,
        ["cpu_bound_estimate"] = CpuBoundEstimate,
        ["bytes_in_per_sec"] = BytesInPerSec,
        ["bytes_out_per_sec"] = BytesOutPerSec,
        ["packets_in_per_sec"] = PacketsInPerSec,
        ["packets_out_per_sec"] = PacketsOutPerSec,
        ["time_since_last_authoritative_update_ms"] = TimeSinceLastAuthoritativeUpdateMs,
        ["time_since_last_nearby_entity_update_ms"] = TimeSinceLastNearbyEntityUpdateMs,
        ["correction_count_recent"] = CorrectionCountRecent,
        ["correction_magnitude_avg"] = CorrectionMagnitudeAvg,
        ["nearby_players"] = NearbyPlayers,
        ["nearby_entities"] = NearbyEntities,
        ["nearby_build_pieces"] = NearbyBuildPieces,
        ["danger_nearby"] = DangerNearby
    };
  }
}
