namespace ComfyNetworkSense;

public sealed class NetworkSenseSnapshot {
  public string SessionId { get; set; }
  public string RoleLabel { get; set; }
  public bool HudVisible { get; set; }
  public HudDetailLevel HudDetailLevel { get; set; }
  public NetworkSenseMode Mode { get; set; }
  public bool BenchmarkRunning { get; set; }
  public string ServerPulseState { get; set; }
  public ClientTelemetrySample ClientSample { get; set; }
  public ServerPulseSnapshot ServerPulse { get; set; }
  public ScoreSnapshot Scores { get; set; }
  public string LatestExportPath { get; set; }
  public string ExportDirectory { get; set; }
  public NetworkSenseEventRow[] RecentEvents { get; set; }
  public RavenState Raven { get; set; }
}
