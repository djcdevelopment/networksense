namespace ComfyNetworkSense;

/// <summary>
/// Client half of the session-resume contract. A Gateway that still holds a zombie
/// incarnation for our token now evicts it and resumes us (server half), but any Gateway
/// build that refuses the token would otherwise be retried forever at 500 ms — the
/// candidate-8/9 reconnect storm. After <see cref="MaxRefusedResumeAttempts"/> consecutive
/// connections that die before session_started while presenting a resume token, the runner
/// abandons the token and reincarnates: fresh session plus full resync beats an infinite
/// livelock. Failed attempts also back off so the storm cannot saturate the Gateway.
/// </summary>
public sealed class ResumeReattachPolicy {
  public const int MaxRefusedResumeAttempts = 3;
  public const int BaseRetryDelayMs = 500;
  public const int MaxRetryDelayMs = 5000;

  int _refusedStreak;

  public int RefusedStreak => _refusedStreak;

  /// <summary>A session_started arrived: the transport is healthy, whatever the resume verdict.</summary>
  public void OnSessionStarted() {
    _refusedStreak = 0;
  }

  /// <summary>
  /// The connection ended before session_started. Returns true when the caller should
  /// abandon the resume token and reincarnate; the streak resets so the fresh identity
  /// starts with a clean retry budget.
  /// </summary>
  public bool OnConnectionEndedWithoutSessionStarted(bool hadResumeToken) {
    if (!hadResumeToken) return false;
    _refusedStreak++;
    if (_refusedStreak < MaxRefusedResumeAttempts) return false;
    _refusedStreak = 0;
    return true;
  }

  public int NextRetryDelayMs {
    get {
      if (_refusedStreak <= 0) return BaseRetryDelayMs;
      long delay = BaseRetryDelayMs * (1L << (_refusedStreak < 3 ? _refusedStreak : 3));
      return delay < MaxRetryDelayMs ? (int)delay : MaxRetryDelayMs;
    }
  }
}
