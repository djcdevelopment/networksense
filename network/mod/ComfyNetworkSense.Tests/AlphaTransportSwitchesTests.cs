namespace ComfyNetworkSense.Tests;

using Xunit;

public sealed class AlphaTransportSwitchesTests {
  [Fact]
  public void SwitchesAreIndependentAndResetOnDemand() {
    AlphaTransportSwitches.Reset();

    AlphaTransportSwitches.SetLumberjacksHttpEnabled(false);
    Assert.False(AlphaTransportSwitches.LumberjacksHttpEnabled);
    Assert.True(AlphaTransportSwitches.McpEnabled);
    Assert.True(AlphaTransportSwitches.LumberjacksWebSocketEnabled);
    Assert.True(AlphaTransportSwitches.LumberjacksUdpEnabled);
    Assert.False(AlphaTransportSwitches.MotionApplyEnabled);

    AlphaTransportSwitches.SetMcpEnabled(false);
    Assert.False(AlphaTransportSwitches.McpEnabled);
    AlphaTransportSwitches.SetLumberjacksUdpEnabled(false);
    AlphaTransportSwitches.SetLumberjacksWebSocketEnabled(false);
    AlphaTransportSwitches.SetMotionApplyEnabled(true);

    AlphaTransportSwitches.Reset();
    Assert.True(AlphaTransportSwitches.LumberjacksHttpEnabled);
    Assert.True(AlphaTransportSwitches.McpEnabled);
    Assert.True(AlphaTransportSwitches.LumberjacksWebSocketEnabled);
    Assert.True(AlphaTransportSwitches.LumberjacksUdpEnabled);
    Assert.False(AlphaTransportSwitches.MotionApplyEnabled);
  }

  [Fact]
  public void ResetCanArmMotionApplyFromDurableConfig() {
    AlphaTransportSwitches.Reset(motionApplyEnabled: true);
    Assert.True(AlphaTransportSwitches.MotionApplyEnabled);
  }
}
