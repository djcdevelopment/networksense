namespace ComfyNetworkSense;

using System.Collections.Generic;

/// <summary>What the co-presence fan-out should do for one (observer, ZDO) pair.</summary>
public enum ZdoFanoutDisposition {
  /// <summary>In-band and this observer's bookkeeping does not yet have this data revision — emit a
  /// read copy (a redirect stamped for this recipient).</summary>
  Emit,
  /// <summary>In-band but this observer's native bookkeeping already considered this data revision
  /// delivered — skip (the per-observer dedup that keeps one revision from being sent twice).</summary>
  AlreadyDelivered,
  /// <summary>Beyond the outer band and not a landmark — not visible to this observer this pass. Left
  /// for the observer's own approach re-sync; never emitted.</summary>
  OutOfBand,
}

/// <summary>One connected observer's inputs to the fan-out decision. Pure data, no Unity types.</summary>
public readonly struct FanoutObserverInput {
  public readonly string Recipient;              // the observer's stamped identity (SteamID host)
  public readonly double DistanceMeters;         // observer position -> ZDO position
  public readonly long? DeliveredDataRevision;   // this observer's m_zdos data revision, null if none

  public FanoutObserverInput(string recipient, double distanceMeters, long? deliveredDataRevision) {
    Recipient = recipient;
    DistanceMeters = distanceMeters;
    DeliveredDataRevision = deliveredDataRevision;
  }
}

/// <summary>The evaluated decision for one observer, carrying the inputs back for shadow evidence and
/// the final <see cref="Emit"/> flag the runner dispatches on.</summary>
public readonly struct FanoutObserverDecision {
  public readonly string Recipient;
  public readonly double DistanceMeters;
  public readonly long? DeliveredDataRevision;
  public readonly ZdoFanoutDisposition Disposition;
  /// <summary>True only when the runner should perform a redirect for this observer: the disposition
  /// is <see cref="ZdoFanoutDisposition.Emit"/> AND this is the first occurrence of the recipient in
  /// the plan (so one logical revision produces at most one redirect per recipient).</summary>
  public readonly bool Emit;

  public FanoutObserverDecision(
      string recipient, double distanceMeters, long? deliveredDataRevision,
      ZdoFanoutDisposition disposition, bool emit) {
    Recipient = recipient;
    DistanceMeters = distanceMeters;
    DeliveredDataRevision = deliveredDataRevision;
    Disposition = disposition;
    Emit = emit;
  }
}

/// <summary>
/// Pure per-observer visibility decision for the ADR-0013 co-presence fan-out. Unity-free by
/// construction (like <see cref="ZdoBandPolicy"/> and <see cref="ZdoIntegrationContract"/>) so it
/// links into the headless test assembly and is driveable without a game.
///
/// Visibility is band-only: near/mid (<= outer radius) and landmarks are relevant; far (> outer) is
/// not. Fan-out v1 does not thin the mid band (that stays the per-recipient concern of the existing
/// single-recipient path); it asks only "should this observer see this object at all, and does it not
/// already have this revision?". Ownership is deliberately NOT an input — visibility must never be a
/// function of who owns the ZDO.
/// </summary>
public static class ZdoFanoutPolicy {
  public static ZdoFanoutDisposition Evaluate(
      double distanceMeters,
      double innerRadius,
      double outerRadius,
      double reachMeters,
      long dataRevision,
      long? deliveredDataRevision) {
    bool relevant = ZdoIntegrationContract.LandmarkAllows(distanceMeters, reachMeters)
        || distanceMeters <= outerRadius;
    if (!relevant) {
      return ZdoFanoutDisposition.OutOfBand;
    }
    if (deliveredDataRevision.HasValue && deliveredDataRevision.Value >= dataRevision) {
      return ZdoFanoutDisposition.AlreadyDelivered;
    }
    return ZdoFanoutDisposition.Emit;
  }
}

/// <summary>
/// Orders a full set of observers into fan-out decisions for one candidate ZDO, guaranteeing each
/// recipient is marked to emit at most once per logical (data) revision — the seam the runner
/// dispatches on. Evaluates every observer independently through <see cref="ZdoFanoutPolicy"/>, so
/// one recipient being AlreadyDelivered or OutOfBand can never suppress delivery to another. Input
/// order is preserved in the output.
/// </summary>
public static class ZdoFanoutPlan {
  public static IReadOnlyList<FanoutObserverDecision> Evaluate(
      long dataRevision,
      double innerRadius,
      double outerRadius,
      double reachMeters,
      IEnumerable<FanoutObserverInput> observers) {
    var seenRecipients = new HashSet<string>();
    var decisions = new List<FanoutObserverDecision>();
    foreach (FanoutObserverInput observer in observers) {
      ZdoFanoutDisposition disposition = ZdoFanoutPolicy.Evaluate(
          observer.DistanceMeters, innerRadius, outerRadius, reachMeters,
          dataRevision, observer.DeliveredDataRevision);
      // Emit at most once per recipient per revision: the first Emit occurrence wins; a repeat of the
      // same recipient (or a null recipient) is recorded but never dispatched.
      bool emit = disposition == ZdoFanoutDisposition.Emit
          && !string.IsNullOrEmpty(observer.Recipient)
          && seenRecipients.Add(observer.Recipient);
      decisions.Add(new FanoutObserverDecision(
          observer.Recipient, observer.DistanceMeters, observer.DeliveredDataRevision,
          disposition, emit));
    }
    return decisions;
  }
}
