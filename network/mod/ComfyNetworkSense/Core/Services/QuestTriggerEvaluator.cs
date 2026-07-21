namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;

/// <summary>
/// Matches the already-classified gameplay events against the player's tracked quests and yields
/// completions to relay. Re-homed from the pruned comfy <c>QuestTriggerService</c>, reduced to the
/// telemetry seam: no Unity, no <c>SubmissionService</c>/screenshots/outbox.
///
/// One deliberate simplification vs. the comfy original: because the proof is now the durable
/// EventLog entry (ADR 0012) and not a pair of screenshots, the old two-shot <c>shots</c> sequence —
/// whose only job was to decide how many screenshots to capture (first-hit frame + kill frame) —
/// carries no behaviour here. A kill quest completes on the killing blow whether the fatal blow was
/// the first hit or the tenth. <c>on_first_hit</c> is preserved on the model as informational, and
/// hit-on-world-object ("hit") triggers (punchwood) are a deferred increment (the current seam only
/// hooks creatures), so this evaluator matches <c>kill</c> triggers only.
///
/// Pure and unit-tested; the producer feeds it the client-side kill signal it already computes.
/// </summary>
public sealed class QuestTriggerEvaluator {
  readonly double _cooldownSeconds;
  readonly Dictionary<string, double> _lastFired = new(StringComparer.OrdinalIgnoreCase);

  public QuestTriggerEvaluator(double cooldownSeconds = 60.0) {
    _cooldownSeconds = cooldownSeconds;
  }

  /// <summary>
  /// A creature was killed by the local player. Returns the tracked kill-quests that complete on this
  /// kill — filters (creature / weapon skill / projectile) matched and the quest is off cooldown — and
  /// arms their per-quest cooldowns. Empty when nothing matches.
  /// </summary>
  public IReadOnlyList<QuestCompletion> OnCreatureKilled(
      IReadOnlyList<TrackedQuest> quests, string creature, string weaponSkill, bool ranged, double now) {
    if (quests == null || quests.Count == 0) {
      return Array.Empty<QuestCompletion>();
    }

    List<QuestCompletion> completions = null;
    foreach (TrackedQuest quest in quests) {
      if (!Matches(quest, creature, weaponSkill, ranged, now)) {
        continue;
      }

      _lastFired[quest.QuestId] = now;
      completions ??= new List<QuestCompletion>();
      completions.Add(new QuestCompletion(quest));
    }

    return completions ?? (IReadOnlyList<QuestCompletion>) Array.Empty<QuestCompletion>();
  }

  bool Matches(TrackedQuest quest, string creature, string weaponSkill, bool ranged, double now) {
    if (!quest.HasTrigger || !quest.IsCapturable) {
      return false;
    }

    if (!string.Equals(quest.TriggerEvent, "kill", StringComparison.OrdinalIgnoreCase)) {
      return false;
    }

    if (!CreatureMatches(quest.TriggerTarget, creature)) {
      return false;
    }

    if (!string.IsNullOrWhiteSpace(quest.TriggerWeaponSkill)
        && !string.Equals(quest.TriggerWeaponSkill, weaponSkill, StringComparison.OrdinalIgnoreCase)) {
      return false;
    }

    if (quest.TriggerProjectile && !ranged) {
      return false;
    }

    return !OnCooldown(quest.QuestId, now);
  }

  bool OnCooldown(string questId, double now) {
    return _lastFired.TryGetValue(questId, out double last) && now - last < _cooldownSeconds;
  }

  /// <summary>Seconds until this quest's trigger re-arms; 0 when armed.</summary>
  public double CooldownRemaining(string questId, double now) {
    if (!_lastFired.TryGetValue(questId, out double last)) {
      return 0.0;
    }

    double remaining = _cooldownSeconds - (now - last);
    return remaining > 0.0 ? remaining : 0.0;
  }

  static bool CreatureMatches(string filter, string creature) {
    if (string.IsNullOrWhiteSpace(filter) || string.Equals(filter, "any", StringComparison.OrdinalIgnoreCase)) {
      return true;
    }

    string name = (creature ?? string.Empty).ToLowerInvariant().Replace("(clone)", string.Empty).Trim();
    return name.Contains(filter.ToLowerInvariant());
  }
}

/// <summary>A quest that completed, reduced to the fields the completion event carries.</summary>
public readonly struct QuestCompletion {
  public string QuestId { get; }
  public string Name { get; }
  public string Guild { get; }
  public string Category { get; }
  public string BotCommand { get; }

  public QuestCompletion(TrackedQuest quest) {
    QuestId = quest.QuestId;
    Name = quest.Name;
    Guild = quest.Guild;
    Category = quest.Category;
    BotCommand = quest.BotCommand;
  }
}
