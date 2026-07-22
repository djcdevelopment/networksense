namespace ComfyNetworkSense;

public sealed class TransportStatusSnapshot {
  public bool ValheimConnected { get; set; }
  public bool LumberjacksArmed { get; set; }
  public bool LumberjacksHttpEnabled { get; set; }
  public bool LumberjacksWebSocketEnabled { get; set; }
  public bool LumberjacksUdpEnabled { get; set; }
  public bool LumberjacksWebSocketConnected { get; set; }
  public bool LumberjacksUdpReady { get; set; }
  public bool MotionApplyEnabled { get; set; }
  public long MotionSent { get; set; }
  public long MotionReceived { get; set; }
  public long MotionApplied { get; set; }
  public bool McpEnabled { get; set; }
  public bool McpReachable { get; set; }
  public string LumberjacksState { get; set; }
  public string DashboardUrl { get; set; }
  public string SetupUrl { get; set; }
}
