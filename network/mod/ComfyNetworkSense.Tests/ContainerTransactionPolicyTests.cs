using Xunit;

namespace ComfyNetworkSense.Tests;

public sealed class ContainerTransactionPolicyTests {
  [Theory]
  [InlineData("spawn")]
  [InlineData("wait_container")]
  [InlineData("contend_take")]
  [InlineData("observe_empty")]
  public void PhysicalCanaryModes_AreExplicitlyBounded(string mode) {
    Assert.True(ContainerTransactionPolicy.AllowsMode(mode));
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("take")]
  [InlineData("dashboard")]
  public void UnknownModes_FailClosed(string mode) {
    Assert.False(ContainerTransactionPolicy.AllowsMode(mode));
  }

  [Fact]
  public void SameRevisionContention_HasExactlyOneWinner() {
    ContainerTransactionDecision first =
        ContainerTransactionPolicy.AdjudicateTake(1, 1, 1, 1);
    ContainerTransactionDecision second =
        ContainerTransactionPolicy.AdjudicateTake(
            1, first.CanonicalRevision, first.RemainingCount, 1);

    Assert.True(first.Accepted);
    Assert.Equal(1, first.GrantedCount);
    Assert.Equal(2, first.CanonicalRevision);
    Assert.Equal(0, first.RemainingCount);
    Assert.False(second.Accepted);
    Assert.Equal("stale_revision", second.Result);
    Assert.Equal(0, second.GrantedCount);
    Assert.Equal(1, first.GrantedCount + second.GrantedCount);
  }

  [Fact]
  public void ContentionGate_HoldsUntilBothCopiesFromBothPeers() {
    ContainerContentionGate gate = new();

    Assert.Equal(
        ContainerContentionGateResult.Held, gate.Register(101));
    Assert.Equal(
        ContainerContentionGateResult.DuplicateHeld, gate.Register(101));
    Assert.False(gate.Released);
    Assert.Equal(
        ContainerContentionGateResult.Held, gate.Register(202));
    Assert.False(gate.Released);
    Assert.Equal(
        ContainerContentionGateResult.Released, gate.Register(202));
    Assert.True(gate.Released);
    Assert.Equal(2, gate.DistinctPeers);
    Assert.Equal(4, gate.TotalCopies);
  }

  [Fact]
  public void ContentionGate_OnePeerCanNeverReleaseItself() {
    ContainerContentionGate gate = new();

    Assert.Equal(
        ContainerContentionGateResult.Held, gate.Register(101));
    Assert.Equal(
        ContainerContentionGateResult.DuplicateHeld, gate.Register(101));
    Assert.Equal(
        ContainerContentionGateResult.ExcessCopy, gate.Register(101));
    Assert.False(gate.Released);
    Assert.Equal(1, gate.DistinctPeers);
    Assert.Equal(2, gate.TotalCopies);
  }

  [Fact]
  public void ContentionGate_RejectsInvalidAndThirdPeers() {
    ContainerContentionGate gate = new();

    Assert.Equal(
        ContainerContentionGateResult.InvalidPeer, gate.Register(0));
    Assert.Equal(
        ContainerContentionGateResult.Held, gate.Register(101));
    Assert.Equal(
        ContainerContentionGateResult.Held, gate.Register(202));
    Assert.Equal(
        ContainerContentionGateResult.TooManyPeers, gate.Register(303));
    Assert.False(gate.Released);
  }

  [Theory]
  [InlineData(true, true, 0, 101, true)]
  [InlineData(true, true, 202, 101, true)]
  [InlineData(false, true, 0, 101, false)]
  [InlineData(true, false, 0, 101, false)]
  [InlineData(true, true, 101, 101, false)]
  public void TaggedContainer_BlocksOnlyNativeReleaseNearbyReassignment(
      bool insideReleaseNearby,
      bool taggedContainer,
      long currentOwner,
      long attemptedOwner,
      bool expected) {
    Assert.Equal(
        expected,
        ContainerTransactionPolicy.BlocksNativeOwnerReassignment(
            insideReleaseNearby,
            taggedContainer,
            currentOwner,
            attemptedOwner));
  }

  [Theory]
  [InlineData(0, 1, 1, 1)]
  [InlineData(1, 0, 1, 1)]
  [InlineData(1, 1, -1, 1)]
  [InlineData(1, 1, 1, 0)]
  [InlineData(1, 1, 1, 2)]
  public void MalformedTransactions_FailClosed(
      int expected, int revision, int remaining, int requested) {
    ContainerTransactionDecision decision =
        ContainerTransactionPolicy.AdjudicateTake(
            expected, revision, remaining, requested);

    Assert.False(decision.Accepted);
    Assert.Equal("transaction_shape_invalid", decision.Result);
    Assert.Equal(0, decision.GrantedCount);
  }
}
