namespace ComfyNetworkSense.Tests;

using System.Collections.Generic;
using System.Linq;
using Xunit;

/// <summary>
/// Unit coverage for the ADR-0013 co-presence fan-out decision (the Unity-free seam the runner
/// dispatches on). Proves the acceptance criteria that do not need a live client: one candidate can
/// produce N recipient-specific redirects, the same recipient is never emitted twice for one logical
/// revision, and one recipient's bookkeeping never suppresses delivery to another.
/// </summary>
public sealed class ZdoFanoutPolicyTests {
  const double Inner = 30.0, Outer = 64.0, NoReach = 0.0;
  const long Revision = 5;

  static ZdoFanoutDisposition Disposition(double dist, long? delivered, double reach = NoReach) =>
      ZdoFanoutPolicy.Evaluate(dist, Inner, Outer, reach, Revision, delivered);

  // --- ZdoFanoutPolicy: the per-observer decision ---------------------------------------------

  [Fact]
  public void InBandUndelivered_Emits() {
    Assert.Equal(ZdoFanoutDisposition.Emit, Disposition(0, delivered: null));      // near, never delivered
    Assert.Equal(ZdoFanoutDisposition.Emit, Disposition(45, delivered: null));     // mid
    Assert.Equal(ZdoFanoutDisposition.Emit, Disposition(64, delivered: null));     // inclusive outer edge
    Assert.Equal(ZdoFanoutDisposition.Emit, Disposition(20, delivered: 4));        // stale revision -> re-emit
  }

  [Fact]
  public void FarObserver_IsOutOfBand_AndNeverEmits() {
    Assert.Equal(ZdoFanoutDisposition.OutOfBand, Disposition(64.01, delivered: null));
    Assert.Equal(ZdoFanoutDisposition.OutOfBand, Disposition(500, delivered: null));
  }

  [Fact]
  public void Landmark_OverridesFarBand() {
    // Beyond outer, but within a granted landmark reach -> still relevant, emits.
    Assert.Equal(ZdoFanoutDisposition.Emit, Disposition(200, delivered: null, reach: 250));
  }

  [Fact]
  public void AlreadyAtOrAheadOfRevision_IsSkipped() {
    Assert.Equal(ZdoFanoutDisposition.AlreadyDelivered, Disposition(10, delivered: Revision));
    Assert.Equal(ZdoFanoutDisposition.AlreadyDelivered, Disposition(10, delivered: Revision + 1));
  }

  // --- ZdoFanoutPlan: one candidate -> N recipient-specific redirects --------------------------

  static FanoutObserverInput Obs(string recipient, double dist, long? delivered = null) =>
      new(recipient, dist, delivered);

  static IReadOnlyList<FanoutObserverDecision> Plan(params FanoutObserverInput[] observers) =>
      ZdoFanoutPlan.Evaluate(Revision, Inner, Outer, NoReach, observers);

  [Theory]
  [InlineData(2)]
  [InlineData(10)]
  public void OneCandidate_ProducesOneRedirectPerInBandObserver(int observerCount) {
    var observers = Enumerable.Range(0, observerCount)
        .Select(i => Obs("rcpt-" + i, dist: 10 + i)) // all in-band, all undelivered
        .ToArray();

    var plan = Plan(observers);

    var emitted = plan.Where(decision => decision.Emit).Select(decision => decision.Recipient).ToArray();
    Assert.Equal(observerCount, emitted.Length);
    Assert.Equal(observers.Select(observer => observer.Recipient), emitted); // distinct, order preserved
  }

  [Fact]
  public void SameRecipient_IsEmittedOnlyOncePerRevision() {
    // The exposing peer can appear alongside itself in the connected set; a revision must not fan two
    // copies to one recipient.
    var plan = Plan(Obs("rcpt-a", 10), Obs("rcpt-a", 12), Obs("rcpt-b", 15));

    Assert.Equal(["rcpt-a", "rcpt-b"], plan.Where(d => d.Emit).Select(d => d.Recipient).ToArray());
    // The duplicate rcpt-a is evaluated (visible) but not dispatched.
    Assert.Equal(2, plan.Count(d => d.Recipient == "rcpt-a"));
    Assert.Equal(1, plan.Count(d => d.Recipient == "rcpt-a" && d.Emit));
  }

  [Fact]
  public void OneRecipientBookkeeping_DoesNotSuppressAnother() {
    // rcpt-a already has this revision; rcpt-b does not. rcpt-a is skipped, rcpt-b still emits — each
    // observer is decided from its OWN delivered revision only.
    var plan = Plan(Obs("rcpt-a", 10, delivered: Revision), Obs("rcpt-b", 12, delivered: null));

    Assert.Equal(ZdoFanoutDisposition.AlreadyDelivered, plan[0].Disposition);
    Assert.False(plan[0].Emit);
    Assert.Equal(ZdoFanoutDisposition.Emit, plan[1].Disposition);
    Assert.True(plan[1].Emit);
  }

  [Fact]
  public void FarObserversAreRecordedButNotEmitted() {
    var plan = Plan(Obs("near", 10), Obs("far", 500));

    Assert.True(plan.Single(d => d.Recipient == "near").Emit);
    var far = plan.Single(d => d.Recipient == "far");
    Assert.Equal(ZdoFanoutDisposition.OutOfBand, far.Disposition);
    Assert.False(far.Emit);
  }
}
