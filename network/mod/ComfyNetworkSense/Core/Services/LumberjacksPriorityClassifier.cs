namespace ComfyNetworkSense;

using System;

using UnityEngine;

/// <summary>
/// Shared Valheim object classifier carried forward from the FieldLab priority-path runs.
/// The 256 m Era16 route proved these tiers over 11,544 observations before they were
/// promoted from side-channel metadata into authoritative delivery ordering.
/// </summary>
public static class LumberjacksPriorityClassifier {
  public static string Classify(
      string objectName,
      string[] componentNames,
      Vector3 objectPosition,
      Vector3 observerPosition,
      float radiusMeters,
      out int priorityRank,
      out string reason) {
    string name = (objectName ?? string.Empty).ToLowerInvariant();
    string joinedComponents = string.Join("|", componentNames ?? Array.Empty<string>()).ToLowerInvariant();
    float distance = Vector3.Distance(objectPosition, observerPosition);

    if (ContainsAny(name, "player") || ContainsAny(joinedComponents, "player", "character")) {
      priorityRank = 0;
      reason = "player_name_or_component";
      return "player_critical";
    }

    if (ContainsAny(name, "portal", "teleport") || ContainsAny(joinedComponents, "teleportworld", "teleport")) {
      priorityRank = 1;
      reason = "portal_name_or_component";
      return "portal";
    }

    if (ContainsAny(name, "bed", "guardstone", "ward", "piece_guardstone", "fire", "hearth", "bonfire", "shieldgenerator")
        || ContainsAny(joinedComponents, "bed", "fireplace", "privatearea", "shieldgenerator")) {
      priorityRank = 0;
      reason = "spawn_protection_or_survival_component";
      return "player_critical";
    }

    if (ContainsAny(name, "chest", "crate", "cart", "karve", "longship", "forge", "workbench", "artisan", "cauldron", "oven", "smelter", "kiln", "fermenter", "windmill", "spinningwheel", "sapcollector", "beehive")
        || ContainsAny(joinedComponents, "container", "craftingstation", "smelter", "fermenter", "cookingstation", "beehive", "sapcollector")) {
      priorityRank = 4;
      reason = "storage_or_crafting_name_or_component";
      return "storage_crafting";
    }

    if (ContainsAny(name, "door", "gate", "sign", "itemstand", "chair", "table", "maptable", "raven", "ship")
        || ContainsAny(joinedComponents, "door", "textreceiver", "itemstand", "chair", "ship", "maptable")) {
      priorityRank = 3;
      reason = "near_interactive_name_or_component";
      return "near_interactive";
    }

    if (ContainsAny(name, "pole", "beam", "pillar", "column", "foundation", "floor", "wall", "roof", "stone", "marble", "blackmarble", "iron_")) {
      priorityRank = 2;
      reason = "structural_piece_name";
      return "structural_anchor";
    }

    if (distance > radiusMeters * 0.66f
        && ContainsAny(name, "rug", "banner", "curtain", "deco", "piece_banner", "piece_tapestry", "piece_xmas", "piece_maypole")) {
      priorityRank = 6;
      reason = "far_decorative_name";
      return "decorative_far";
    }

    priorityRank = 5;
    reason = "default_loaded_piece";
    return "support_piece";
  }

  static bool ContainsAny(string value, params string[] needles) {
    foreach (string needle in needles) {
      if (value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return true;
    }
    return false;
  }
}
