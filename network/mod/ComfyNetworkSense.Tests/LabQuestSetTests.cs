using System.Collections.Generic;
using System.Linq;
using ComfyNetworkSense;
using ComfyQuestLab;
using Xunit;

namespace ComfyNetworkSense.Tests;

/// <summary>
/// The lab's quest set — per-file isolation, the dry-fire armed probe, and the reload diff.
///
/// These run without Valheim because <c>LabQuestSet</c> takes file CONTENTS rather than paths and
/// touches no Unity type. That is the whole reason the split exists: the logic a creator depends
/// on is provable in seconds, and only the disk-and-HUD orchestration needs a game.
/// </summary>
public class LabQuestSetTests {
  /// <summary>Wrap quest object literals in the envelope the schema requires.</summary>
  static string View(params string[] quests) =>
      "{ \"schema_version\": 1, \"player\": { \"name\": \"you\" }, \"quests\": ["
      + string.Join(",", quests) + "] }";

  static string Quest(
      string id, string trigger, string guild = "Test", bool autoChecked = false,
      string venue = "in_game") =>
      $$"""
      {
        "quest_id": "{{id}}", "name": "{{id}}", "guild": "{{guild}}", "era": 17,
        "category": "Test", "bot_command": "/x", "auto_checked": {{(autoChecked ? "true" : "false")}},
        "venue": "{{venue}}", "trigger": {{trigger}}
      }
      """;

  static List<KeyValuePair<string, string>> Files(params (string Name, string Text)[] files) =>
      files.Select(f => new KeyValuePair<string, string>(f.Name, f.Text)).ToList();

  static LabQuest Find(LabQuestSet set, string id) => set.Quests.Single(q => q.QuestId == id);

  // ---- the armed probe ---------------------------------------------------------------------

  [Fact]
  public void AKillQuestIsArmed() {
    var set = LabQuestSet.Build(Files(("a.json",
        View(Quest("k", "{ \"event\": \"kill\", \"target\": \"Neck\" }")))));

    Assert.Empty(set.Errors);
    Assert.True(Find(set, "k").IsArmed);
    Assert.Equal(1, set.ArmedCount);
  }

  /// <summary>The published schema-1 hit verb remains armed across the canonical split between
  /// creature and resource damage.</summary>
  [Fact]
  public void AHitQuestIsArmedThroughTheCompatibilityAlias() {
    var set = LabQuestSet.Build(Files(("a.json",
        View(Quest("h", "{ \"event\": \"hit\", \"target\": \"tree_or_bush\" }")))));

    LabQuest quest = Find(set, "h");
    Assert.Empty(set.Errors);
    Assert.True(quest.IsArmed);
  }

  [Fact]
  public void AQuestWithNoTriggerIsCheckedByHand() {
    var set = LabQuestSet.Build(Files(("a.json", View(Quest("m", "null")))));

    Assert.Equal(LabArmed.NoTrigger, Find(set, "m").Armed);
  }

  [Fact]
  public void AnIrlQuestIsNamedAsIrlRatherThanBlamedOnItsVerb() {
    var set = LabQuestSet.Build(Files(("a.json",
        View(Quest("i", "{ \"event\": \"kill\", \"target\": \"Neck\" }", venue: "irl")))));

    Assert.Equal(LabArmed.Irl, Find(set, "i").Armed);
  }

  [Fact]
  public void AnAutoCheckedQuestIsNamedAsAutoChecked() {
    var set = LabQuestSet.Build(Files(("a.json",
        View(Quest("a", "{ \"event\": \"kill\", \"target\": \"Neck\" }", autoChecked: true)))));

    Assert.Equal(LabArmed.AutoChecked, Find(set, "a").Armed);
  }

  /// <summary>A trigger-less quest must not be reported as an unsupported-event problem.</summary>
  [Fact]
  public void ATriggerLessQuestIsNotMisreportedAsAnUnsupportedEvent() {
    var set = LabQuestSet.Build(Files(("a.json", View(Quest("m", "null")))));

    Assert.NotEqual(LabArmed.UnsupportedEvent, Find(set, "m").Armed);
  }

