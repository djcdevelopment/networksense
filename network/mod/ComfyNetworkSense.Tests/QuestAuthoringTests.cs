using System.Collections.Generic;
using System.Linq;
using ComfyNetworkSense;
using Xunit;

namespace ComfyNetworkSense.Tests;

public sealed class QuestAuthoringTests {
  [Fact]
  public void EverySafeEventHasHonestMetadataAndProducesAValidExampleDraft() {
    Assert.Equal(34, QuestEventCatalog.Count);
    foreach (string eventName in QuestEventCatalog.AllEventNames) {
      Assert.True(QuestEventCatalog.TryGet(eventName, out QuestEventCatalog.Definition definition));
      Assert.False(string.IsNullOrWhiteSpace(definition.TargetKind));
      Assert.False(string.IsNullOrWhiteSpace(definition.TargetDescription));
      Assert.False(string.IsNullOrWhiteSpace(definition.ExampleTarget));

      QuestEvent example = QuestAuthoring.ExampleEvent(eventName);
      Assert.NotNull(example);
      QuestDraft draft = QuestAuthoring.FromEvent(
          example, "example_" + eventName, "Example " + eventName, "Creators", 18);

      Assert.True(draft.Match.Matched);
      Assert.Empty(draft.Match.Diagnostics);
      Assert.Equal(eventName, draft.Quest.TriggerEvent);
      Assert.Equal(definition.ExampleTarget, draft.Quest.TriggerTarget);
      Assert.True(QuestAuthoring.ValidateDraft(draft.Json, example).Matched);
    }
  }

  [Fact]
  public void AStationDraftKeepsIdentityFieldsButNotVolatileQuantity() {
    var witnessed = new QuestEvent(
        "station_input_added",
        "CopperOre",
        fields: new Dictionary<string, string> {
          ["station"] = "smelter",
          ["item"] = "CopperOre",
          ["quantity"] = "7",
        });

    QuestDraft draft = QuestAuthoring.FromEvent(
        witnessed, "feed_smelter", "Feed the smelter", "Creators", 18);

    Assert.Equal("smelter", draft.Quest.TriggerWhere["station"]);
    Assert.Equal("CopperOre", draft.Quest.TriggerWhere["item"]);
    Assert.DoesNotContain("quantity", draft.Quest.TriggerWhere.Keys);
    Assert.DoesNotContain("\"quantity\"", draft.Json);
  }

  [Fact]
  public void CombatDraftCarriesMeaningfulSkillAndProjectileContext() {
    var witnessed = new QuestEvent(
        "kill", "$enemy_greydwarfbrute", "Bows", true);

    QuestDraft draft = QuestAuthoring.FromEvent(
        witnessed, "bow_brute", "Bow a brute", "Rangers", 18);

    Assert.Equal("Bows", draft.Quest.TriggerWeaponSkill);
    Assert.True(draft.Quest.TriggerProjectile);
    Assert.True(draft.Match.Matched);
  }

  [Fact]
  public void MissingWitnessTargetDoesNotInventTheCatalogExample() {
    QuestEvent witnessed = new QuestEvent("player_died", null);

    QuestDraft draft = QuestAuthoring.FromEvent(
        witnessed, "falling", "A dramatic exit", "Creators", 18);

    Assert.Null(draft.Quest.TriggerTarget);
    Assert.DoesNotContain("\"target\"", draft.Json);
    Assert.True(draft.Match.Matched);
  }

  [Fact]
  public void DraftTextRoundTripsCreatorCharactersThroughTheSharedLoader() {
    QuestDraft draft = QuestAuthoring.FromEvent(
        new QuestEvent("sign_written", "sign"),
        "quote_test",
        "Say \"hello\"\nthen sign",
        "Creator\\Guild",
        18);

    Assert.Equal("Say \"hello\"\nthen sign", draft.Quest.Name);
    Assert.Equal("Creator\\Guild", draft.Quest.Guild);
  }

  [Fact]
  public void ParseFailuresKeepTheSharedLoadersOwnMessageAndCode() {
    QuestMatchReport report = QuestAuthoring.ValidateDraft(
        "{ \"schema_version\": 9, \"quests\": [] }");

    QuestAuthoringDiagnostic diagnostic = Assert.Single(report.Diagnostics);
    Assert.Equal("contract.parse", diagnostic.Code);
    Assert.Contains("schema_version must be 1", diagnostic.Message);
    Assert.False(report.Matched);
  }

  [Fact]
  public void ANoisyMissNamesEveryIndependentlyFailingConstraint() {
    const string json = """
      {
        "schema_version": 1,
        "player": { "name": "creator" },
        "quests": [{
          "quest_id": "miss", "name": "Miss", "guild": "Creators", "era": 18,
          "category": "Combat", "auto_checked": false, "venue": "in_game",
          "trigger": {
            "event": "kill", "target": "Troll", "weapon_skill": "Bows",
            "projectile": true, "where": { "station": "forge" }
          }
        }]
      }
      """;
    var witnessed = new QuestEvent(
        "resource_damaged", "Beech1 (tree)", "Axes", false,
        fields: new Dictionary<string, string> { ["station"] = "workbench" });

    QuestMatchReport report = QuestAuthoring.ValidateDraft(json, witnessed);
    string[] codes = report.Diagnostics.Select(item => item.Code).ToArray();

    Assert.False(report.Matched);
    Assert.Contains("event.mismatch", codes);
    Assert.Contains("target.mismatch", codes);
    Assert.Contains("weapon_skill.mismatch", codes);
    Assert.Contains("projectile.mismatch", codes);
    Assert.Contains("where.mismatch", codes);
    Assert.DoesNotContain("matcher.unexplained", codes);
  }

  [Fact]
  public void EligibilityMissesRemainStructured() {
    const string json = """
      {
        "schema_version": 1,
        "quests": [{
          "quest_id": "external", "name": "External", "guild": "Creators",
          "auto_checked": true, "venue": "irl",
          "trigger": { "event": "kill", "target": "$enemy_greyling" }
        }]
      }
      """;

    QuestMatchReport report = QuestAuthoring.ValidateDraft(
        json, new QuestEvent("kill", "$enemy_greyling"));

    Assert.Contains(report.Diagnostics, item => item.Code == "quest.auto_checked");
    Assert.Contains(report.Diagnostics, item => item.Code == "quest.venue");
  }

  [Fact]
  public void MetadataDoesNotPromiseRedactedChatOrSignText() {
    Assert.True(QuestEventCatalog.TryGet("chat_sent", out QuestEventCatalog.Definition chat));
    Assert.True(QuestEventCatalog.TryGet("sign_written", out QuestEventCatalog.Definition sign));

    Assert.Contains("not the redacted message text", chat.TargetDescription);
    Assert.Contains("text is deliberately redacted", sign.TargetDescription);
    Assert.Empty(chat.Fields);
    Assert.Empty(sign.Fields);
  }
}
