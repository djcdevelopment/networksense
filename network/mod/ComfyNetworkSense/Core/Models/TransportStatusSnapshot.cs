namespace ComfyNetworkSense;

public sealed class TransportStatusSnapshot {
  public bool ValheimConnected { get; set; }
  public bool LumberjacksArmed { get; set; }
  public bool LumberjacksHttpEnabled { get; set; }
  public bool McpEnabled { get; set; }
  public bool McpReachable { get; set; }
  public string LumberjacksState { get; set; }
  public string DashboardUrl { get; set; }
  public string SetupUrl { get; set; }
}