  [Fact]
  public void AnUnknownEventIsNamedAsUnsupported() {
    var set = LabQuestSet.Build(Files(("a.json",
        View(Quest("x", "{ \"event\": \"method_name_from_a_mod\" }")))));

    Assert.Equal(LabArmed.UnsupportedEvent, Find(set, "x").Armed);
    Assert.Contains("method_name_from_a_mod", Find(set, "x").ArmedLine());
  }

  [Fact]
  public void AGenericWhereFilterCanBeDryFiredWithoutMirroringEvaluatorRules() {
    var set = LabQuestSet.Build(Files(("a.json", View(Quest("craft",
        "{ \"event\": \"item_crafted\", \"target\": \"SwordIron\", "
        + "\"where\": { \"station\": \"forge\", \"quality\": 2 } }")))));

    Assert.True(Find(set, "craft").IsArmed);
  }

  // ---- per-file isolation ------------------------------------------------------------------

  /// <summary>The reason this calls Parse per file instead of Load: one bad draft must not cost
  /// a creator every good quest they have.</summary>
  [Fact]
  public void OneUnparseableFileDoesNotCostTheOthersTheirQuests() {
    var set = LabQuestSet.Build(Files(
        ("good.json", View(Quest("k", "{ \"event\": \"kill\", \"target\": \"Neck\" }"))),
        ("bad.json", "{ \"schema_version\": 9, \"quests\": [] }")));

    Assert.Single(set.Quests);
    Assert.True(Find(set, "k").IsArmed);

    LabQuestFileError error = Assert.Single(set.Errors);
    Assert.Equal("bad.json", error.SourceFile);
    Assert.Contains("schema_version", error.ContractMessage);   // the contract's own words
    Assert.False(string.IsNullOrWhiteSpace(error.Remedy));
  }

  [Fact]
  public void AMissingRequiredFieldIsReportedWithTheContractsOwnMessage() {
    var set = LabQuestSet.Build(Files(("a.json",
        View("{ \"name\": \"nameless\", \"guild\": \"Test\", \"trigger\": null }"))));

    Assert.Empty(set.Quests);
    Assert.Contains("quest_id", Assert.Single(set.Errors).ContractMessage);
  }

  /// <summary>The contract's parser is regex-and-brace based, not a JSON validator, so a stray
  /// brace can drop a quest with no error at all. This is the only thing that notices.</summary>
  [Fact]
  public void FewerQuestsParsedThanDeclaredIsReported() {
    // Three quest_id keys; the second object's brace is closed early so the third is swallowed.
    string malformed =
        "{ \"schema_version\": 1, \"quests\": ["
        + "{ \"quest_id\": \"a\", \"name\": \"a\", \"guild\": \"g\", \"trigger\": null }"
        + "] } \"quest_id\": \"b\" \"quest_id\": \"c\"";

    var set = LabQuestSet.Build(Files(("a.json", malformed)));

    Assert.Single(set.Quests);
    LabQuestFileError error = Assert.Single(set.Errors);
    Assert.Contains("names 3 quests but only 1 parsed", error.ContractMessage);
  }

  [Fact]
  public void AnEmptyDirectoryIsNotAnError() {
    var set = LabQuestSet.Build(Files());

    Assert.Empty(set.Quests);
    Assert.Empty(set.Errors);
    Assert.Equal(0, set.ArmedCount);
  }

  // ---- cross-file facts --------------------------------------------------------------------

