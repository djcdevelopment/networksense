namespace ComfyNetworkSense;

/// <summary>
/// Keeps mount physics ownership separate from rider control. A dedicated
/// server may own an idle mount, but it is never a rider and must therefore
/// leave the canonical <c>s_user</c> edge clear.
/// </summary>
internal static class SaddleAuthorityTransferPolicy {
  internal static long CanonicalUser(long newOwnerPeerId, long serverPeerId) =>
      newOwnerPeerId == serverPeerId ? 0L : newOwnerPeerId;

  internal static bool BlocksNativeServerOwnerReassignment(
      bool releaseScopeActive,
      long canonicalOwnerPeerId,
      long serverPeerId,
      long currentOwnerPeerId,
      long attemptedOwnerPeerId) =>
      releaseScopeActive && serverPeerId != 0L &&
      canonicalOwnerPeerId == serverPeerId &&
      currentOwnerPeerId == serverPeerId &&
      attemptedOwnerPeerId != serverPeerId;
}
