namespace ComfyNetworkSense;

using System.Collections.Generic;

/// <summary>
/// The action a server-side combat observation should produce. Unity-free so this whole classifier
/// can be unit-tested (it is <c>&lt;Compile Link&gt;</c>-ed into ComfyNetworkSense.Tests, exactly like
/// ZdoBandPolicy). MVP (Increment 1) is killing_blow only; first_hit / weapon_used arrive in
/// Increment 2 as new kinds without changing this contract.
/// </summary>
public enum GameplayEventKind {
  None = 0,
  KillingBlow = 1,
}

/// <summary>
/// Pure, deterministic classifier for server-side creature-damage observations. All inputs are
/// primitives — the Unity/Valheim reads (attacker resolution, IsDead, instance id) happen in the
/// producer, never here. Holds only per-creature dedup state so a single death (which the native
/// damage path can report more than once in a frame) emits exactly one killing_blow.
/// </summary>
public sealed class GameplayEventClassifier {
  const int PruneThreshold = 512;

  readonly Dictionary<int, double> _lastKill = new();
  readonly double _dedupSeconds;

  public GameplayEventClassifier(double dedupSeconds = 2.0) {
    _dedupSeconds = dedupSeconds > 0 ? dedupSeconds : 2.0;
  }

  /// <summary>
  /// Classify a creature-damage observation. Emits <see cref="GameplayEventKind.KillingBlow"/> when
  /// a player-attributed blow kills a creature, deduped per creature instance within the window.
  /// </summary>
  /// <param name="creatureInstanceId">Stable per-creature id (Character.GetInstanceID()).</param>
  /// <param name="attackerIsPlayer">True if the resolved attacker is a player (not env/another creature).</param>
  /// <param name="creatureDied">True if the creature is dead after this blow.</param>
  /// <param name="nowSeconds">Monotonic seconds (Time.realtimeSinceStartup); used for dedup only.</param>
  public GameplayEventKind ClassifyCreatureDamage(
      int creatureInstanceId, bool attackerIsPlayer, bool creatureDied, double nowSeconds) {
    if (!attackerIsPlayer || !creatureDied) {
      return GameplayEventKind.None;
    }

    if (_lastKill.TryGetValue(creatureInstanceId, out double last) && nowSeconds - last < _dedupSeconds) {
      return GameplayEventKind.None;
    }

    _lastKill[creatureInstanceId] = nowSeconds;
    Prune(nowSeconds);
    return GameplayEventKind.KillingBlow;
  }

  /// <summary>Bound the dedup map: drop entries older than a few dedup windows once it grows.</summary>
  void Prune(double nowSeconds) {
    if (_lastKill.Count < PruneThreshold) {
      return;
    }

    List<int> stale = new();
    foreach (KeyValuePair<int, double> entry in _lastKill) {
      if (nowSeconds - entry.Value > _dedupSeconds * 4) {
        stale.Add(entry.Key);
      }
    }

    foreach (int key in stale) {
      _lastKill.Remove(key);
    }
  }
}
