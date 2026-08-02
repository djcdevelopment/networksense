namespace ComfyNetworkSense;

internal static class ShipCutoverModePolicy {
  internal static bool Allows(string mode) => mode is
      "water" or "spawn" or "wait_ship" or "board" or
      "drive" or "observe" or "transfer" or "wait_owner" or
      "wait_released";
}
