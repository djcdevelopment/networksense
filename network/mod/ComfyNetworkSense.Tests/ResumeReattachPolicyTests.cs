namespace ComfyNetworkSense.Tests;

using Xunit;

public sealed class ResumeReattachPolicyTests {
  [Fact]
  public void ThreeRefusedResumeAttemptsAbandonTheTokenAndResetTheStreak() {
    ResumeReattachPolicy policy = new();

    // The candidate-8/9 storm: every reconnect presents the token and dies before
    // session_started. The third refusal must reincarnate instead of retrying forever.
    Assert.False(policy.OnConnectionEndedWithoutSessionStarted(hadResumeToken: true));
    Assert.False(policy.OnConnectionEndedWithoutSessionStarted(hadResumeToken: true));
    Assert.True(policy.OnConnectionEndedWithoutSessionStarted(hadResumeToken: true));

    // The fresh identity starts with a clean retry budget.
    Assert.Equal(0, policy.RefusedStreak);
    Assert.Equal(ResumeReattachPolicy.BaseRetryDelayMs, policy.NextRetryDelayMs);
  }

  [Fact]
  public void FailuresWithoutAResumeTokenNeverAbandon() {
    ResumeReattachPolicy policy = new();

    // A Gateway that is simply down must keep the existing fixed-cadence retry: there is
    // no token to abandon and reincarnating would discard nothing.
    for (int attempt = 0; attempt < 10; attempt++)
      Assert.False(policy.OnConnectionEndedWithoutSessionStarted(hadResumeToken: false));
    Assert.Equal(ResumeReattachPolicy.BaseRetryDelayMs, policy.NextRetryDelayMs);
  }

  [Fact]
  public void SessionStartedResetsTheRefusedStreak() {
    ResumeReattachPolicy policy = new();

    Assert.False(policy.OnConnectionEndedWithoutSessionStarted(hadResumeToken: true));
    Assert.False(policy.OnConnectionEndedWithoutSessionStarted(hadResumeToken: true));
    policy.OnSessionStarted();

    // The streak is consecutive refusals, so a healthy session_started restarts the count.
    Assert.False(policy.OnConnectionEndedWithoutSessionStarted(hadResumeToken: true));
    Assert.False(policy.OnConnectionEndedWithoutSessionStarted(hadResumeToken: true));
    Assert.True(policy.OnConnectionEndedWithoutSessionStarted(hadResumeToken: true));
  }

  [Fact]
  public void RefusedResumeAttemptsBackOffInsteadOfStormingAtBaseCadence() {
    ResumeReattachPolicy policy = new();

    Assert.Equal(500, policy.NextRetryDelayMs);
    policy.OnConnectionEndedWithoutSessionStarted(hadResumeToken: true);
    Assert.Equal(1000, policy.NextRetryDelayMs);
    policy.OnConnectionEndedWithoutSessionStarted(hadResumeToken: true);
    Assert.Equal(2000, policy.NextRetryDelayMs);
  }
}
