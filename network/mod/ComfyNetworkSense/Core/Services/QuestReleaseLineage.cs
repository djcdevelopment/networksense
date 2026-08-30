namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>Immutable release identity projected beside a schema-1 quest view. The ordinary quest
/// loader remains backwards compatible; this narrower reader only supplies correlation fields for
/// durable EventLog rows and refuses malformed or partially pinned lineage.</summary>
public sealed class QuestReleaseLineage {
  public const string CurrentSchema = "creatoros-quest-view-lineage/v1";

  public string ReleaseId { get; set; }
  public string CampaignId { get; set; }
  public int CampaignRevision { get; set; }
  public string CompositionHash { get; set; }
  public string PackContentHash { get; set; }
  public string VenueId { get; set; }
  public int VenueRevision { get; set; }
  public string VenueSha256 { get; set; }
  public IReadOnlyDictionary<string, string> ExperienceIds { get; set; }

  public string ExperienceIdFor(string questId) {
    if (string.IsNullOrWhiteSpace(questId) || ExperienceIds == null) return null;
    return ExperienceIds.TryGetValue(questId, out string value) ? value : null;
  }
}

public static class QuestReleaseLineageLoader {
  const int MaxBytes = 1024 * 1024;
  static readonly Regex Pair = new(
      "\\\"((?:\\\\.|[^\\\"\\\\])*)\\\"\\s*:\\s*\\\"((?:\\\\.|[^\\\"\\\\])*)\\\"",
      RegexOptions.CultureInvariant);

  public static QuestReleaseLineage Current { get; private set; }
  public static string LastError { get; private set; }

  /// <summary>A missing lineage block is valid legacy input. A present block must be complete and
  /// exact; stale identity is worse than no identity because it would forge an evidence join.</summary>
  public static bool Load(string path) {
    Current = null;
    LastError = null;
    try {
      if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return true;
      FileInfo info = new(path);
      if (info.Length is <= 0 or > MaxBytes) throw new InvalidOperationException("quest view lineage file size is invalid");
      Current = Parse(File.ReadAllText(path, Encoding.UTF8));
      return true;
    } catch (Exception exception) {
      Current = null;
      LastError = exception.Message;
      return false;
    }
  }

  public static QuestReleaseLineage Parse(string json) {
    if (string.IsNullOrWhiteSpace(json)) throw new InvalidOperationException("quest view is empty");
    string body = ExtractObject(json, "release_lineage");
    if (body == null) return null;
    string schema = ReadString(body, "schema");
    string releaseId = ReadString(body, "release_id");
    string campaignId = ReadString(body, "campaign_id");
    int campaignRevision = ReadPositiveInt(body, "campaign_revision");
    string compositionHash = ReadString(body, "composition_hash");
    string packContentHash = ReadString(body, "pack_content_hash");
    string venueId = ReadString(body, "venue_id");
    int venueRevision = ReadPositiveInt(body, "venue_revision");
    string venueSha256 = ReadString(body, "venue_sha256");
    string experiences = ExtractObject(body, "experience_ids");
    if (schema != QuestReleaseLineage.CurrentSchema || !SafeId(releaseId) || !SafeId(campaignId)
        || !Sha(compositionHash) || !Sha(packContentHash) || !SafeId(venueId) || !Sha(venueSha256)
        || campaignRevision < 1 || venueRevision < 1 || experiences == null)
      throw new InvalidOperationException("quest view release lineage is incomplete or invalid");
    Dictionary<string, string> mappings = new(StringComparer.Ordinal);
    foreach (Match match in Pair.Matches(experiences)) {
      string questId = Unescape(match.Groups[1].Value);
      string experienceId = Unescape(match.Groups[2].Value);
      if (!SafeId(questId) || !SafeId(experienceId) || mappings.ContainsKey(questId))
        throw new InvalidOperationException("quest view experience lineage is invalid");
      mappings.Add(questId, experienceId);
    }
    if (mappings.Count is < 1 or > 64)
      throw new InvalidOperationException("quest view experience lineage is empty or too large");
    return new QuestReleaseLineage {
      ReleaseId = releaseId,
      CampaignId = campaignId,
      CampaignRevision = campaignRevision,
      CompositionHash = compositionHash,
      PackContentHash = packContentHash,
      VenueId = venueId,
      VenueRevision = venueRevision,
      VenueSha256 = venueSha256,
      ExperienceIds = mappings,
    };
  }

  static string ReadString(string json, string name) {
    Match match = Regex.Match(json,
        "\\\"" + Regex.Escape(name) + "\\\"\\s*:\\s*\\\"((?:\\\\.|[^\\\"\\\\])*)\\\"",
        RegexOptions.CultureInvariant);
    return match.Success ? Unescape(match.Groups[1].Value) : null;
  }

  static int ReadPositiveInt(string json, string name) {
    Match match = Regex.Match(json, "\\\"" + Regex.Escape(name) + "\\\"\\s*:\\s*([0-9]+)",
        RegexOptions.CultureInvariant);
    return match.Success && int.TryParse(match.Groups[1].Value, out int value) ? value : 0;
  }

  static string ExtractObject(string json, string name) {
    Match match = Regex.Match(json, "\\\"" + Regex.Escape(name) + "\\\"\\s*:\\s*\\{",
        RegexOptions.CultureInvariant);
    if (!match.Success) {
      if (Regex.IsMatch(json, "\\\"" + Regex.Escape(name) + "\\\"\\s*:", RegexOptions.CultureInvariant))
        throw new InvalidOperationException(name + " must be an object");
      return null;
    }
    int start = match.Index + match.Length - 1;
    int depth = 0;
    bool quoted = false, escaped = false;
    for (int index = start; index < json.Length; index++) {
      char character = json[index];
      if (quoted) {
        if (escaped) escaped = false;
        else if (character == '\\') escaped = true;
        else if (character == '"') quoted = false;
        continue;
      }
      if (character == '"') quoted = true;
      else if (character == '{') depth++;
      else if (character == '}' && --depth == 0) return json.Substring(start, index - start + 1);
    }
    throw new InvalidOperationException(name + " is unbalanced");
  }

  static bool SafeId(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 96
      && Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant);
  static bool Sha(string value) => value != null && Regex.IsMatch(value, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

  static string Unescape(string value) {
    if (string.IsNullOrEmpty(value) || value.IndexOf('\\') < 0) return value;
    StringBuilder result = new(value.Length);
    for (int index = 0; index < value.Length; index++) {
      char character = value[index];
      if (character != '\\' || index + 1 >= value.Length) { result.Append(character); continue; }
      char next = value[++index];
      result.Append(next switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '"' => '"', '\\' => '\\', '/' => '/', _ => next });
    }
    return result.ToString();
  }
}
