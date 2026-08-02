namespace ComfyNetworkSense;

using System;
using System.Globalization;

/// <summary>
/// Identity carried by state that contains Valheim ZDO ids. The world component survives a
/// dedicated-server restart; the session component deliberately does not because ZDOMan assigns
/// ids inside its boot session.
/// </summary>
public static class WorldSessionEpoch {
  const string WorldPrefix = "world-";
  const string SessionPrefix = "session-";

  public static string StableWorld(long worldUid) =>
      WorldPrefix + unchecked((ulong) worldUid)
          .ToString("x16", CultureInfo.InvariantCulture);

  public static string ServerSession(long sessionId) =>
      SessionPrefix + unchecked((ulong) sessionId)
          .ToString("x16", CultureInfo.InvariantCulture);

  public static string Compose(long worldUid, long sessionId) =>
      StableWorld(worldUid) + "-" + ServerSession(sessionId);

  public static bool IsConsistent(
      string worldEpoch,
      string stableWorldEpoch,
      string serverSessionEpoch,
      long worldUid) =>
      string.Equals(stableWorldEpoch, StableWorld(worldUid),
          StringComparison.Ordinal) &&
      IsHexComponent(serverSessionEpoch, SessionPrefix) &&
      string.Equals(
          worldEpoch, stableWorldEpoch + "-" + serverSessionEpoch,
          StringComparison.Ordinal);

  static bool IsHexComponent(string value, string prefix) {
    if (value == null || value.Length != prefix.Length + 16 ||
        !value.StartsWith(prefix, StringComparison.Ordinal)) return false;
    for (int index = prefix.Length; index < value.Length; index++) {
      char c = value[index];
      if (c is not (>= '0' and <= '9') and
          not (>= 'a' and <= 'f')) return false;
    }
    return true;
  }
}
