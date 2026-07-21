namespace ComfyNetworkSense;

/// <summary>The redirect action for one ZDO, given the observing peer's distance to it.</summary>
public enum ZdoBandAction {
  /// <summary>Near band (0..inner): redirect every pass at full rate.</summary>
  EmitFull,
  /// <summary>Mid band (inner..outer), and this pass is due: redirect (and stamp last-emit).</summary>
  EmitThinned,
  /// <summary>Mid band, not yet due: suppress from native this pass, do NOT emit.</summary>
  HoldThinned,
  /// <summary>Far band (>outer): suppress from native, never emit.</summary>
  Drop,
  /// <summary>A granted-reach landmark within reach: always emit regardless of band.</summary>
  Landmark,
}

/// <summary>
/// Pure distance-band Area-of-Interest decision for the ZDO redirect producer. Unity-free by
/// construction (like <see cref="ZdoIntegrationContract"/>) so it links into the headless test
/// assembly and the band math is driveable without a game.
///
/// The measured shape (fieldlab/evidence/aoi-baseline-20260721): send-VOLUME is the tick ceiling, so
/// the win is thinning what is redirected by distance, per observing peer:
///
///   near     0 .. inner        -> EmitFull                  (full rate, where the player interacts)
///   mid      inner .. outer     -> EmitThinned / HoldThinned (thinned to ~1/thinInterval Hz)
///   far      > outer            -> Drop                      (never emitted; re-evaluated as the peer moves)
///   landmark reach>0, dist<=reach -> Landmark                (reliable-lane presence, overrides the band)
///
/// EmitFull/EmitThinned/Landmark are emit actions (enqueue to the gateway). Drop/HoldThinned are
/// suppress-only: the runner must still remove+ack the ZDO from Valheim's send list (remove without
/// ack = duplicate storm) but must NOT emit it and must NOT touch the gate seq counter.
///
/// Cadence is time-gated against a caller-supplied clock, not a mod-owned counter: the Harmony
/// postfix fires once per peer per CreateSyncList pass, so "5 Hz" means "emit at most once per
/// thinInterval" over whatever candidates native presents. A first sighting
/// (<paramref name="lastEmitSeconds"/> &lt; 0) is always due.
/// </summary>
public static class ZdoBandPolicy {
  public static ZdoBandAction Classify(
      double distanceMeters,
      double innerRadius,
      double outerRadius,
      double reachMeters,
      double nowSeconds,
      double lastEmitSeconds,
      double thinIntervalSeconds) {
    // Landmark presence overrides band shaping entirely — distance as a scarce, granted property.
    if (ZdoIntegrationContract.LandmarkAllows(distanceMeters, reachMeters)) {
      return ZdoBandAction.Landmark;
    }

    if (distanceMeters <= innerRadius) {
      return ZdoBandAction.EmitFull;
    }

    if (distanceMeters <= outerRadius) {
      bool due = lastEmitSeconds < 0.0 || (nowSeconds - lastEmitSeconds) >= thinIntervalSeconds;
      return due ? ZdoBandAction.EmitThinned : ZdoBandAction.HoldThinned;
    }

    return ZdoBandAction.Drop;
  }

  /// <summary>True for the actions that enqueue an envelope to the gateway.</summary>
  public static bool Emits(ZdoBandAction action) =>
      action == ZdoBandAction.EmitFull
      || action == ZdoBandAction.EmitThinned
      || action == ZdoBandAction.Landmark;
}
