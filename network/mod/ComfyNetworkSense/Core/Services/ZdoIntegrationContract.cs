namespace ComfyNetworkSense;

/// <summary>
/// The versioned server-to-Gateway submission contract shared by the live redirect runner and
/// the headless contract tests. Keep this type free of Unity and Valheim references.
/// </summary>
public static class ZdoIntegrationContract {
  public const int SchemaVersion = 2;
  public const string Operation = "zdo_redirect";
  public const string LegacyRecipient = "legacy";

  public static bool ImportanceAllows(int priorityRank, int maximumPriorityRank) =>
      priorityRank >= 0 && priorityRank <= maximumPriorityRank;

  /// <summary>
  /// Landmark reach — the parallel admission exemption to the rank gate. A landmark is a static,
  /// reliable-lane object granted long-range PRESENCE: it is admitted when the observer is within
  /// its authored reach, regardless of priority rank. <paramref name="reachMeters"/> &lt;= 0 means
  /// "not a landmark" (no reach was granted), so an ordinary object is never admitted this way. See
  /// landmark-reach-design.md: this is what lets a lighthouse reach a client that an aggressive
  /// interest cut would otherwise never present it to — distance becomes a scarce, earned property.
  /// </summary>
  public static bool LandmarkAllows(double distanceMeters, double reachMeters) =>
      reachMeters > 0.0 && distanceMeters >= 0.0 && distanceMeters <= reachMeters;

  /// <summary>
  /// The full network-admission decision at the redirect boundary (ZdoRedirectRunner): admit if the
  /// rank gate allows (<see cref="ImportanceAllows"/>) OR this object is a landmark within its reach
  /// of the observer (<see cref="LandmarkAllows"/>). The landmark path adds no per-tick datagram
  /// cost — a static landmark is pure reliable-lane traffic; this predicate only widens what the
  /// existing suppression admits at redirect time.
  /// </summary>
  public static bool Admits(
      int priorityRank, int maximumPriorityRank, double distanceMeters, double reachMeters) =>
      ImportanceAllows(priorityRank, maximumPriorityRank)
      || LandmarkAllows(distanceMeters, reachMeters);
}
