namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;

/// <summary>
/// One stable, creator-facing gameplay event. Harmony method names and ownership routes never
/// cross this boundary: patches normalize them to a name from <see cref="QuestEventCatalog"/>, a
/// subject in <see cref="Target"/>, and optional scalar fields before the evaluator sees them.
///
/// Unity-free by construction so ComfyNetworkSense, Quest Lab, and the headless contract tests
/// compile the same source file. The copied, case-insensitive field dictionary prevents a patch
/// from changing an event after it has entered the evaluator.
/// </summary>
public sealed class QuestEvent {
  static readonly IReadOnlyDictionary<string, string> EmptyFields =
      new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

  public string Name { get; }
  public string Target { get; }
  public string WeaponSkill { get; }
  public bool Projectile { get; }

  /// <summary>
  /// Identity shared by alternative witnesses of one game action. A local method and its owning
  /// RPC must provide the same key; independent actions must provide different keys. Empty means
  /// the producer has no safe identity and asks the evaluator not to deduplicate.
  /// </summary>
  public string DedupeKey { get; }

  /// <summary>
  /// Event-specific scalar payload, matched by a quest's additive <c>trigger.where</c> object.
  /// Values are deliberately strings: quest JSON, logs, receipts, and both target frameworks can
  /// round-trip them without a type-coercion vocabulary.
  /// </summary>
  public IReadOnlyDictionary<string, string> Fields { get; }

  public QuestEvent(
      string name,
      string target = null,
      string weaponSkill = null,
      bool projectile = false,
      string dedupeKey = null,
      IReadOnlyDictionary<string, string> fields = null) {
    Name = name;
    Target = target;
    WeaponSkill = weaponSkill;
    Projectile = projectile;
    DedupeKey = dedupeKey;

    if (fields == null || fields.Count == 0) {
      Fields = EmptyFields;
      return;
    }

    var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (KeyValuePair<string, string> field in fields) {
      if (!string.IsNullOrWhiteSpace(field.Key) && field.Value != null) {
        copy[field.Key] = field.Value;
      }
    }
    Fields = copy;
  }

  /// <summary>Read a universal or event-specific field using creator JSON spelling.</summary>
  public bool TryGetField(string name, out string value) {
    value = null;
    if (string.IsNullOrWhiteSpace(name)) {
      return false;
    }

    if (string.Equals(name, "event", StringComparison.OrdinalIgnoreCase)) {
      value = Name;
      return value != null;
    }
    if (string.Equals(name, "target", StringComparison.OrdinalIgnoreCase)) {
      value = Target;
      return value != null;
    }
    if (string.Equals(name, "weapon_skill", StringComparison.OrdinalIgnoreCase)) {
      value = WeaponSkill;
      return value != null;
    }
    if (string.Equals(name, "projectile", StringComparison.OrdinalIgnoreCase)) {
      value = Projectile ? "true" : "false";
      return true;
    }

    return Fields.TryGetValue(name, out value);
  }

  /// <summary>The existing kill producer's compatibility envelope.</summary>
  public static QuestEvent CreatureKilled(
      string creature, string weaponSkill, bool ranged, string dedupeKey = null) =>
      new QuestEvent("kill", creature, weaponSkill, ranged, dedupeKey);
}
