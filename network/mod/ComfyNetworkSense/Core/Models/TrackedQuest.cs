namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;

/// <summary>
/// One quest selected in a <c>quest-view.json</c>, reduced to what the mod needs to match a gameplay
/// event and attribute a completion. Re-homed from the pruned comfy quest slice
/// (<c>handoffs/comfy-control-surface/Core/TrackedQuest.cs</c>) with the submission/evidence coupling
/// removed: no <c>ToAction()</c>/<c>ControlAction</c>, no screenshots, no outbox — the durable
/// EventLog entry is the proof now (ADR 0012, quest-evaluator track). Unity-free by construction so
/// it links into the test project.
/// </summary>
public sealed class TrackedQuest {
  public string QuestId { get; set; }
  public string Name { get; set; }
  public string Guild { get; set; }
  public string Era { get; set; }
  public string Category { get; set; }
  public string BotCommand { get; set; }
  public bool AutoChecked { get; set; }
  public string Venue { get; set; }

  public string TriggerEvent { get; set; }
  public string TriggerTarget { get; set; }
  public string TriggerWeaponSkill { get; set; }
  public bool TriggerProjectile { get; set; }
  public List<string> TriggerShots { get; set; }

  /// <summary>
  /// Additive event-specific scalar filters from <c>trigger.where</c>. Existing quest files omit
  /// this field and retain their exact behaviour. Keys match <see cref="QuestEvent.Fields"/> plus
  /// the universal event/target/weapon_skill/projectile fields.
  /// </summary>
  public Dictionary<string, string> TriggerWhere { get; set; }

  /// <summary>Quests the game can auto-capture: not externally auto-checked and not IRL.</summary>
  public bool IsCapturable =>
      !AutoChecked && !string.Equals(Venue, "irl", StringComparison.OrdinalIgnoreCase);

  /// <summary>Has an in-game trigger spec (as opposed to a manual or IRL quest).</summary>
  public bool HasTrigger => !string.IsNullOrWhiteSpace(TriggerEvent);

  /// <summary>Two-shot quests arm on the first hit and confirm on the kill.</summary>
  public bool WantsFirstHitShot => TriggerShots != null && TriggerShots.Contains("on_first_hit");
}
