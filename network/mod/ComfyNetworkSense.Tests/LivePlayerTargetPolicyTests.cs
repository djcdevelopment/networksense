using Xunit;

namespace ComfyNetworkSense.Tests;

public sealed class LivePlayerTargetPolicyTests {
  [Theory]
  [InlineData(1059480882L, 1059480882L, true)]
  [InlineData(1L, 1059480882L, false)]
  [InlineData(1059480882L, 0L, false)]
  public void MatchesCurrentOwner_RejectsSavedOrUnownedAliases(
      long zdoUserId,
      long ownerPeerId,
      bool expected) {
    Assert.Equal(
        expected,
        LivePlayerTargetPolicy.MatchesCurrentOwner(zdoUserId, ownerPeerId));
  }
}