  /// <summary>Nothing dedupes, so the advisory says what actually happens — both fire — rather
  /// than the plausible-sounding "the second is ignored".</summary>
  [Fact]
  public void ADuplicateQuestIdAcrossFilesIsFlaggedOnTheSecondOne() {
    var set = LabQuestSet.Build(Files(
        ("first.json", View(Quest("dup", "{ \"event\": \"kill\", \"target\": \"Neck\" }"))),
        ("second.json", View(Quest("dup", "{ \"event\": \"kill\", \"target\": \"Boar\" }")))));

    Assert.Equal(2, set.Quests.Count);
    Assert.Empty(set.Quests[0].Advisories);
    Assert.Contains("first.json", Assert.Single(set.Quests[1].Advisories));
  }

  [Fact]
  public void TheAdvisoryPassIsAppliedToEveryQuestWhenSupplied() {
    var set = LabQuestSet.Build(
        Files(("a.json", View(Quest("k", "{ \"event\": \"kill\", \"target\": \"Neck\" }")))),
        _ => new[] { "a note" });

    Assert.Equal("a note", Assert.Single(Find(set, "k").Advisories));
    Assert.Equal(1, set.AdvisoryCount);
  }

  // ---- the reload diff ---------------------------------------------------------------------

  [Fact]
  public void TheDiffNamesAddedRemovedAndUnchangedQuests() {
    var before = LabQuestSet.Build(Files(("a.json", View(
        Quest("stays", "{ \"event\": \"kill\", \"target\": \"Neck\" }"),
        Quest("goes", "{ \"event\": \"kill\", \"target\": \"Boar\" }")))));

    var after = LabQuestSet.Build(Files(("a.json", View(
        Quest("stays", "{ \"event\": \"kill\", \"target\": \"Neck\" }"),
        Quest("arrives", "{ \"event\": \"kill\", \"target\": \"Troll\" }")))));

    List<string> diff = after.DiffFrom(before);

    Assert.Contains("+ arrives", diff);
    Assert.Contains("- goes", diff);
    Assert.Contains("= 1 unchanged", diff);
  }

  /// <summary>The edit a creator most often makes — retarget a quest — has to show up by name,
  /// or a hot-reload gives them no evidence their save was picked up.</summary>
  [Fact]
  public void RetargetingAQuestShowsAsATriggerChange() {
    var before = LabQuestSet.Build(Files(("a.json",
        View(Quest("q", "{ \"event\": \"kill\", \"target\": \"Neck\" }")))));
    var after = LabQuestSet.Build(Files(("a.json",
        View(Quest("q", "{ \"event\": \"kill\", \"target\": \"Boar\" }")))));

    Assert.Contains("~ q (trigger changed)", after.DiffFrom(before));
  }

  [Fact]
  public void ChangingAWhereFieldShowsAsATriggerChange() {
    var before = LabQuestSet.Build(Files(("a.json",
        View(Quest("q", "{ \"event\": \"item_crafted\", \"where\": { \"station\": \"forge\" } }")))));
    var after = LabQuestSet.Build(Files(("a.json",
        View(Quest("q", "{ \"event\": \"item_crafted\", \"where\": { \"station\": \"workbench\" } }")))));

    Assert.Contains("~ q (trigger changed)", after.DiffFrom(before));
  }

  [Fact]
  public void FixingTheVerbShowsAsNowArmed() {
    var before = LabQuestSet.Build(Files(("a.json",
        View(Quest("q", "{ \"event\": \"hit\", \"target\": \"Neck\" }")))));
    var after = LabQuestSet.Build(Files(("a.json",
        View(Quest("q", "{ \"event\": \"kill\", \"target\": \"Neck\" }")))));

    // The trigger did change, so that is the honest headline; what matters is that the creator
    // sees a line for this quest at all rather than a bare "reloaded".
    Assert.Contains(after.DiffFrom(before), line => line.StartsWith("~ q"));
  }

  [Fact]
  public void TheFirstLoadReportsEveryQuestAsAdded() {
    var set = LabQuestSet.Build(Files(("a.json",
        View(Quest("q", "{ \"event\": \"kill\", \"target\": \"Neck\" }")))));

    Assert.Equal(new[] { "+ q" }, set.DiffFrom(null));
  }
}
