namespace ComfyNetworkSense.Tests;

using Xunit;

public sealed class SaddleAuthorityTransferPolicyTests {
  [Fact]
  public void Dedicated_server_owner_is_not_written_as_the_rider() {
    Assert.Equal(0L,
        SaddleAuthorityTransferPolicy.CanonicalUser(
            newOwnerPeerId: 7001L, serverPeerId: 7001L));
  }

  [Fact]
  public void Client_owner_remains_the_canonical_rider() {
    Assert.Equal(8002L,
        SaddleAuthorityTransferPolicy.CanonicalUser(
            newOwnerPeerId: 8002L, serverPeerId: 7001L));
  }
}
