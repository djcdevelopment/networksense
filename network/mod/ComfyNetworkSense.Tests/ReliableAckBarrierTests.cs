namespace ComfyNetworkSense.Tests;

using Xunit;

public sealed class ReliableAckBarrierTests {
  [Fact]
  public void AcknowledgesNormallyWithoutBarrier() {
    ReliableAckBarrier barrier = new();

    Assert.True(barrier.TryAcknowledge(9, out long through, out long released));
    Assert.Equal(9, through);
    Assert.Equal(0, released);
  }

  [Fact]
  public void BanksLaterAcknowledgementsUntilHeldFrameReplays() {
    ReliableAckBarrier barrier = new();
    Assert.True(barrier.TryHold(10));

    Assert.True(barrier.TryAcknowledge(12, out long through, out long released));
    Assert.Equal(0, through);
    Assert.Equal(0, released);

    Assert.True(barrier.TryAcknowledge(11, out through, out released));
    Assert.Equal(0, through);
    Assert.Equal(0, released);

    Assert.True(barrier.TryAcknowledge(9, out through, out released));
    Assert.Equal(9, through);
    Assert.Equal(0, released);

    Assert.True(barrier.TryAcknowledge(10, out through, out released));
    Assert.Equal(12, through);
    Assert.Equal(10, released);
  }

  [Fact]
  public void DuplicateHoldIsIdempotentAndResetDropsIt() {
    ReliableAckBarrier barrier = new();
    Assert.True(barrier.TryHold(21));
    Assert.True(barrier.TryHold(21));
    Assert.False(barrier.TryHold(22));

    barrier.Reset();

    Assert.True(barrier.TryAcknowledge(22, out long through, out long released));
    Assert.Equal(22, through);
    Assert.Equal(0, released);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(-1)]
  public void RejectsInvalidSequences(long sequence) {
    ReliableAckBarrier barrier = new();
    Assert.False(barrier.TryHold(sequence));
    Assert.False(barrier.TryAcknowledge(sequence, out _, out _));
  }
}
