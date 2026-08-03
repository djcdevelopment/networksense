namespace ComfyNetworkSense;

using System.Collections.Generic;

internal readonly struct ContainerTransactionDecision {
  public ContainerTransactionDecision(
      bool accepted,
      int canonicalRevision,
      int remainingCount,
      int grantedCount,
      string result) {
    Accepted = accepted;
    CanonicalRevision = canonicalRevision;
    RemainingCount = remainingCount;
    GrantedCount = grantedCount;
    Result = result;
  }

  public bool Accepted { get; }
  public int CanonicalRevision { get; }
  public int RemainingCount { get; }
  public int GrantedCount { get; }
  public string Result { get; }
}

internal enum ContainerContentionGateResult {
  Held,
  DuplicateHeld,
  Released,
  InvalidPeer,
  TooManyPeers,
  ExcessCopy,
  AlreadyReleased
}

/// <summary>
/// Physical-canary rendezvous for two distinct clients. Each client sends the
/// same transaction twice so the server can prove idempotent replay. The gate
/// does not release either request until both copies from both peers are held;
/// fixed client-side timing is therefore not part of the contention proof.
/// </summary>
internal sealed class ContainerContentionGate {
  public const int RequiredPeers = 2;
  public const int RequiredCopiesPerPeer = 2;

  readonly Dictionary<long, int> _copiesByPeer = new();

  public bool Released { get; private set; }
  public int DistinctPeers => _copiesByPeer.Count;
  public int TotalCopies { get; private set; }

  public ContainerContentionGateResult Register(long peerId) {
    if (peerId <= 0) return ContainerContentionGateResult.InvalidPeer;
    if (Released) return ContainerContentionGateResult.AlreadyReleased;

    if (!_copiesByPeer.TryGetValue(peerId, out int copies)) {
      if (_copiesByPeer.Count >= RequiredPeers)
        return ContainerContentionGateResult.TooManyPeers;
      _copiesByPeer[peerId] = 1;
      TotalCopies++;
      return ContainerContentionGateResult.Held;
    }
    if (copies >= RequiredCopiesPerPeer)
      return ContainerContentionGateResult.ExcessCopy;

    _copiesByPeer[peerId] = copies + 1;
    TotalCopies++;
    if (_copiesByPeer.Count == RequiredPeers) {
      foreach (int peerCopies in _copiesByPeer.Values)
        if (peerCopies != RequiredCopiesPerPeer)
          return ContainerContentionGateResult.DuplicateHeld;
      Released = true;
      return ContainerContentionGateResult.Released;
    }
    return ContainerContentionGateResult.DuplicateHeld;
  }
}

/// <summary>
/// Unity-free adjudication for the server-owned container transaction lane.
/// Keeping this decision pure makes the no-duplication invariant executable in
/// unit tests as well as in the physical two-client canary.
/// </summary>
internal static class ContainerTransactionPolicy {
  public const int InitialRevision = 1;
  public const int InitialCount = 1;

  public static bool AllowsMode(string mode) => mode is
      "spawn" or "wait_container" or "contend_take" or "observe_empty";

  public static bool BlocksNativeOwnerReassignment(
      bool insideReleaseNearby,
      bool taggedContainer,
      long currentOwner,
      long attemptedOwner) =>
      insideReleaseNearby && taggedContainer &&
      currentOwner != attemptedOwner;

  public static ContainerTransactionDecision AdjudicateTake(
      int expectedRevision,
      int canonicalRevision,
      int canonicalCount,
      int requestedCount) {
    if (expectedRevision < 1 || canonicalRevision < 1 ||
        canonicalCount < 0 || requestedCount != 1)
      return new(false, canonicalRevision, canonicalCount, 0,
          "transaction_shape_invalid");
    if (expectedRevision != canonicalRevision)
      return new(false, canonicalRevision, canonicalCount, 0,
          "stale_revision");
    if (canonicalCount < requestedCount)
      return new(false, canonicalRevision, canonicalCount, 0,
          "insufficient_items");
    return new(true, canonicalRevision + 1,
        canonicalCount - requestedCount, requestedCount, "committed");
  }
}
