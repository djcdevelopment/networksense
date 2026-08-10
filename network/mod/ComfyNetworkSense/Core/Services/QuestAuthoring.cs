namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

/// <summary>One structured reason a draft could not parse or a quest did not match an event.</summary>
public sealed class QuestAuthoringDiagnostic {
  public string Code { get; }
  public string Field { get; }
  public string Expected { get; }
  public string Actual { get; }
  public string Message { get; }

  public QuestAuthoringDiagnostic(
      string code, string field, string expected, string actual, string message) {
    Code = code;
    Field = field;
    Expected = expected;
    Actual = actual;
    Message = message;
  }
}

/// <summary>Exact-evaluator result plus counterfactual reasons for a miss.</summary>
public sealed class QuestMatchReport {
  public bool Matched { get; internal set; }
  public readonly List<QuestAuthoringDiagnostic> Diagnostics =
      new List<QuestAuthoringDiagnostic>();
}

/// <summary>A generated schema-1 file that has already survived the shared loader and evaluator.</summary>
public sealed class QuestDraft {
  public string Json { get; internal set; }
  public TrackedQuest Quest { get; internal set; }
  public QuestMatchReport Match { get; internal set; }
}

/// <summary>
/// Unity-free creator primitives shared by ComfyNetworkSense, Quest Lab, and headless tests.
///
/// This deliberately does not contain a second quest parser or matcher. Generated JSON is handed
/// straight back to <see cref="QuestViewLoader.Parse"/>, and every match/miss answer comes from a
/// fresh <see cref="QuestTriggerEvaluator"/>. The diagnostic probes change one constraint at a
/// time and ask the evaluator again; they never restate its substring, alias, where, or eligibility
/// rules.
/// </summary>
public static class QuestAuthoring {
  /// <summary>Create a conservative quest from one witnessed canonical event.
  ///
  /// Target, meaningful weapon/projectile context, and metadata fields marked DraftByDefault are
  /// retained. Volatile amount/quantity fields remain visible in the catalog but are not copied,
  /// because an exact amount observed once is almost never the quest a creator intended.</summary>
  public static QuestDraft FromEvent(
      QuestEvent witnessed,
      string questId,
      string name,
      string guild,
      int era = 0,
      string category = null) {
    if (witnessed == null) {
      throw new ArgumentNullException(nameof(witnessed));
    }
    string canonical = QuestEventCatalog.CanonicalName(witnessed.Name);
    if (canonical == null
        || !QuestEventCatalog.TryGet(canonical, out QuestEventCatalog.Definition definition)) {
      throw new ArgumentException(
          "A creator draft requires a canonical bindable event.", nameof(witnessed));
    }

    // A catalog example is documentation, not evidence. If the producer did not expose a
    // target, leave the draft broad enough to match the event that was actually witnessed.
    string target = witnessed.Target;
    string weaponSkill = definition.SupportsWeaponSkill ? witnessed.WeaponSkill : null;
    bool projectile = definition.SupportsProjectile && witnessed.Projectile;
    var where = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (QuestEventCatalog.FieldDefinition field in definition.Fields) {
      if (field.DraftByDefault
          && witnessed.Fields.TryGetValue(field.Name, out string value)
          && !string.IsNullOrWhiteSpace(value)) {
        where[field.Name] = value;
      }
    }

    string json = RenderView(
        questId,
        name,
        guild,
        era,
        string.IsNullOrWhiteSpace(category) ? definition.Category : category,
        canonical,
        target,
        weaponSkill,
        projectile,
        where);

    List<TrackedQuest> parsed = QuestViewLoader.Parse(json, out string _);
    if (parsed.Count != 1) {
      throw new InvalidOperationException(
          "Creator draft generation did not round-trip exactly one quest through the shared loader.");
    }

    QuestMatchReport match = Diagnose(parsed[0], witnessed);
    if (!match.Matched) {
      throw new InvalidOperationException(
          "Creator draft generation produced a quest the shared evaluator did not match.");
    }
    return new QuestDraft { Json = json, Quest = parsed[0], Match = match };
  }

  /// <summary>Construct the catalog's honest synthetic example envelope for a creator preview.</summary>
  public static QuestEvent ExampleEvent(string eventName) {
    string canonical = QuestEventCatalog.CanonicalName(eventName);
    if (canonical == null
        || !QuestEventCatalog.TryGet(canonical, out QuestEventCatalog.Definition definition)) {
      return null;
    }
    var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (QuestEventCatalog.FieldDefinition field in definition.Fields) {
      fields[field.Name] = field.Example;
    }
    return new QuestEvent(
        canonical,
        definition.ExampleTarget,
        definition.SupportsWeaponSkill ? "Axes" : null,
        false,
        fields: fields);
  }

