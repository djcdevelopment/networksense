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

  [Fact]
  public void ContractIdentityIsExplicit() {
    Assert.Equal(2, ZdoIntegrationContract.SchemaVersion);
    Assert.Equal("zdo_redirect", ZdoIntegrationContract.Operation);
    Assert.Equal("legacy", ZdoIntegrationContract.LegacyRecipient);
  }
}
