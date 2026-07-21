namespace ComfyNetworkSense;

using System.Collections.Generic;

/// <summary>
/// The action a server-side combat observation should produce. Unity-free so this whole classifier
/// can be unit-tested (it is <c>&lt;Compile Link&gt;</c>-ed into ComfyNetworkSense.Tests, exactly like
/// ZdoBandPolicy).
/// </summary>
public enum GameplayEventKind {
  None = 0,
  FirstHit = 1,
  KillingBlow = 2,
}

/// <summary>
/// Pure, deterministic classifier for player-vs-creature combat. All inputs are primitives — the
/// Unity/Valheim reads (attacker resolution, IsDead, instance id) happen in the producer, never
/// here. Holds only per-creature state:
/// <list type="bullet">
/// <item><see cref="RegisterHit"/> — emits <see cref="GameplayEventKind.FirstHit"/> the first time a
/// player hits a creature (within the window); subsequent hits are <see cref="GameplayEventKind.None"/>.</item>
/// <item><see cref="RegisterDeath"/> — emits <see cref="GameplayEventKind.KillingBlow"/> once per
/// creature death (deduped), and clears the creature's first-hit state so a reused instance id starts fresh.</item>
/// </list>
/// The window bounds id reuse (the engine recycles instance ids after a creature despawns) and keeps
/// the maps from growing.
/// </summary>
public sealed class GameplayEventClassifier {
  const int PruneThreshold = 512;

  readonly Dictionary<int, double> _firstHitAt = new();
  readonly Dictionary<int, double> _killedAt = new();
  readonly double _windowSeconds;

  public GameplayEventClassifier(double windowSeconds = 30.0) {
    _windowSeconds = windowSeconds > 0 ? windowSeconds : 30.0;
  }

  /// <summary>First player hit on a creature → FirstHit; a subsequent hit within the window → None.</summary>
  public GameplayEventKind RegisterHit(int creatureInstanceId, double nowSeconds) {
    if (_firstHitAt.TryGetValue(creatureInstanceId, out double last) && nowSeconds - last < _windowSeconds) {
      return GameplayEventKind.None;
    }

    _firstHitAt[creatureInstanceId] = nowSeconds;
    Prune(_firstHitAt, nowSeconds);
    return GameplayEventKind.FirstHit;
  }

  /// <summary>Creature death → KillingBlow, deduped per creature within the window.</summary>
  public GameplayEventKind RegisterDeath(int creatureInstanceId, double nowSeconds) {
    _firstHitAt.Remove(creatureInstanceId);

    if (_killedAt.TryGetValue(creatureInstanceId, out double last) && nowSeconds - last < _windowSeconds) {
      return GameplayEventKind.None;
    }

    _killedAt[creatureInstanceId] = nowSeconds;
    Prune(_killedAt, nowSeconds);
    return GameplayEventKind.KillingBlow;
  }

  static void Prune(Dictionary<int, double> map, double nowSeconds) {
    if (map.Count < PruneThreshold) {
      return;
    }

    List<int> stale = new();
    foreach (KeyValuePair<int, double> entry in map) {
      if (nowSeconds - entry.Value > 60.0) {
        stale.Add(entry.Key);
      }
    }

    foreach (int key in stale) {
      map.Remove(key);
    }
  }
}
