namespace ComfyNetworkSense;

internal static class ShipCutoverModePolicy {
  internal static bool Allows(string mode) => mode is
      "water" or "spawn" or "wait_ship" or "board" or
      "drive" or "observe" or "transfer" or "wait_owner" or
      "wait_released";

  // A ship's control holder and its physics owner are separate authorities.
  // Moving physics ownership while s_user still names the previous helmsman
  // strands the new owner: vanilla correctly refuses every subsequent helm
  // request because its local replica still sees a live controller.  The
  // server transfer receipt therefore has exactly one admissible helm state.
  internal static bool AllowsAuthorityHandoff(long canonicalHelmUser) =>
      canonicalHelmUser == 0L;
}