  /// <summary>Parse a single-quest draft through the shipping loader, then optionally diagnose it
  /// against a witnessed event. Parse errors remain the loader's own words.</summary>
  public static QuestMatchReport ValidateDraft(string json, QuestEvent witnessed = null) {
    var report = new QuestMatchReport();
    List<TrackedQuest> parsed;
    try {
      parsed = QuestViewLoader.Parse(json, out string _);
    } catch (Exception ex) {
      report.Diagnostics.Add(new QuestAuthoringDiagnostic(
          "contract.parse", "view", "schema-1 quest view", null, ex.Message));
      return report;
    }
    if (parsed.Count != 1) {
      report.Diagnostics.Add(new QuestAuthoringDiagnostic(
          "contract.quest_count",
          "quests",
          "1",
          parsed.Count.ToString(CultureInfo.InvariantCulture),
          "A creator draft must contain exactly one quest."));
      return report;
    }
    if (witnessed == null) {
      report.Matched = true;
      return report;
    }
    return Diagnose(parsed[0], witnessed);
  }

  /// <summary>Ask the evaluator whether a quest matches, then isolate mismatching constraints by
  /// asking it counterfactual questions. This is explainability around the matcher, not a mirror
  /// implementation of it.</summary>
  public static QuestMatchReport Diagnose(TrackedQuest quest, QuestEvent witnessed) {
    var report = new QuestMatchReport();
    if (quest == null || witnessed == null) {
      report.Diagnostics.Add(new QuestAuthoringDiagnostic(
          "input.missing", "quest/event", "both present", null,
          "A quest and witnessed event are both required."));
      return report;
    }
    if (EvaluatorMatches(quest, witnessed)) {
      report.Matched = true;
      return report;
    }

    if (!quest.HasTrigger) {
      Add(report, "trigger.missing", "trigger.event", "canonical event", null,
          "This quest has no in-game trigger.");
    }
    if (quest.AutoChecked) {
      Add(report, "quest.auto_checked", "auto_checked", "false", "true",
          "The shared evaluator excludes externally auto-checked quests.");
    }
    if (!string.Equals(quest.Venue, "in_game", StringComparison.OrdinalIgnoreCase)) {
      Add(report, "quest.venue", "venue", "in_game", quest.Venue,
          "The shared evaluator only captures in-game quests.");
    }

    string actualCanonical = QuestEventCatalog.CanonicalName(witnessed.Name);
    if (actualCanonical == null) {
      Add(report, "event.unbindable", "event", "canonical bindable event", witnessed.Name,
          "The witnessed event is outside the creator-safe catalog.");
      return report;
    }

    if (!ConstraintMatches(CloneForProbe(quest, eventName: quest.TriggerEvent), witnessed)) {
      Add(report, "event.mismatch", "trigger.event", quest.TriggerEvent, actualCanonical,
          "This trigger event does not accept the witnessed canonical event.");
    }
    if (!string.IsNullOrWhiteSpace(quest.TriggerTarget)
        && !ConstraintMatches(
            CloneForProbe(quest, eventName: actualCanonical, target: quest.TriggerTarget),
            witnessed)) {
      Add(report, "target.mismatch", "trigger.target", quest.TriggerTarget, witnessed.Target,
          "The witnessed target does not satisfy this target constraint.");
    }
    if (!string.IsNullOrWhiteSpace(quest.TriggerWeaponSkill)
        && !ConstraintMatches(
            CloneForProbe(
                quest, eventName: actualCanonical, weaponSkill: quest.TriggerWeaponSkill),
            witnessed)) {
      Add(report, "weapon_skill.mismatch", "trigger.weapon_skill", quest.TriggerWeaponSkill,
          witnessed.WeaponSkill,
          "The witnessed weapon skill does not satisfy this constraint.");
    }
    if (quest.TriggerProjectile
        && !ConstraintMatches(
            CloneForProbe(quest, eventName: actualCanonical, projectile: true), witnessed)) {
      Add(report, "projectile.mismatch", "trigger.projectile", "true",
          witnessed.Projectile ? "true" : "false",
          "This quest requires a projectile event.");
    }
    if (quest.TriggerWhere != null) {
      foreach (KeyValuePair<string, string> field in quest.TriggerWhere) {
        var isolated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
          [field.Key] = field.Value,
        };
        if (!ConstraintMatches(
            CloneForProbe(quest, eventName: actualCanonical, where: isolated), witnessed)) {
          witnessed.TryGetField(field.Key, out string actual);
          Add(report, "where.mismatch", "trigger.where." + field.Key, field.Value, actual,
              "The witnessed event does not satisfy this field constraint.");
        }
      }
    }

