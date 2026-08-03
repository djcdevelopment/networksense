using Xunit;

namespace ComfyNetworkSense.Tests;

public sealed class ZdoJournalInterestPolicyTests {
  [Fact]
  public void FirstRegistrationInFreshProcess_ForcesDurableRefresh() {
    Assert.True(
        ZdoJournalInterestPolicy.ShouldRefreshProcessRegistration(
            hasRegisteredInterest: false));
  }

  [Fact]
  public void SteadyStateRegistration_DoesNotForceDurableRefresh() {
    Assert.False(
        ZdoJournalInterestPolicy.ShouldRefreshProcessRegistration(
            hasRegisteredInterest: true));
  }
}
