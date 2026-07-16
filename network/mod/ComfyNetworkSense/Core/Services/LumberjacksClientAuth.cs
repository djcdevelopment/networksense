namespace ComfyNetworkSense;

using System.Net.WebSockets;

/// <summary>Adds the Steam-enrollment credential to Lumberjacks client connections.</summary>
public static class LumberjacksClientAuth {
  public static void Apply(ClientWebSocket socket) {
    string key = PluginConfig.LumberjacksClientAccessKey.Value;
    string enrollment = PluginConfig.LumberjacksEnrollmentId.Value;
    if (!string.IsNullOrWhiteSpace(key))
      socket.Options.SetRequestHeader("X-Lumberjacks-Client-Key", key);
    if (!string.IsNullOrWhiteSpace(enrollment))
      socket.Options.SetRequestHeader("X-Lumberjacks-Enrollment-Id", enrollment);
  }
}
