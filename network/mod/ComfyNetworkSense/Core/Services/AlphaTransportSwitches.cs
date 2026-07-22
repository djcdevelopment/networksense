namespace ComfyNetworkSense;

using System.Threading;

/// <summary>
/// Process-local, restart-reset alpha fault switches. These are intentionally volatile controls,
/// not durable configuration: a crashed or restarted client always comes back with transport on.
/// </summary>
public static class AlphaTransportSwitches {
  static int _lumberjacksHttpEnabled = 1;
  static int _mcpEnabled = 1;
  static int _lumberjacksWebSocketEnabled = 1;
  static int _lumberjacksUdpEnabled = 1;
  static int _motionApplyEnabled;

  public static bool LumberjacksHttpEnabled => Volatile.Read(ref _lumberjacksHttpEnabled) != 0;
  public static bool McpEnabled => Volatile.Read(ref _mcpEnabled) != 0;
  public static bool LumberjacksWebSocketEnabled => Volatile.Read(ref _lumberjacksWebSocketEnabled) != 0;
  public static bool LumberjacksUdpEnabled => Volatile.Read(ref _lumberjacksUdpEnabled) != 0;
  public static bool MotionApplyEnabled => Volatile.Read(ref _motionApplyEnabled) != 0;

  public static bool SetLumberjacksHttpEnabled(bool enabled) {
    Interlocked.Exchange(ref _lumberjacksHttpEnabled, enabled ? 1 : 0);
    return enabled;
  }

  public static bool SetMcpEnabled(bool enabled) {
    Interlocked.Exchange(ref _mcpEnabled, enabled ? 1 : 0);
    return enabled;
  }

  public static bool SetLumberjacksWebSocketEnabled(bool enabled) {
    Interlocked.Exchange(ref _lumberjacksWebSocketEnabled, enabled ? 1 : 0);
    return enabled;
  }

  public static bool SetLumberjacksUdpEnabled(bool enabled) {
    Interlocked.Exchange(ref _lumberjacksUdpEnabled, enabled ? 1 : 0);
    return enabled;
  }

  public static bool SetMotionApplyEnabled(bool enabled) {
    Interlocked.Exchange(ref _motionApplyEnabled, enabled ? 1 : 0);
    return enabled;
  }

  public static void Reset(bool motionApplyEnabled = false) {
    SetLumberjacksHttpEnabled(true);
    SetMcpEnabled(true);
    SetLumberjacksWebSocketEnabled(true);
    SetLumberjacksUdpEnabled(true);
    SetMotionApplyEnabled(motionApplyEnabled);
  }
}
