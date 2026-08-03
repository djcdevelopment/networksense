namespace ComfyNetworkSense;

/// <summary>
/// A new Valheim process starts with an empty local ZDO bank even when it
/// resumes the same stable logical peer. Its first canonical interest must
/// therefore request a durable snapshot; steady-state registrations do not.
/// </summary>
internal static class ZdoJournalInterestPolicy {
  public static bool ShouldRefreshProcessRegistration(
      bool hasRegisteredInterest) => !hasRegisteredInterest;
}
