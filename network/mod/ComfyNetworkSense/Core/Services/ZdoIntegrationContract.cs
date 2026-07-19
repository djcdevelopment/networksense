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
}
