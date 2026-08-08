namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Loads a player's <c>quest-view.json</c> (authored by the surviving picker,
/// <c>recipes/quest-catalogs/render_quest_picker.py</c>) into a <see cref="TrackedQuest"/> list.
/// Re-homed and minimalized from the pruned comfy <c>QuestViewLoader</c>: the schema is unchanged
/// (<c>recipes/quest-catalogs/quest-view-schema.md</c>) but the comfy
/// <c>WorkbenchPaths</c>/<c>AtomicFile</c>/<c>StatusFiles</c> infra is dropped. Parsing follows the
/// mod's regex idiom (<c>ZdoRedirectEnvelopeCodec</c>) and is Unity-free by construction, so
/// <see cref="Parse"/> links into the test project. The caller resolves the config-dir path and
/// hands it to <see cref="Load"/>.
/// </summary>
public static class QuestViewLoader {
  public static IReadOnlyList<TrackedQuest> Quests { get; private set; } = Array.Empty<TrackedQuest>();
  public static string PlayerName { get; private set; }
  public static string LastError { get; private set; }

  /// <summary>
  /// Read and parse the file at <paramref name="path"/>. A missing file is NOT an error — it just
  /// means no quests are tracked (returns true, empty list). Returns false only on a malformed file,
  /// with the reason in <see cref="LastError"/>; the tracked set is cleared in that case.
  /// </summary>
  public static bool Load(string path) {
    try {
      if (string.IsNullOrEmpty(path) || !File.Exists(path)) {
        Quests = Array.Empty<TrackedQuest>();
        PlayerName = null;
        LastError = null;
        return true;
      }

      string json = File.ReadAllText(path, Encoding.UTF8);
      List<TrackedQuest> quests = Parse(json, out string playerName);
      Quests = quests;
      PlayerName = playerName;
      LastError = null;
      return true;
    } catch (Exception exception) {
      Quests = Array.Empty<TrackedQuest>();
      PlayerName = null;
      LastError = exception.Message;
      return false;
    }
  }

  /// <summary>
  /// Pure parse — the unit-test entry. Throws <see cref="InvalidOperationException"/> with a clean,
  /// human-readable message on a schema or shape error.
  /// </summary>
  public static List<TrackedQuest> Parse(string json, out string playerName) {
    if (string.IsNullOrWhiteSpace(json)) {
      throw new InvalidOperationException("quest-view.json is empty.");
    }

    Match schema = Regex.Match(json, "\"schema_version\"\\s*:\\s*(\\d+)");
    if (!schema.Success || schema.Groups[1].Value != "1") {
      throw new InvalidOperationException("quest-view.json schema_version must be 1.");
    }

    playerName = ReadPlayerName(json);

    string questsBody = ExtractArrayBody(json, "quests");
    if (questsBody == null) {
      throw new InvalidOperationException("quest-view.json must contain a quests array.");
    }

    List<TrackedQuest> quests = new();
    foreach (string objectText in ExtractObjects(questsBody)) {
      TrackedQuest quest = ParseQuest(objectText);
      ValidateQuest(quest);
      quests.Add(quest);
    }

    return quests;
  }

  static TrackedQuest ParseQuest(string objectText) {
    string triggerBody = ExtractObjectBody(objectText, "trigger");

    return new TrackedQuest {
      QuestId = ReadString(objectText, "quest_id"),
      Name = ReadString(objectText, "name"),
      Guild = ReadString(objectText, "guild"),
      Era = ReadValue(objectText, "era"),
      Category = ReadString(objectText, "category"),
      BotCommand = ReadString(objectText, "bot_command"),
      AutoChecked = ReadBool(objectText, "auto_checked"),
      Venue = ReadString(objectText, "venue") ?? "in_game",
      TriggerEvent = triggerBody == null ? null : ReadString(triggerBody, "event"),
      TriggerTarget = triggerBody == null ? null : ReadString(triggerBody, "target"),
      TriggerWeaponSkill = triggerBody == null ? null : ReadString(triggerBody, "weapon_skill"),
      TriggerProjectile = triggerBody != null && ReadBool(triggerBody, "projectile"),
      TriggerShots = triggerBody == null ? null : ReadShots(triggerBody),
      TriggerWhere = triggerBody == null ? null : ReadWhere(triggerBody),
    };
  }

