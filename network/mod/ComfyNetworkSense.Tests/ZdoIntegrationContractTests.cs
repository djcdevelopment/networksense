namespace ComfyNetworkSense.Tests;

using Xunit;

public sealed class ZdoIntegrationContractTests {
  [Theory]
  [InlineData(0, 4, true)]
  [InlineData(4, 4, true)]
  [InlineData(5, 4, false)]
  [InlineData(6, 4, false)]
  [InlineData(-1, 6, false)]
  public void ImportanceGateIsTheNetworkBoundary(int rank, int maximum, bool expected) {
    Assert.Equal(expected, ZdoIntegrationContract.ImportanceAllows(rank, maximum));
  }

  [Theory]
  [InlineData(0.0, 0.0, false)]      // reach 0 => not a landmark, never admitted this way
  [InlineData(100.0, 0.0, false)]    // unmarked peer, any distance => no exemption
  [InlineData(100.0, 100.0, true)]   // marked with reach R, observer at exactly R => admitted
  [InlineData(50.0, 100.0, true)]    // inside reach => admitted
  [InlineData(150.0, 100.0, false)]  // beyond reach => not admitted
  [InlineData(-1.0, 100.0, false)]   // nonsensical negative distance => not admitted
  public void LandmarkReachGrantsPresenceWithinReach(double distance, double reach, bool expected) {
    Assert.Equal(expected, ZdoIntegrationContract.LandmarkAllows(distance, reach));
  }

  [Fact]
  public void LandmarkWithinReachIsDeliveredWhereAnUnmarkedPeerAtTheSameDistanceIsNot() {
    // The task-7 gate: a rank ABOVE the maximum (so the rank gate alone rejects it) is admitted
    // anyway when it carries reach and the observer is within it; an identical object with no reach
    // (an unmarked peer) at the same distance is dropped. Distance R, reach R.
    const int rankAboveMax = 5, maxRank = 4;
    const double distance = 250.0, reach = 250.0;

    Assert.True(ZdoIntegrationContract.Admits(rankAboveMax, maxRank, distance, reach));
    Assert.False(ZdoIntegrationContract.Admits(rankAboveMax, maxRank, distance, reachMeters: 0.0));
  }

  [Fact]
  public void AdmitsStillHonoursTheRankGateForNonLandmarks() {
    // A rank within the maximum is admitted regardless of reach (the ordinary path is unchanged).
    Assert.True(ZdoIntegrationContract.Admits(4, 4, distanceMeters: 9999.0, reachMeters: 0.0));
    Assert.False(ZdoIntegrationContract.Admits(5, 4, distanceMeters: 9999.0, reachMeters: 0.0));
  }

  [Fact]
  public void ContractIdentityIsExplicit() {
    Assert.Equal(2, ZdoIntegrationContract.SchemaVersion);
    Assert.Equal("zdo_redirect", ZdoIntegrationContract.Operation);
    Assert.Equal("legacy", ZdoIntegrationContract.LegacyRecipient);
  }
}
