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

  [Theory]
  [InlineData(0L)]
  [InlineData(8002L)]
  public void Native_release_sweep_cannot_revoke_canonical_server_owner(
      long attemptedOwner) {
    Assert.True(
        SaddleAuthorityTransferPolicy.BlocksNativeServerOwnerReassignment(
            releaseScopeActive: true,
            canonicalOwnerPeerId: 7001L,
            serverPeerId: 7001L,
            currentOwnerPeerId: 7001L,
            attemptedOwnerPeerId: attemptedOwner));
  }

  [Fact]
  public void Explicit_transfer_outside_native_release_scope_remains_allowed() {
    Assert.False(
        SaddleAuthorityTransferPolicy.BlocksNativeServerOwnerReassignment(
            releaseScopeActive: false,
            canonicalOwnerPeerId: 7001L,
            serverPeerId: 7001L,
            currentOwnerPeerId: 7001L,
            attemptedOwnerPeerId: 8002L));
  }

  [Fact]
  public void Native_release_sweep_does_not_pin_a_client_authority() {
    Assert.False(
        SaddleAuthorityTransferPolicy.BlocksNativeServerOwnerReassignment(
            releaseScopeActive: true,
            canonicalOwnerPeerId: 8002L,
            serverPeerId: 7001L,
            currentOwnerPeerId: 8002L,
            attemptedOwnerPeerId: 7001L));
  }
}
