namespace ComfyNetworkSense.Tests;

using Xunit;

public sealed class AlphaTransportSwitchesTests {
  [Fact]
  public void SwitchesAreIndependentAndResetOnDemand() {
    AlphaTransportSwitches.Reset();

    AlphaTransportSwitches.SetLumberjacksHttpEnabled(false);
    Assert.False(AlphaTransportSwitches.LumberjacksHttpEnabled);
    Assert.True(AlphaTransportSwitches.McpEnabled);

    AlphaTransportSwitches.SetMcpEnabled(false);
    Assert.False(AlphaTransportSwitches.McpEnabled);

    AlphaTransportSwitches.Reset();
    Assert.True(AlphaTransportSwitches.LumberjacksHttpEnabled);
    Assert.True(AlphaTransportSwitches.McpEnabled);
  }
}
