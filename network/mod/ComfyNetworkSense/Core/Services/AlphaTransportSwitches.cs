namespace ComfyNetworkSense;

using System.Threading;

/// <summary>
/// Process-local, restart-reset alpha fault switches. These are intentionally volatile controls,
/// not durable configuration: a crashed or restarted client always comes back with transport on.
/// </summary>
public static class AlphaTransportSwitches {
  static int _lumberjacksHttpEnabled = 1;
  static int _mcpEnabled = 1;

  public static bool LumberjacksHttpEnabled => Volatile.Read(ref _lumberjacksHttpEnabled) != 0;
  public static bool McpEnabled => Volatile.Read(ref _mcpEnabled) != 0;

  public static bool SetLumberjacksHttpEnabled(bool enabled) {
    Interlocked.Exchange(ref _lumberjacksHttpEnabled, enabled ? 1 : 0);
    return enabled;
  }

  public static bool SetMcpEnabled(bool enabled) {
    Interlocked.Exchange(ref _mcpEnabled, enabled ? 1 : 0);
    return enabled;
  }

  public static void Reset() {
    SetLumberjacksHttpEnabled(true);
    SetMcpEnabled(true);
  }
}
