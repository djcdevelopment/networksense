namespace ComfyNetworkSense;

using System;

/// <summary>
/// Unity-free acceptance bounds for the selected autonomous-creature canary.
/// A successful owner must execute BaseAI and publish resulting motion; a
/// replica must be denied BaseAI while still presenting that canonical motion.
/// </summary>
internal static class CreatureAiProofPolicy {
  public const int MinimumOwnerTicks = 40;
  public const int MinimumBlockedTicks = 40;
  public const int MinimumSnapshotAdvance = 20;
  public const float MinimumDisplacementMeters = 1.0f;
  public const double MaximumRecoverySeconds = 2.0;

  internal static bool AllowsMode(string mode) =>
      mode is "drive" or "observe";

  internal static bool DrivePasses(
      int ownerTicks,
      int blockedTicks,
      bool riderObserved,
      bool authorityChanged,
      float displacementMeters,
      int snapshotAdvance) =>
      ownerTicks >= MinimumOwnerTicks &&
      blockedTicks == 0 &&
      !riderObserved &&
      !authorityChanged &&
      Finite(displacementMeters) &&
      displacementMeters >= MinimumDisplacementMeters &&
      snapshotAdvance >= MinimumSnapshotAdvance;

  internal static bool ObservePasses(
      int ownerTicks,
      int blockedTicks,
      bool riderObserved,
      bool authorityChanged,
      float displacementMeters,
      int snapshotAdvance) =>
      ownerTicks == 0 &&
      blockedTicks >= MinimumBlockedTicks &&
      !riderObserved &&
      !authorityChanged &&
      Finite(displacementMeters) &&
      displacementMeters >= MinimumDisplacementMeters &&
      snapshotAdvance >= MinimumSnapshotAdvance;

  internal static bool RecoveryPasses(double seconds) =>
      !double.IsNaN(seconds) && !double.IsInfinity(seconds) &&
      seconds >= 0.0 && seconds <= MaximumRecoverySeconds;

  static bool Finite(float value) =>
      !float.IsNaN(value) && !float.IsInfinity(value);
}
