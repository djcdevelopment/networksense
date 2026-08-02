namespace ComfyNetworkSense;

/// <summary>
/// Distinguishes a currently spawned player ZDO from a saved/aliased player-shaped
/// object that merely carries the same current owner. A live player's ZDO user
/// component is minted by that owning peer; an alias such as 1:2860948 owned by a
/// current peer must never be selected for an owner-semantic gameplay probe.
/// </summary>
public static class LivePlayerTargetPolicy {
  public static bool MatchesCurrentOwner(long zdoUserId, long ownerPeerId) =>
      ownerPeerId != 0 && zdoUserId == ownerPeerId;
}
