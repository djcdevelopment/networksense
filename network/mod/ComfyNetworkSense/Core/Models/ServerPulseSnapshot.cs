namespace ComfyNetworkSense;

using System.Collections.Generic;

public sealed class ServerPulseSnapshot {
  public string TimestampUtc { get; set; }
  public string SessionId { get; set; }
  public string SampleId { get; set; }
  public string PlayerId { get; set; }
  public string PlayerName { get; set; }
  public string RegionId { get; set; }
  public string ClusterId { get; set; }
  public string Mode { get; set; }
  public string OwnerId { get; set; }
  public string OwnerReason { get; set; }
  public string SampleSource { get; set; }
  public string BuildVersion { get; set; }
  public int RegionObserverCount { get; set; }
  public int RegionPlayerCount { get; set; }
  public int RegionEntityCount { get; set; }
  public int RegionBuildCount { get; set; }
  public float RegionPressureScore { get; set; }
  public float HeartbeatGapMs { get; set; }
  public float MessagesSentPerSec { get; set; }
  public float BytesSentPerSec { get; set; }
  public float OwnerConfidence { get; set; }
  public float AuthorityStabilitySec { get; set; }
  public int OwnerCandidateCount { get; set; }

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
        ["owner_reason"] = OwnerReason,
        ["sample_source"] = SampleSource,
        ["build_version"] = BuildVersion,
        ["region_observer_count"] = RegionObserverCount,
        ["region_player_count"] = RegionPlayerCount,
        ["region_entity_count"] = RegionEntityCount,
        ["region_build_count"] = RegionBuildCount,
        ["region_pressure_score"] = RegionPressureScore,
        ["heartbeat_gap_ms"] = HeartbeatGapMs,
        ["messages_sent_per_sec"] = MessagesSentPerSec,
        ["bytes_sent_per_sec"] = BytesSentPerSec,
        ["owner_confidence"] = OwnerConfidence,
        ["authority_stability_sec"] = AuthorityStabilitySec,
        ["owner_candidate_count"] = OwnerCandidateCount
    };
  }

  public void WriteTo(ZPackage package) {
    package.Write(TimestampUtc ?? string.Empty);
    package.Write(SessionId ?? string.Empty);
    package.Write(SampleId ?? string.Empty);
    package.Write(PlayerId ?? string.Empty);
    package.Write(PlayerName ?? string.Empty);
    package.Write(RegionId ?? string.Empty);
    package.Write(ClusterId ?? string.Empty);
    package.Write(Mode ?? string.Empty);
    package.Write(OwnerId ?? string.Empty);
    package.Write(OwnerReason ?? string.Empty);
    package.Write(SampleSource ?? string.Empty);
    package.Write(BuildVersion ?? string.Empty);
    package.Write(RegionObserverCount);
    package.Write(RegionPlayerCount);
    package.Write(RegionEntityCount);
    package.Write(RegionBuildCount);
    package.Write(RegionPressureScore);
    package.Write(HeartbeatGapMs);
    package.Write(MessagesSentPerSec);
    package.Write(BytesSentPerSec);
    package.Write(OwnerConfidence);
    package.Write(AuthorityStabilitySec);
    package.Write(OwnerCandidateCount);
  }

  public static ServerPulseSnapshot ReadFrom(ZPackage package) {
    return new() {
        TimestampUtc = package.ReadString(),
        SessionId = package.ReadString(),
        SampleId = package.ReadString(),
        PlayerId = package.ReadString(),
        PlayerName = package.ReadString(),
        RegionId = package.ReadString(),
        ClusterId = package.ReadString(),
        Mode = package.ReadString(),
        OwnerId = package.ReadString(),
        OwnerReason = package.ReadString(),
        SampleSource = package.ReadString(),
        BuildVersion = package.ReadString(),
        RegionObserverCount = package.ReadInt(),
        RegionPlayerCount = package.ReadInt(),
        RegionEntityCount = package.ReadInt(),
        RegionBuildCount = package.ReadInt(),
        RegionPressureScore = package.ReadSingle(),
        HeartbeatGapMs = package.ReadSingle(),
        MessagesSentPerSec = package.ReadSingle(),
        BytesSentPerSec = package.ReadSingle(),
        OwnerConfidence = package.ReadSingle(),
        AuthorityStabilitySec = package.ReadSingle(),
        OwnerCandidateCount = package.ReadInt()
    };
  }
}
