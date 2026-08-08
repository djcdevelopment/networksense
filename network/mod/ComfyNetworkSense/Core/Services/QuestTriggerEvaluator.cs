namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;

/// <summary>
/// Matches normalized, creator-facing gameplay events against tracked quests. The evaluator knows
/// nothing about Harmony methods or Valheim ownership routes; both mods compile this exact
/// Unity-free source and feed it <see cref="QuestEvent"/> envelopes named by
/// <see cref="QuestEventCatalog"/>.
///
/// Existing schema-1 kill quests and <see cref="OnCreatureKilled"/> callers remain compatible.
/// Other events use the same event/target filters plus additive scalar <c>trigger.where</c> fields.
/// Event dedupe is separate from quest cooldown so local/RPC alternatives cannot double-complete
/// even while a creator has deliberately set the quest cooldown to zero.
/// </summary>
public sealed class QuestTriggerEvaluator {
  const int MaxRecentEventKeys = 512;

  readonly double _cooldownSeconds;
  readonly double _dedupeWindowSeconds;
  readonly Dictionary<string, double> _lastFired = new(StringComparer.OrdinalIgnoreCase);
  readonly Dictionary<string, double> _recentEventKeys = new(StringComparer.Ordinal);

  public QuestTriggerEvaluator(double cooldownSeconds = 60.0, double dedupeWindowSeconds = 1.0) {
    _cooldownSeconds = Math.Max(0.0, cooldownSeconds);
    _dedupeWindowSeconds = Math.Max(0.0, dedupeWindowSeconds);
  }

  /// <summary>
  /// Evaluate one canonical event. Unknown or diagnostic-only event names cannot bind a quest.
  /// When <see cref="QuestEvent.DedupeKey"/> is present, a repeated witness inside the bounded
  /// dedupe window is consumed without evaluating or arming cooldowns a second time.
  /// </summary>
  public IReadOnlyList<QuestCompletion> OnEvent(
      IReadOnlyList<TrackedQuest> quests, QuestEvent gameplayEvent, double now) {
    if (gameplayEvent == null) {
      return Array.Empty<QuestCompletion>();
    }

    string canonicalEvent = QuestEventCatalog.CanonicalName(gameplayEvent.Name);
    if (canonicalEvent == null || IsDuplicate(gameplayEvent, canonicalEvent, now)) {
      return Array.Empty<QuestCompletion>();
    }
    if (quests == null || quests.Count == 0) {
      return Array.Empty<QuestCompletion>();
    }

    List<QuestCompletion> completions = null;
    foreach (TrackedQuest quest in quests) {
      if (!Matches(quest, gameplayEvent, canonicalEvent, now)) {
        continue;
      }

      _lastFired[quest.QuestId] = now;
      completions ??= new List<QuestCompletion>();
      completions.Add(new QuestCompletion(quest, canonicalEvent));
    }

    return completions ?? (IReadOnlyList<QuestCompletion>) Array.Empty<QuestCompletion>();
  }

  /// <summary>
  /// Backward-compatible kill lane. The optional key lets the producer join multiple death
  /// witnesses without changing any existing call site or quest file.
  /// </summary>
  public IReadOnlyList<QuestCompletion> OnCreatureKilled(
      IReadOnlyList<TrackedQuest> quests,
      string creature,
      string weaponSkill,
      bool ranged,
      double now,
      string dedupeKey = null) =>
      OnEvent(
          quests,
          QuestEvent.CreatureKilled(creature, weaponSkill, ranged, dedupeKey),
          now);

  bool Matches(TrackedQuest quest, QuestEvent gameplayEvent, string canonicalEvent, double now) {
    if (quest == null || !quest.HasTrigger || !quest.IsCapturable) {
      return false;
    }

    if (!QuestEventCatalog.TriggerMatches(quest.TriggerEvent, canonicalEvent)) {
      return false;
    }

    if (!TargetMatches(quest.TriggerTarget, gameplayEvent.Target)) {
      return false;
    }

    if (!string.IsNullOrWhiteSpace(quest.TriggerWeaponSkill)
        && !string.Equals(
            quest.TriggerWeaponSkill,
            gameplayEvent.WeaponSkill,
            StringComparison.OrdinalIgnoreCase)) {
      return false;
    }

    if (quest.TriggerProjectile && !gameplayEvent.Projectile) {
      return false;
    }

    if (!WhereMatches(quest.TriggerWhere, gameplayEvent)) {
      return false;
    }

    return !OnCooldown(quest.QuestId, now);
  }

  static bool WhereMatches(Dictionary<string, string> expected, QuestEvent gameplayEvent) {
    if (expected == null || expected.Count == 0) {
      return true;
    }

    foreach (KeyValuePair<string, string> field in expected) {
      if (!gameplayEvent.TryGetField(field.Key, out string actual)) {
        return false;
      }
      if (!string.Equals(field.Value, "any", StringComparison.OrdinalIgnoreCase)
          && !string.Equals(field.Value, actual, StringComparison.OrdinalIgnoreCase)) {
        return false;
      }
    }
    return true;
  }

  bool IsDuplicate(QuestEvent gameplayEvent, string canonicalEvent, double now) {
    if (_dedupeWindowSeconds <= 0.0 || string.IsNullOrWhiteSpace(gameplayEvent.DedupeKey)) {
      return false;
    }

    string key = canonicalEvent + "\n" + gameplayEvent.DedupeKey;
    if (_recentEventKeys.TryGetValue(key, out double previous)
        && now >= previous
        && now - previous < _dedupeWindowSeconds) {
      return true;
    }

    TrimRecentEventKeys();
    _recentEventKeys[key] = now;
    return false;
  }

  void TrimRecentEventKeys() {
    if (_recentEventKeys.Count < MaxRecentEventKeys) {
      return;
    }

    var ordered = new List<KeyValuePair<string, double>>(_recentEventKeys);
    ordered.Sort((left, right) => left.Value.CompareTo(right.Value));
    int remove = ordered.Count - (MaxRecentEventKeys / 2);
    for (int i = 0; i < remove; i++) {
      _recentEventKeys.Remove(ordered[i].Key);
    }
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

  /// <summary>Forget cooldown and event-dedupe state, such as after a creator reload.</summary>
  public void Reset() {
    _lastFired.Clear();
    _recentEventKeys.Clear();
  }

  static bool TargetMatches(string filter, string target) {
    if (string.IsNullOrWhiteSpace(filter)
        || string.Equals(filter, "any", StringComparison.OrdinalIgnoreCase)) {
      return true;
    }

    string normalized = (target ?? string.Empty)
        .ToLowerInvariant()
        .Replace("(clone)", string.Empty)
        .Trim();
    string normalizedFilter = filter.ToLowerInvariant();
    foreach (string alternative in normalizedFilter.Split(
        new[] { "_or_" }, StringSplitOptions.RemoveEmptyEntries)) {
      if (normalized.Contains(alternative.Trim())) {
        return true;
      }
    }
    return false;
  }
}

/// <summary>A quest that completed, reduced to the fields the completion event carries.</summary>
public readonly struct QuestCompletion {
  public string QuestId { get; }
  public string Name { get; }
  public string Guild { get; }
  public string Category { get; }
  public string BotCommand { get; }
  public string EventName { get; }

  public QuestCompletion(TrackedQuest quest) : this(quest, quest.TriggerEvent) {
  }

  public QuestCompletion(TrackedQuest quest, string eventName) {
    QuestId = quest.QuestId;
    Name = quest.Name;
    Guild = quest.Guild;
    Category = quest.Category;
    BotCommand = quest.BotCommand;
    EventName = eventName;
  }
}
