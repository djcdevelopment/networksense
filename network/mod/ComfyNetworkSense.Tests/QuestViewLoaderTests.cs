using System.Collections.Generic;
using System.Linq;
using ComfyNetworkSense;
using Xunit;

namespace ComfyNetworkSense.Tests;

public class QuestViewLoaderTests {
  // A verbatim copy of the comfy quest slice's fixture
  // (handoffs/comfy-control-surface/fixtures/quest-view.json) — the schema this loader must accept
  // unchanged. Kept inline so the test is self-contained (no dependency on the comfy backup path).
  const string Fixture = """
  {
    "schema_version": 1,
    "player": { "name": "Tugcow", "discord": null },
    "created_at": "2026-07-01T22:00:00Z",
    "picker_version": 1,
    "quests": [
      {
        "quest_id": "punchwood",
        "name": "Punchwood",
        "category": "Smoke test",
        "coopable": false,
        "requirements": "Punch a bush or tree with your bare hands (unarmed).\nEvidence captures automatically.",
        "reward": "1 Splinter",
        "evidence": { "screenshots": 1, "video_alternative": false, "link": false, "group_turnin": false, "notes": false },
        "evidence_note": null,
        "bot_command": "/comfy test summons_type:Punchwood image:",
        "auto_checked": false,
        "venue": "in_game",
        "trigger": { "event": "hit", "target": "tree_or_bush", "weapon_skill": "Unarmed" },
        "guild": "Test",
        "era": 17
      },
      {
        "quest_id": "neck_romancer",
        "name": "Neck Romancer",
        "category": "Smoke test",
        "coopable": false,
        "requirements": "Kill a Neck with your bare fists (unarmed).\nEvidence captures automatically.",
        "reward": "1 Neck Tail (emotional)",
        "evidence": { "screenshots": 2, "video_alternative": false, "link": false, "group_turnin": false, "notes": false },
        "evidence_note": null,
        "bot_command": "/comfy test summons_type:Neck Romancer image: image2:",
        "auto_checked": false,
        "venue": "in_game",
        "trigger": { "event": "kill", "target": "Neck", "weapon_skill": "Unarmed", "shots": [ "on_first_hit", "on_death" ] },
        "guild": "Test",
        "era": 17
      },
      {
        "quest_id": "air_drop",
        "name": "Air Drop",
        "category": "General summons",
        "coopable": false,
        "requirements": "Kill a Deathsquito with a thrown Spear",
        "reward": null,
        "evidence": { "screenshots": 2, "video_alternative": false, "link": false, "group_turnin": false, "notes": false },
        "evidence_note": null,
        "bot_command": "/slayer summons summons_type:Air Drop image: image2:",
        "auto_checked": false,
        "venue": "in_game",
        "trigger": null,
        "guild": "Slayers",
        "era": 17
      },
      {
        "quest_id": "animal_care",
        "name": "Animal Care",
        "category": "IRL Badges",
        "coopable": false,
        "requirements": "Post a photo of yourself feeding an animal.",
        "reward": null,
        "evidence": { "screenshots": 1, "video_alternative": false, "link": false, "group_turnin": false, "notes": false },
        "evidence_note": "Photo with handmade sign",
        "bot_command": "/ranger badge badge_name:Animal Care image:",
        "auto_checked": false,
        "venue": "irl",
        "trigger": null,
        "guild": "Rangers",
        "era": 17
      }
    ]
  }
  """;

  static TrackedQuest Quest(List<TrackedQuest> quests, string id) =>
      quests.Single(q => q.QuestId == id);

  [Fact]
  public void ParsesEveryQuestAndThePlayerName() {
    List<TrackedQuest> quests = QuestViewLoader.Parse(Fixture, out string player);

    Assert.Equal("Tugcow", player);
    Assert.Equal(4, quests.Count);
    Assert.Equal(
        new[] { "punchwood", "neck_romancer", "air_drop", "animal_care" },
        quests.Select(q => q.QuestId).ToArray());
  }

  [Fact]
  public void KillTrigger_ReadsTargetSkillAndTwoShotSequence() {
    TrackedQuest neck = Quest(QuestViewLoader.Parse(Fixture, out _), "neck_romancer");

    Assert.True(neck.HasTrigger);
    Assert.True(neck.IsCapturable);
    Assert.Equal("kill", neck.TriggerEvent);
    Assert.Equal("Neck", neck.TriggerTarget);
    Assert.Equal("Unarmed", neck.TriggerWeaponSkill);
    Assert.False(neck.TriggerProjectile);
    Assert.Equal(new[] { "on_first_hit", "on_death" }, neck.TriggerShots.ToArray());
    Assert.True(neck.WantsFirstHitShot);
  }

  [Fact]
  public void HitTrigger_HasNoShotsAndDoesNotWantAFirstHitSequence() {
    TrackedQuest punch = Quest(QuestViewLoader.Parse(Fixture, out _), "punchwood");

    Assert.Equal("hit", punch.TriggerEvent);
    Assert.Equal("tree_or_bush", punch.TriggerTarget);
    Assert.Equal("Unarmed", punch.TriggerWeaponSkill);
    Assert.Null(punch.TriggerShots);
    Assert.False(punch.WantsFirstHitShot);
    Assert.Equal("Test", punch.Guild);
    Assert.Equal("17", punch.Era);
  }

