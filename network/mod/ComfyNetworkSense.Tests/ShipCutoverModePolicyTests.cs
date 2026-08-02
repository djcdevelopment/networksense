namespace ComfyNetworkSense.Tests;

using Xunit;

public class ShipCutoverModePolicyTests {
  [Theory]
  [InlineData("water")]
  [InlineData("spawn")]
  [InlineData("wait_ship")]
  [InlineData("board")]
  [InlineData("drive")]
  [InlineData("observe")]
  [InlineData("transfer")]
  [InlineData("wait_owner")]
  [InlineData("wait_released")]
  public void Allows_every_scenario_controller_mode(string mode) =>
      Assert.True(ShipCutoverModePolicy.Allows(mode));

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("saddle")]
  [InlineData("wait-release")]
  public void Rejects_unknown_modes(string mode) =>
      Assert.False(ShipCutoverModePolicy.Allows(mode));

  [Fact]
  public void Authority_handoff_requires_canonical_helm_release() {
    Assert.True(ShipCutoverModePolicy.AllowsAuthorityHandoff(0L));
    Assert.False(ShipCutoverModePolicy.AllowsAuthorityHandoff(1L));
    Assert.False(ShipCutoverModePolicy.AllowsAuthorityHandoff(long.MaxValue));
  }
}
