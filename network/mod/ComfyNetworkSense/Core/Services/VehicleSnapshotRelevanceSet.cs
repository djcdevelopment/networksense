#nullable enable

namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;

/// <summary>
/// One observer's visibility transition for a live vehicle or mount snapshot.
/// Authority is deliberately absent: visibility is an N-recipient fact and
/// never changes the single writer for the object.
/// </summary>
public enum VehicleSnapshotRelevanceTransition {
  Outside,
  Entered,
  Retained,
  Left,
}

/// <summary>Pure producer-side input for one connected native observer.</summary>
public readonly struct VehicleSnapshotRelevanceCandidate {
  public VehicleSnapshotRelevanceCandidate(long peerId, double distanceMeters) {
    PeerId = peerId;
    DistanceMeters = distanceMeters;
  }

  public long PeerId { get; }
  public double DistanceMeters { get; }
}

/// <summary>Pure decision returned in the same order as the distinct inputs.</summary>
public readonly struct VehicleSnapshotRelevanceDecision {
  public VehicleSnapshotRelevanceDecision(
      long peerId,
      double distanceMeters,
      VehicleSnapshotRelevanceTransition transition,
      bool deliver) {
    PeerId = peerId;
    DistanceMeters = distanceMeters;
    Transition = transition;
    Deliver = deliver;
  }

  public long PeerId { get; }
  public double DistanceMeters { get; }
  public VehicleSnapshotRelevanceTransition Transition { get; }
  public bool Deliver { get; }
}

/// <summary>
/// Stateful, Unity-free enter/leave gate for snapshot fan-out. New observers
/// enter at <paramref name="outerRadiusMeters"/>. Existing observers retain
/// the edge through an additional hysteresis band, preventing a player or
/// moving vehicle at the zone boundary from flapping every 200 ms.
/// </summary>
public sealed class VehicleSnapshotRelevanceSet {
  readonly HashSet<Edge> _edges = new();

  public IReadOnlyList<VehicleSnapshotRelevanceDecision> Reconcile(
      string objectId,
      IEnumerable<VehicleSnapshotRelevanceCandidate> candidates,
      double outerRadiusMeters,
      double hysteresisMeters) {
    if (string.IsNullOrWhiteSpace(objectId))
      throw new ArgumentException("object id is required", nameof(objectId));
    if (!Finite(outerRadiusMeters) || outerRadiusMeters <= 0)
      throw new ArgumentOutOfRangeException(nameof(outerRadiusMeters));
    if (!Finite(hysteresisMeters) || hysteresisMeters < 0)
      throw new ArgumentOutOfRangeException(nameof(hysteresisMeters));

    var decisions = new List<VehicleSnapshotRelevanceDecision>();
    var connected = new HashSet<long>();
    double enterSquared = outerRadiusMeters * outerRadiusMeters;
    double leaveRadius = outerRadiusMeters + hysteresisMeters;
    double leaveSquared = leaveRadius * leaveRadius;

    foreach (VehicleSnapshotRelevanceCandidate candidate in
             candidates ?? Array.Empty<VehicleSnapshotRelevanceCandidate>()) {
      if (candidate.PeerId == 0 || !connected.Add(candidate.PeerId)) continue;
      var edge = new Edge(objectId, candidate.PeerId);
      bool hadEdge = _edges.Contains(edge);
      double distance = candidate.DistanceMeters;
      bool within = Finite(distance) && distance >= 0 &&
          distance * distance <= (hadEdge ? leaveSquared : enterSquared);
      VehicleSnapshotRelevanceTransition transition;
      if (within) {
        transition = hadEdge
            ? VehicleSnapshotRelevanceTransition.Retained
            : VehicleSnapshotRelevanceTransition.Entered;
        _edges.Add(edge);
      } else if (hadEdge) {
        transition = VehicleSnapshotRelevanceTransition.Left;
        _edges.Remove(edge);
      } else {
        transition = VehicleSnapshotRelevanceTransition.Outside;
      }
      decisions.Add(new VehicleSnapshotRelevanceDecision(
          candidate.PeerId, distance, transition, within));
    }

    // A disconnected peer cannot retain invisible state forever. There is no
    // delivery target for a disconnect transition, so prune it silently; a
    // later reconnect must cross Entered again.
    _edges.RemoveWhere(edge =>
        string.Equals(edge.ObjectId, objectId, StringComparison.Ordinal) &&
        !connected.Contains(edge.PeerId));
    return decisions;
  }

  public bool Contains(string objectId, long peerId) =>
      _edges.Contains(new Edge(objectId, peerId));

  public void Forget(string objectId) =>
      _edges.RemoveWhere(edge =>
          string.Equals(edge.ObjectId, objectId, StringComparison.Ordinal));

  static bool Finite(double value) =>
      !double.IsNaN(value) && !double.IsInfinity(value);

  readonly struct Edge : IEquatable<Edge> {
    public Edge(string objectId, long peerId) {
      ObjectId = objectId;
      PeerId = peerId;
    }

    public string ObjectId { get; }
    public long PeerId { get; }

    public bool Equals(Edge other) =>
        PeerId == other.PeerId &&
        string.Equals(ObjectId, other.ObjectId, StringComparison.Ordinal);

    public override bool Equals(object? value) =>
        value is Edge other && Equals(other);

    public override int GetHashCode() {
      unchecked {
        return ((ObjectId != null
                    ? StringComparer.Ordinal.GetHashCode(ObjectId)
                    : 0) * 397) ^ PeerId.GetHashCode();
      }
    }
  }
}