  static void ValidateQuest(TrackedQuest quest) {
    if (string.IsNullOrWhiteSpace(quest.QuestId)) {
      throw new InvalidOperationException("A tracked quest is missing quest_id.");
    }

    if (string.IsNullOrWhiteSpace(quest.Name)) {
      throw new InvalidOperationException($"Tracked quest {quest.QuestId} is missing name.");
    }

    if (string.IsNullOrWhiteSpace(quest.Guild)) {
      throw new InvalidOperationException($"Tracked quest {quest.QuestId} is missing guild.");
    }
  }

  static string ReadPlayerName(string json) {
    string playerBody = ExtractObjectBody(json, "player");
    return playerBody == null ? null : ReadString(playerBody, "name");
  }

  // --- JSON reads (regex, string-aware balanced extraction) — the ZdoRedirectEnvelopeCodec idiom ---

  static string ReadString(string json, string name) {
    Match match = Regex.Match(json, "\"" + Regex.Escape(name) + "\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
    return match.Success ? Unescape(match.Groups[1].Value) : null;
  }

  static string ReadValue(string json, string name) {
    Match match = Regex.Match(json, "\"" + Regex.Escape(name) + "\"\\s*:\\s*([^,}\\]\\s]+)");
    return match.Success ? match.Groups[1].Value : null;
  }

  static bool ReadBool(string json, string name) {
    Match match = Regex.Match(json, "\"" + Regex.Escape(name) + "\"\\s*:\\s*(true|false)");
    return match.Success && match.Groups[1].Value == "true";
  }

  static readonly Regex QuotedStringRegex = new("\"((?:\\\\.|[^\"\\\\])*)\"", RegexOptions.CultureInvariant);

  static List<string> ReadShots(string triggerBody) {
    string shotsBody = ExtractArrayBody(triggerBody, "shots");
    if (shotsBody == null) {
      return null;
    }

    List<string> shots = new();
    foreach (Match item in QuotedStringRegex.Matches(shotsBody)) {
      shots.Add(Unescape(item.Groups[1].Value));
    }

    return shots.Count > 0 ? shots : null;
  }

  /// <summary>
  /// Parse the additive <c>trigger.where</c> object. It is intentionally scalar-only: accepting a
  /// nested value and then ignoring it would silently widen a quest trigger, which is the unsafe
  /// direction. Strings, JSON numbers, and booleans retain their text spelling for exact matching
  /// against <see cref="QuestEvent.Fields"/>.
  /// </summary>
  static Dictionary<string, string> ReadWhere(string triggerBody) {
    string body = ExtractObjectBody(triggerBody, "where");
    if (body == null) {
      if (Regex.IsMatch(triggerBody, "\"where\"\\s*:")) {
        throw new InvalidOperationException("trigger.where must be an object of scalar values.");
      }
      return null;
    }

    var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    int index = 1; // opening brace
    while (true) {
      SkipWhitespace(body, ref index);
      if (index >= body.Length - 1) {
        break;
      }
      if (body[index] != '"') {
        throw new InvalidOperationException("trigger.where contains a malformed field name.");
      }

      string name = ReadJsonString(body, ref index);
      if (string.IsNullOrWhiteSpace(name)) {
        throw new InvalidOperationException("trigger.where field names cannot be empty.");
      }
      SkipWhitespace(body, ref index);
      if (index >= body.Length || body[index] != ':') {
        throw new InvalidOperationException("trigger.where contains a field without a value.");
      }
      index++;
      SkipWhitespace(body, ref index);
      if (index >= body.Length - 1) {
        throw new InvalidOperationException("trigger.where contains a field without a value.");
      }

      string value;
      if (body[index] == '"') {
        value = ReadJsonString(body, ref index);
      } else {
        int start = index;
        while (index < body.Length - 1 && body[index] != ',') {
          index++;
        }
        value = body.Substring(start, index - start).Trim();
        if (!ScalarTokenRegex.IsMatch(value)) {
          throw new InvalidOperationException(
              "trigger.where values must be strings, numbers, or booleans.");
        }
      }

      if (fields.ContainsKey(name)) {
        throw new InvalidOperationException("trigger.where repeats field '" + name + "'.");
      }
      fields.Add(name, value);

      SkipWhitespace(body, ref index);
      if (index >= body.Length - 1) {
        break;
      }
      if (body[index] != ',') {
        throw new InvalidOperationException("trigger.where fields must be comma-separated.");
      }
      index++;
    }

    return fields.Count == 0 ? null : fields;
  }

  static readonly Regex ScalarTokenRegex = new(
      "^(?:true|false|-?(?:0|[1-9]\\d*)(?:\\.\\d+)?(?:[eE][+-]?\\d+)?)$",
      RegexOptions.CultureInvariant);

  static void SkipWhitespace(string text, ref int index) {
    while (index < text.Length && char.IsWhiteSpace(text[index])) {
      index++;
    }
  }

  /// <summary>Read one JSON string at <paramref name="index"/> and advance past its quote.</summary>
  static string ReadJsonString(string text, ref int index) {
    if (index >= text.Length || text[index] != '"') {
      throw new InvalidOperationException("Expected a JSON string.");
    }

    int contentStart = ++index;
    bool escape = false;
    while (index < text.Length) {
      char c = text[index];
      if (escape) {
        escape = false;
      } else if (c == '\\') {
        escape = true;
      } else if (c == '"') {
        string value = text.Substring(contentStart, index - contentStart);
        index++;
        return Unescape(value);
      }
      index++;
    }

    throw new InvalidOperationException("Unterminated JSON string in trigger.where.");
  }

  /// <summary>The <c>{...}</c> body of the object at key <paramref name="name"/> (braces included),
  /// or null if the key is absent or its value is not an object (e.g. <c>"trigger": null</c>).</summary>
  static string ExtractObjectBody(string json, string name) {
    Match match = Regex.Match(json, "\"" + Regex.Escape(name) + "\"\\s*:\\s*\\{");
    if (!match.Success) {
      return null;
    }

    return ExtractBalanced(json, match.Index + match.Length - 1);
  }

  /// <summary>The inner text of the array at key <paramref name="name"/> (without the surrounding
  /// brackets), or null if the key is absent or its value is not an array.</summary>
  static string ExtractArrayBody(string json, string name) {
    Match match = Regex.Match(json, "\"" + Regex.Escape(name) + "\"\\s*:\\s*\\[");
    if (!match.Success) {
      return null;
    }

    string balanced = ExtractBalanced(json, match.Index + match.Length - 1);
    return balanced == null ? null : balanced.Substring(1, balanced.Length - 2);
  }

  /// <summary>Yield each top-level <c>{...}</c> object in an array body, string- and nesting-aware.</summary>
  static IEnumerable<string> ExtractObjects(string arrayBody) {
    int i = 0;
    bool inString = false, escape = false;
    while (i < arrayBody.Length) {
      char c = arrayBody[i];
      if (inString) {
        if (escape) {
          escape = false;
        } else if (c == '\\') {
          escape = true;
        } else if (c == '"') {
          inString = false;
        }

        i++;
        continue;
      }

      if (c == '"') {
        inString = true;
        i++;
        continue;
      }

      if (c == '{') {
        string obj = ExtractBalanced(arrayBody, i);
        if (obj == null) {
          yield break;
        }

        yield return obj;
        i += obj.Length;
        continue;
      }

      i++;
    }
  }

  /// <summary>The balanced <c>{...}</c> or <c>[...]</c> substring starting at <paramref name="openIndex"/>,
  /// counting only the opening delimiter's pair and skipping string contents; null if unbalanced.</summary>
  static string ExtractBalanced(string s, int openIndex) {
    char open = s[openIndex];
    char close = open == '{' ? '}' : ']';
    int depth = 0;
    bool inString = false, escape = false;

    for (int i = openIndex; i < s.Length; i++) {
      char c = s[i];
      if (inString) {
        if (escape) {
          escape = false;
        } else if (c == '\\') {
          escape = true;
        } else if (c == '"') {
          inString = false;
        }

        continue;
      }

      if (c == '"') {
        inString = true;
      } else if (c == open) {
        depth++;
      } else if (c == close) {
        depth--;
        if (depth == 0) {
          return s.Substring(openIndex, i - openIndex + 1);
        }
      }
    }

    return null;
  }

  static string Unescape(string value) {
    if (string.IsNullOrEmpty(value) || value.IndexOf('\\') < 0) {
      return value;
    }

    StringBuilder builder = new(value.Length);
    for (int i = 0; i < value.Length; i++) {
      char c = value[i];
      if (c != '\\' || i + 1 >= value.Length) {
        builder.Append(c);
        continue;
      }

      char next = value[++i];
      builder.Append(next switch {
        'n' => '\n',
        't' => '\t',
        'r' => '\r',
        '"' => '"',
        '\\' => '\\',
        '/' => '/',
        _ => next,
      });
    }

    return builder.ToString();
  }
}