    if (report.Diagnostics.Count == 0) {
      Add(report, "matcher.unexplained", "trigger", "match", "miss",
          "The exact evaluator rejected this quest, but no single isolated constraint explains it.");
    }
    return report;
  }

  static bool ConstraintMatches(TrackedQuest constraint, QuestEvent witnessed) =>
      EvaluatorMatches(constraint, witnessed);

  static bool EvaluatorMatches(TrackedQuest quest, QuestEvent witnessed) {
    var evaluator = new QuestTriggerEvaluator(0.0, 0.0);
    return evaluator.OnEvent(new[] { quest }, witnessed, 0.0).Count > 0;
  }

  /// <summary>Create an in-game, capturable one-constraint probe. Null means wildcard/absent.</summary>
  static TrackedQuest CloneForProbe(
      TrackedQuest source,
      string eventName = null,
      string target = null,
      string weaponSkill = null,
      bool projectile = false,
      Dictionary<string, string> where = null) =>
      new TrackedQuest {
        QuestId = string.IsNullOrWhiteSpace(source.QuestId) ? "creator-probe" : source.QuestId,
        Name = string.IsNullOrWhiteSpace(source.Name) ? "Creator probe" : source.Name,
        Guild = string.IsNullOrWhiteSpace(source.Guild) ? "Creator" : source.Guild,
        Era = source.Era,
        Category = source.Category,
        BotCommand = source.BotCommand,
        AutoChecked = false,
        Venue = "in_game",
        TriggerEvent = eventName,
        TriggerTarget = target,
        TriggerWeaponSkill = weaponSkill,
        TriggerProjectile = projectile,
        TriggerWhere = where,
      };

  static void Add(
      QuestMatchReport report,
      string code,
      string field,
      string expected,
      string actual,
      string message) =>
      report.Diagnostics.Add(
          new QuestAuthoringDiagnostic(code, field, expected, actual, message));

  static string RenderView(
      string questId,
      string name,
      string guild,
      int era,
      string category,
      string eventName,
      string target,
      string weaponSkill,
      bool projectile,
      Dictionary<string, string> where) {
    var json = new StringBuilder();
    json.Append("{\n  \"schema_version\": 1,\n")
        .Append("  \"player\": { \"name\": \"creator\" },\n")
        .Append("  \"quests\": [\n    {\n")
        .Append("      \"quest_id\": ").Append(Quote(questId)).Append(",\n")
        .Append("      \"name\": ").Append(Quote(name)).Append(",\n")
        .Append("      \"guild\": ").Append(Quote(guild)).Append(",\n")
        .Append("      \"era\": ").Append(era.ToString(CultureInfo.InvariantCulture)).Append(",\n")
        .Append("      \"category\": ").Append(Quote(category)).Append(",\n")
        .Append("      \"bot_command\": null,\n")
        .Append("      \"auto_checked\": false,\n")
        .Append("      \"venue\": \"in_game\",\n")
        .Append("      \"trigger\": {\n")
        .Append("        \"event\": ").Append(Quote(eventName));
    if (!string.IsNullOrWhiteSpace(target)) {
      json.Append(",\n        \"target\": ").Append(Quote(target));
    }
    if (!string.IsNullOrWhiteSpace(weaponSkill)) {
      json.Append(",\n        \"weapon_skill\": ").Append(Quote(weaponSkill));
    }
    if (projectile) {
      json.Append(",\n        \"projectile\": true");
    }
    if (where != null && where.Count > 0) {
      json.Append(",\n        \"where\": {");
      bool first = true;
      foreach (KeyValuePair<string, string> field in where) {
        json.Append(first ? "\n" : ",\n")
            .Append("          ").Append(Quote(field.Key)).Append(": ")
            .Append(Quote(field.Value));
        first = false;
      }
      json.Append("\n        }");
    }
    json.Append("\n      }\n    }\n  ]\n}\n");
    return json.ToString();
  }

  static string Quote(string value) {
    if (value == null) {
      return "null";
    }
    var escaped = new StringBuilder(value.Length + 2).Append('"');
    foreach (char c in value) {
      switch (c) {
        case '"': escaped.Append("\\\""); break;
        case '\\': escaped.Append("\\\\"); break;
        case '\n': escaped.Append("\\n"); break;
        case '\r': escaped.Append("\\r"); break;
        case '\t': escaped.Append("\\t"); break;
        default:
          if (char.IsControl(c)) {
            escaped.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
          } else {
            escaped.Append(c);
          }
          break;
      }
    }
    return escaped.Append('"').ToString();
  }
}
