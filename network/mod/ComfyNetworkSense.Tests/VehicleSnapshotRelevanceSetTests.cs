namespace ComfyNetworkSense.Tests;

using System;
using System.Linq;
using Xunit;

public sealed class VehicleSnapshotRelevanceSetTests {
  [Fact]
  public void Third_recipient_enters_retains_leaves_and_reenters_independently() {
    var relevance = new VehicleSnapshotRelevanceSet();

    var first = relevance.Reconcile(
        "ship:7:42",
        new[] {
            new VehicleSnapshotRelevanceCandidate(101, 4),
            new VehicleSnapshotRelevanceCandidate(202, 32),
            new VehicleSnapshotRelevanceCandidate(303, 90),
        },
        outerRadiusMeters: 64,
        hysteresisMeters: 8).ToArray();

    Assert.Equal(
        new[] {
            VehicleSnapshotRelevanceTransition.Entered,
            VehicleSnapshotRelevanceTransition.Entered,
            VehicleSnapshotRelevanceTransition.Outside,
        },
        first.Select(decision => decision.Transition));
    Assert.Equal(new long[] { 101, 202 },
        first.Where(decision => decision.Deliver)
            .Select(decision => decision.PeerId));

    var entered = relevance.Reconcile(
        "ship:7:42",
        new[] {
            new VehicleSnapshotRelevanceCandidate(101, 4),
            new VehicleSnapshotRelevanceCandidate(202, 32),
            new VehicleSnapshotRelevanceCandidate(303, 63.9),
        },
        64,
        8).ToArray();
    Assert.Equal(VehicleSnapshotRelevanceTransition.Entered,
        entered.Single(decision => decision.PeerId == 303).Transition);
    Assert.True(entered.Single(decision => decision.PeerId == 303).Deliver);

    var hysteresis = relevance.Reconcile(
        "ship:7:42",
        new[] {
            new VehicleSnapshotRelevanceCandidate(101, 4),
            new VehicleSnapshotRelevanceCandidate(202, 32),
            new VehicleSnapshotRelevanceCandidate(303, 70),
        },
        64,
        8).ToArray();
    Assert.Equal(VehicleSnapshotRelevanceTransition.Retained,
        hysteresis.Single(decision => decision.PeerId == 303).Transition);

    var left = relevance.Reconcile(
        "ship:7:42",
        new[] {
            new VehicleSnapshotRelevanceCandidate(101, 4),
            new VehicleSnapshotRelevanceCandidate(202, 32),
            new VehicleSnapshotRelevanceCandidate(303, 72.1),
        },
        64,
        8).ToArray();
    Assert.Equal(VehicleSnapshotRelevanceTransition.Left,
        left.Single(decision => decision.PeerId == 303).Transition);
    Assert.False(left.Single(decision => decision.PeerId == 303).Deliver);

    var reentered = relevance.Reconcile(
        "ship:7:42",
        new[] {
            new VehicleSnapshotRelevanceCandidate(101, 4),
            new VehicleSnapshotRelevanceCandidate(202, 32),
            new VehicleSnapshotRelevanceCandidate(303, 64),
        },
        64,
        8).ToArray();
    Assert.Equal(VehicleSnapshotRelevanceTransition.Entered,
        reentered.Single(decision => decision.PeerId == 303).Transition);
  }

  [Fact]
  public void Object_edges_and_duplicate_candidates_are_recipient_local() {
    var relevance = new VehicleSnapshotRelevanceSet();
    var ship = relevance.Reconcile(
        "ship:1",
        new[] {
            new VehicleSnapshotRelevanceCandidate(101, 10),
            new VehicleSnapshotRelevanceCandidate(101, 12),
            new VehicleSnapshotRelevanceCandidate(202, 80),
        },
        64,
        8);
    var mount = relevance.Reconcile(
        "mount:1",
        new[] {
            new VehicleSnapshotRelevanceCandidate(101, 80),
            new VehicleSnapshotRelevanceCandidate(202, 10),
        },
        64,
        8);

    Assert.Single(ship, decision => decision.PeerId == 101);
    Assert.True(relevance.Contains("ship:1", 101));
    Assert.False(relevance.Contains("ship:1", 202));
    Assert.False(relevance.Contains("mount:1", 101));
    Assert.True(relevance.Contains("mount:1", 202));
  }

  [Fact]
  public void Disconnect_prunes_the_edge_and_reconnect_enters_again() {
    var relevance = new VehicleSnapshotRelevanceSet();
    relevance.Reconcile(
        "mount:9",
        new[] { new VehicleSnapshotRelevanceCandidate(303, 10) },
        64,
        8);

    Assert.True(relevance.Contains("mount:9", 303));
    Assert.Empty(relevance.Reconcile(
        "mount:9",
        Array.Empty<VehicleSnapshotRelevanceCandidate>(),
        64,
        8));
    Assert.False(relevance.Contains("mount:9", 303));

    VehicleSnapshotRelevanceDecision reconnect = Assert.Single(
        relevance.Reconcile(
            "mount:9",
            new[] { new VehicleSnapshotRelevanceCandidate(303, 10) },
            64,
            8));
    Assert.Equal(
        VehicleSnapshotRelevanceTransition.Entered,
        reconnect.Transition);
  }

  [Fact]
  public void Non_finite_distance_leaves_and_forget_is_object_local() {
    var relevance = new VehicleSnapshotRelevanceSet();
    relevance.Reconcile(
        "ship:a",
        new[] { new VehicleSnapshotRelevanceCandidate(101, 10) },
        64,
        8);
    relevance.Reconcile(
        "ship:b",
        new[] { new VehicleSnapshotRelevanceCandidate(101, 10) },
        64,
        8);

    VehicleSnapshotRelevanceDecision invalid = Assert.Single(
        relevance.Reconcile(
            "ship:a",
            new[] { new VehicleSnapshotRelevanceCandidate(101, double.NaN) },
            64,
            8));
    Assert.Equal(VehicleSnapshotRelevanceTransition.Left, invalid.Transition);
    Assert.False(invalid.Deliver);

    relevance.Forget("ship:b");
    Assert.False(relevance.Contains("ship:a", 101));
    Assert.False(relevance.Contains("ship:b", 101));
  }
}