  [Fact]
  public void NullTrigger_MeansNoTriggerAndFieldsStayNull() {
    TrackedQuest airDrop = Quest(QuestViewLoader.Parse(Fixture, out _), "air_drop");

    Assert.False(airDrop.HasTrigger);
    Assert.Null(airDrop.TriggerEvent);
    Assert.Null(airDrop.TriggerTarget);
    Assert.True(airDrop.IsCapturable);  // in_game, not auto-checked — capturable, just not auto-triggered
  }

  [Fact]
  public void IrlVenue_IsNotCapturable() {
    TrackedQuest irl = Quest(QuestViewLoader.Parse(Fixture, out _), "animal_care");

    Assert.False(irl.IsCapturable);
    Assert.Equal("irl", irl.Venue);
  }

  [Fact]
  public void AutoCheckedQuest_IsNotCapturable() {
    const string json = """
    { "schema_version": 1, "player": { "name": "P" }, "quests": [
      { "quest_id": "auto", "name": "Auto", "guild": "G", "auto_checked": true, "venue": "in_game",
        "trigger": { "event": "kill", "target": "Neck" } } ] }
    """;

    TrackedQuest quest = QuestViewLoader.Parse(json, out _).Single();

    Assert.True(quest.AutoChecked);
    Assert.False(quest.IsCapturable);
  }

  [Fact]
  public void MissingVenue_DefaultsToInGame() {
    const string json = """
    { "schema_version": 1, "quests": [
      { "quest_id": "q", "name": "Q", "guild": "G", "trigger": { "event": "kill", "target": "Boar" } } ] }
    """;

    TrackedQuest quest = QuestViewLoader.Parse(json, out _).Single();

    Assert.Equal("in_game", quest.Venue);
    Assert.True(quest.IsCapturable);
  }

  [Fact]
  public void WrongSchemaVersion_Throws() {
    const string json = """{ "schema_version": 2, "quests": [] }""";

    var ex = Assert.Throws<System.InvalidOperationException>(() => QuestViewLoader.Parse(json, out _));
    Assert.Contains("schema_version", ex.Message);
  }

  [Fact]
  public void QuestMissingQuestId_Throws() {
    const string json = """
    { "schema_version": 1, "quests": [ { "name": "No Id", "guild": "G" } ] }
    """;

    Assert.Throws<System.InvalidOperationException>(() => QuestViewLoader.Parse(json, out _));
  }

  [Fact]
  public void EmptyQuestsArray_ParsesToNoQuests() {
    const string json = """{ "schema_version": 1, "player": { "name": "P" }, "quests": [] }""";

    List<TrackedQuest> quests = QuestViewLoader.Parse(json, out string player);

    Assert.Empty(quests);
    Assert.Equal("P", player);
  }

  [Fact]
  public void Load_MissingFile_IsNotAnError() {
    bool ok = QuestViewLoader.Load(@"Z:\definitely\not\here\quest-view.json");

    Assert.True(ok);
    Assert.Empty(QuestViewLoader.Quests);
    Assert.Null(QuestViewLoader.LastError);
  }

  [Fact]
  public void AdditiveWhereObjectParsesScalarFieldsWithoutChangingSchemaVersion() {
    const string json = """
    { "schema_version": 1, "quests": [
      { "quest_id": "craft", "name": "Craft", "guild": "G", "trigger": {
        "event": "item_crafted", "target": "SwordIron",
        "where": { "station": "forge", "quality": 2, "upgraded": true }
      } }
    ] }
    """;

    TrackedQuest quest = QuestViewLoader.Parse(json, out _).Single();

    Assert.Equal("item_crafted", quest.TriggerEvent);
    Assert.Equal("SwordIron", quest.TriggerTarget);
    Assert.Equal("forge", quest.TriggerWhere["station"]);
    Assert.Equal("2", quest.TriggerWhere["quality"]);
    Assert.Equal("true", quest.TriggerWhere["upgraded"]);
  }

  [Fact]
  public void ExistingQuestWithoutWhereKeepsNullFilterState() {
    TrackedQuest neck = Quest(QuestViewLoader.Parse(Fixture, out _), "neck_romancer");

    Assert.Null(neck.TriggerWhere);
  }

  [Theory]
  [InlineData("[]")]
  [InlineData("{ \"nested\": { \"x\": 1 } }")]
  [InlineData("{ \"list\": [1, 2] }")]
  [InlineData("{ \"nothing\": null }")]
  public void WhereRejectsShapesTheEvaluatorCouldOtherwiseSilentlyIgnore(string where) {
    string json =
        "{ \"schema_version\": 1, \"quests\": [{ \"quest_id\": \"q\", \"name\": \"Q\", "
        + "\"guild\": \"G\", \"trigger\": { \"event\": \"item_crafted\", \"where\": "
        + where + " } }] }";

    var error = Assert.Throws<System.InvalidOperationException>(
        () => QuestViewLoader.Parse(json, out _));

    Assert.Contains("trigger.where", error.Message);
  }

  [Fact]
  public void WhereRejectsCaseInsensitiveDuplicateKeys() {
    const string json = """
    { "schema_version": 1, "quests": [
      { "quest_id": "q", "name": "Q", "guild": "G", "trigger": {
        "event": "item_crafted", "where": { "station": "forge", "STATION": "kiln" }
      } }
    ] }
    """;

    var error = Assert.Throws<System.InvalidOperationException>(
        () => QuestViewLoader.Parse(json, out _));

    Assert.Contains("repeats field", error.Message);
  }
}
