namespace ComfyNetworkSense;

public sealed class ScoreSnapshot {
  public float NetworkQuality { get; set; }
  public float FrameStability { get; set; }
  public float CpuHeadroom { get; set; }
  public float Proximity { get; set; }
  public float CurrentLoadPenalty { get; set; }
  public float RegionPressure { get; set; }
  public float OwnerScore { get; set; }
  public float CombatReadiness { get; set; }
  public float MergeRisk { get; set; }
  public bool LowImpactRecommended { get; set; }
  public string ConfidenceLabel { get; set; }
  public string ConnectionLabel { get; set; }
  public string OwnerFitLabel { get; set; }
  public string PressureLabel { get; set; }
}
