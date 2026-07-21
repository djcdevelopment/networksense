namespace ComfyNetworkSense.Tests;

using Xunit;

public sealed class ZdoBandPolicyTests {
  const double Inner = 30.0, Outer = 64.0, Thin = 0.2;

  static ZdoBandAction At(double dist, double reach = 0.0, double now = 100.0, double lastEmit = -1.0) =>
      ZdoBandPolicy.Classify(dist, Inner, Outer, reach, now, lastEmit, Thin);

  [Fact]
  public void NearBandEmitsFull() {
    Assert.Equal(ZdoBandAction.EmitFull, At(0));
    Assert.Equal(ZdoBandAction.EmitFull, At(29.9));
    Assert.Equal(ZdoBandAction.EmitFull, At(30.0)); // inclusive inner edge
  }

  [Fact]
  public void FarBandDrops() {
    Assert.Equal(ZdoBandAction.Drop, At(64.01));
    Assert.Equal(ZdoBandAction.Drop, At(500));
  }

  [Fact]
  public void MidBandThinsByTheClock() {
    // First sighting (lastEmit < 0) always emits.
    Assert.Equal(ZdoBandAction.EmitThinned, At(45, lastEmit: -1.0));
    // Interval elapsed -> due -> emit.
    Assert.Equal(ZdoBandAction.EmitThinned, ZdoBandPolicy.Classify(45, Inner, Outer, 0, nowSeconds: 100.0, lastEmitSeconds: 99.79, thinIntervalSeconds: Thin));
    // Interval not yet elapsed -> hold (suppress, no emit).
    Assert.Equal(ZdoBandAction.HoldThinned, ZdoBandPolicy.Classify(45, Inner, Outer, 0, nowSeconds: 100.0, lastEmitSeconds: 99.9, thinIntervalSeconds: Thin));
    Assert.Equal(ZdoBandAction.HoldThinned, ZdoBandPolicy.Classify(64.0, Inner, Outer, 0, nowSeconds: 100.0, lastEmitSeconds: 100.0, thinIntervalSeconds: Thin)); // outer edge is mid
  }

  [Fact]
  public void LandmarkWithinReachOverridesEveryBand() {
    // A landmark far beyond the drop band, but within its granted reach, still emits.
    Assert.Equal(ZdoBandAction.Landmark, At(500, reach: 1500));
    // In the near band too — reach short-circuits before band math.
    Assert.Equal(ZdoBandAction.Landmark, At(10, reach: 1500));
  }

  [Fact]
  public void LandmarkBeyondItsReachFallsBackToTheBand() {
    // reach set but the observer is past it -> ordinary far drop, not a landmark.
    Assert.Equal(ZdoBandAction.Drop, At(200, reach: 100));
    // reach 0 is "not a landmark" -> band decides.
    Assert.Equal(ZdoBandAction.Drop, At(200, reach: 0));
  }

  [Fact]
  public void EmitsHelperMatchesTheEmitActions() {
    Assert.True(ZdoBandPolicy.Emits(ZdoBandAction.EmitFull));
    Assert.True(ZdoBandPolicy.Emits(ZdoBandAction.EmitThinned));
    Assert.True(ZdoBandPolicy.Emits(ZdoBandAction.Landmark));
    Assert.False(ZdoBandPolicy.Emits(ZdoBandAction.HoldThinned));
    Assert.False(ZdoBandPolicy.Emits(ZdoBandAction.Drop));
  }
}
