namespace ComfyNetworkSense.Tests;

using Xunit;

public sealed class CreatureAiProofPolicyTests {
  [Theory]
  [InlineData("drive")]
  [InlineData("observe")]
  public void AllowsOnlyPhysicalCreatureModes(string mode) =>
      Assert.True(CreatureAiProofPolicy.AllowsMode(mode));

  [Theory]
  [InlineData("")]
  [InlineData("saddle_drive")]
  [InlineData("spawn")]
  public void RejectsNonCreatureModes(string mode) =>
      Assert.False(CreatureAiProofPolicy.AllowsMode(mode));

  [Fact]
  public void OwnerRequiresTicksMotionSnapshotsAndNoReplicaExecution() {
    Assert.True(CreatureAiProofPolicy.DrivePasses(40, 0, false, false, 1.0f, 20));
    Assert.False(CreatureAiProofPolicy.DrivePasses(39, 0, false, false, 1.0f, 20));
    Assert.False(CreatureAiProofPolicy.DrivePasses(40, 1, false, false, 1.0f, 20));
    Assert.False(CreatureAiProofPolicy.DrivePasses(40, 0, true, false, 1.0f, 20));
    Assert.False(CreatureAiProofPolicy.DrivePasses(40, 0, false, true, 1.0f, 20));
    Assert.False(CreatureAiProofPolicy.DrivePasses(40, 0, false, false, 0.99f, 20));
    Assert.False(CreatureAiProofPolicy.DrivePasses(40, 0, false, false, 1.0f, 19));
  }

  [Fact]
  public void ObserverRequiresOwnerGateAndCanonicalPresentation() {
    Assert.True(CreatureAiProofPolicy.ObservePasses(0, 40, false, false, 1.0f, 20));
    Assert.False(CreatureAiProofPolicy.ObservePasses(1, 40, false, false, 1.0f, 20));
    Assert.False(CreatureAiProofPolicy.ObservePasses(0, 39, false, false, 1.0f, 20));
    Assert.False(CreatureAiProofPolicy.ObservePasses(0, 40, true, false, 1.0f, 20));
    Assert.False(CreatureAiProofPolicy.ObservePasses(0, 40, false, true, 1.0f, 20));
    Assert.False(CreatureAiProofPolicy.ObservePasses(0, 40, false, false, float.NaN, 20));
  }

  [Theory]
  [InlineData(0.0, true)]
  [InlineData(2.0, true)]
  [InlineData(2.001, false)]
  [InlineData(-0.001, false)]
  public void RecoveryIsBounded(double seconds, bool expected) =>
      Assert.Equal(expected, CreatureAiProofPolicy.RecoveryPasses(seconds));
}
