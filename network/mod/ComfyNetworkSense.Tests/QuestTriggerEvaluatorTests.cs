using System;
using System.Collections.Generic;
using System.Linq;
using ComfyNetworkSense;
using Xunit;

namespace ComfyNetworkSense.Tests;

public class QuestTriggerEvaluatorTests {
  static TrackedQuest KillQuest(
      string id = "q", string target = "Neck", string skill = null, bool projectile = false,
      bool autoChecked = false, string venue = "in_game", List<string> shots = null) =>
      new() {
        QuestId = id,
        Name = id + "-name",
        Guild = "Test",
        TriggerEvent = "kill",
        TriggerTarget = target,
        TriggerWeaponSkill = skill,
        TriggerProjectile = projectile,
        TriggerShots = shots,
        AutoChecked = autoChecked,
        Venue = venue,
      };

  static List<TrackedQuest> List(params TrackedQuest[] quests) => quests.ToList();

  static TrackedQuest EventQuest(
      string eventName,
      string id = "event-q",
      string target = null,
      Dictionary<string, string> where = null) =>
      new() {
        QuestId = id,
        Name = id + "-name",
        Guild = "Test",
        TriggerEvent = eventName,
        TriggerTarget = target,
        TriggerWhere = where,
        Venue = "in_game",
      };

  [Fact]
  public void MatchingCreatureKill_Completes() {
    var evaluator = new QuestTriggerEvaluator();

    var completions = evaluator.OnCreatureKilled(List(KillQuest(target: "Neck")), "Neck", "Unarmed", false, 10.0);

    Assert.Single(completions);
    Assert.Equal("q", completions[0].QuestId);
  }

  [Fact]
  public void CloneSuffixAndCase_StillMatch() {
    var evaluator = new QuestTriggerEvaluator();

    var completions = evaluator.OnCreatureKilled(List(KillQuest(target: "neck")), "Neck(Clone)", "Unarmed", false, 10.0);

    Assert.Single(completions);
  }

  [Fact]
  public void WrongCreature_DoesNotMatch() {
    var evaluator = new QuestTriggerEvaluator();

    var completions = evaluator.OnCreatureKilled(List(KillQuest(target: "Neck")), "Boar", "Unarmed", false, 10.0);

    Assert.Empty(completions);
  }

  [Fact]
  public void WeaponSkillFilter_Enforced() {
    var evaluator = new QuestTriggerEvaluator();
    var quests = List(KillQuest(skill: "Unarmed"));

    Assert.Empty(evaluator.OnCreatureKilled(quests, "Neck", "Swords", false, 10.0));
    Assert.Single(evaluator.OnCreatureKilled(quests, "Neck", "Unarmed", false, 20.0));
  }

  [Fact]
  public void ProjectileFilter_RequiresRangedHit() {
    var evaluator = new QuestTriggerEvaluator();
    var quests = List(KillQuest(id: "airdrop", target: "Deathsquito", projectile: true));

    Assert.Empty(evaluator.OnCreatureKilled(quests, "Deathsquito", "Spears", false, 10.0));  // melee
    Assert.Single(evaluator.OnCreatureKilled(quests, "Deathsquito", "Spears", true, 20.0));  // thrown
  }

  [Fact]
  public void Cooldown_SuppressesRefireThenReArms() {
    var evaluator = new QuestTriggerEvaluator(cooldownSeconds: 60.0);
    var quests = List(KillQuest());

    Assert.Single(evaluator.OnCreatureKilled(quests, "Neck", null, false, 10.0));
    Assert.Empty(evaluator.OnCreatureKilled(quests, "Neck", null, false, 40.0));   // within cooldown
    Assert.Single(evaluator.OnCreatureKilled(quests, "Neck", null, false, 75.0));  // past cooldown
  }

  [Fact]
  public void CooldownRemaining_CountsDown() {
    var evaluator = new QuestTriggerEvaluator(cooldownSeconds: 60.0);
    evaluator.OnCreatureKilled(List(KillQuest()), "Neck", null, false, 10.0);

    Assert.Equal(50.0, evaluator.CooldownRemaining("q", 20.0), 3);
    Assert.Equal(0.0, evaluator.CooldownRemaining("q", 80.0), 3);
    Assert.Equal(0.0, evaluator.CooldownRemaining("never-fired", 20.0), 3);
  }

  [Fact]
  public void IrlAndAutoChecked_AreNotCapturable() {
    var evaluator = new QuestTriggerEvaluator();

    Assert.Empty(evaluator.OnCreatureKilled(List(KillQuest(venue: "irl")), "Neck", null, false, 10.0));
    Assert.Empty(evaluator.OnCreatureKilled(List(KillQuest(autoChecked: true)), "Neck", null, false, 10.0));
  }

  [Fact]
  public void NonKillTrigger_IsIgnored() {
    var evaluator = new QuestTriggerEvaluator();
    var hitQuest = KillQuest(target: "tree_or_bush");
    hitQuest.TriggerEvent = "hit";  // hit-on-world-object quests are a deferred increment

    Assert.Empty(evaluator.OnCreatureKilled(List(hitQuest), "tree_or_bush", null, false, 10.0));
  }

  [Fact]
  public void AnyOrEmptyTarget_IsAWildcard() {
    var evaluator = new QuestTriggerEvaluator();

    Assert.Single(evaluator.OnCreatureKilled(List(KillQuest(id: "a", target: "any")), "Boar", null, false, 10.0));
    Assert.Single(evaluator.OnCreatureKilled(List(KillQuest(id: "b", target: null)), "Troll", null, false, 10.0));
  }

  [Fact]
  public void OneBlowKillAndTwoShotQuest_BothCompleteOnce() {
    // shots is informational now (screenshots are gone) — a two-shot quest completes on the kill
    // exactly like a single-blow quest. No behavioural difference; both fire once.
    var evaluator = new QuestTriggerEvaluator();
    var twoShot = KillQuest(id: "neck_romancer", shots: new List<string> { "on_first_hit", "on_death" });

    Assert.Single(evaluator.OnCreatureKilled(List(twoShot), "Neck", null, false, 10.0));
  }

  [Fact]
  public void MultipleMatchingQuests_AllComplete() {
    var evaluator = new QuestTriggerEvaluator();
    var quests = List(KillQuest(id: "a", target: "Neck"), KillQuest(id: "b", target: "any"));

    var completions = evaluator.OnCreatureKilled(quests, "Neck", null, false, 10.0);

    Assert.Equal(new[] { "a", "b" }, completions.Select(c => c.QuestId).ToArray());
  }

  [Fact]
  public void EmptyOrNullQuestList_YieldsNothing() {
    var evaluator = new QuestTriggerEvaluator();

    Assert.Empty(evaluator.OnCreatureKilled(new List<TrackedQuest>(), "Neck", null, false, 10.0));
    Assert.Empty(evaluator.OnCreatureKilled(null, "Neck", null, false, 10.0));
  }

  [Fact]
  public void EveryCreatorSafeCatalogEventUsesTheSameBindableEvaluatorPath() {
    string[] events = QuestEventCatalog.AllEventNames.OrderBy(name => name).ToArray();

    Assert.Equal(34, QuestEventCatalog.Count);
    Assert.Equal(34, events.Length);

    for (int i = 0; i < events.Length; i++) {
      string eventName = events[i];
      var evaluator = new QuestTriggerEvaluator(cooldownSeconds: 0.0);

      QuestCompletion completion = Assert.Single(
          evaluator.OnEvent(
              List(EventQuest(eventName, id: "q-" + i)),
              new QuestEvent(eventName),
              i + 1.0));

      Assert.Equal(eventName, completion.EventName);
    }
  }

  [Fact]
  public void EventNamesAreCaseInsensitiveButDiagnosticEventsCannotBind() {
    var evaluator = new QuestTriggerEvaluator(cooldownSeconds: 0.0);

    Assert.Single(evaluator.OnEvent(
        List(EventQuest("ITEM_CRAFTED")), new QuestEvent("item_crafted"), 1.0));
    Assert.Empty(evaluator.OnEvent(
        List(EventQuest("health_changed", id: "unsafe")),
        new QuestEvent("health_changed"),
        2.0));
    Assert.False(QuestEventCatalog.IsBindable("health_changed"));
  }

  [Fact]
  public void PublishedHitVerbRemainsABroadCompatibilityAlias() {
    var evaluator = new QuestTriggerEvaluator(cooldownSeconds: 0.0);
    var legacy = EventQuest("hit", target: "tree_or_bush");

    Assert.True(QuestEventCatalog.IsBindable("hit"));
    Assert.Equal(1, QuestEventCatalog.AliasCount);
    Assert.Single(evaluator.OnEvent(
        List(legacy), new QuestEvent("resource_damaged", "Beech1 (tree)"), 1.0));

    legacy.TriggerTarget = "Troll";
    Assert.Single(evaluator.OnEvent(
        List(legacy), new QuestEvent("damage_dealt", "Troll"), 2.0));
    Assert.Empty(evaluator.OnEvent(
        List(legacy), new QuestEvent("kill", "Troll"), 3.0));
  }

  [Fact]
  public void GenericTargetAndWhereFieldsNarrowAnEvent() {
    var evaluator = new QuestTriggerEvaluator(cooldownSeconds: 0.0);
    var quest = EventQuest(
        "item_crafted",
        target: "iron",
        where: new Dictionary<string, string> {
          ["station"] = "forge",
          ["quality"] = "2",
        });
    var fields = new Dictionary<string, string> {
      ["station"] = "Forge",
      ["quality"] = "2",
    };

    Assert.Single(evaluator.OnEvent(
        List(quest), new QuestEvent("item_crafted", "SwordIron", fields: fields), 1.0));

    fields["quality"] = "1";
    Assert.Empty(evaluator.OnEvent(
        List(quest), new QuestEvent("item_crafted", "SwordIron", fields: fields), 2.0));

    Assert.Empty(evaluator.OnEvent(
        List(quest), new QuestEvent("item_crafted", "Club", fields: new Dictionary<string, string> {
          ["station"] = "forge", ["quality"] = "2",
        }), 3.0));
  }

  [Fact]
  public void LocalAndRpcWitnessesCannotDoubleCompleteWithCooldownZero() {
    var evaluator = new QuestTriggerEvaluator(cooldownSeconds: 0.0, dedupeWindowSeconds: 1.0);
    var quest = EventQuest("damage_dealt");

    Assert.Single(evaluator.OnEvent(
        List(quest), new QuestEvent("damage_dealt", "Troll", dedupeKey: "victim-42/action-7"), 10.0));
    Assert.Empty(evaluator.OnEvent(
        List(quest), new QuestEvent("damage_dealt", "Troll", dedupeKey: "victim-42/action-7"), 10.1));

    Assert.Single(evaluator.OnEvent(
        List(quest), new QuestEvent("damage_dealt", "Troll", dedupeKey: "victim-42/action-8"), 10.2));
    Assert.Single(evaluator.OnEvent(
        List(quest), new QuestEvent("damage_dealt", "Troll", dedupeKey: "victim-42/action-7"), 11.1));
  }

  [Fact]
  public void MissingDedupeKeyDoesNotInventIdentityForIndependentActions() {
    var evaluator = new QuestTriggerEvaluator(cooldownSeconds: 0.0);
    var quest = EventQuest("resource_picked");

    Assert.Single(evaluator.OnEvent(List(quest), new QuestEvent("resource_picked", "Raspberry"), 1.0));
    Assert.Single(evaluator.OnEvent(List(quest), new QuestEvent("resource_picked", "Raspberry"), 1.1));
  }

  [Fact]
  public void QuestEventCopiesFieldsAndExposesUniversalFieldsToWhere() {
    var mutable = new Dictionary<string, string> { ["station"] = "forge" };
    var gameplayEvent = new QuestEvent(
        "item_crafted", "SwordIron", "Swords", true, fields: mutable);
    mutable["station"] = "workbench";

    Assert.True(gameplayEvent.TryGetField("station", out string station));
    Assert.Equal("forge", station);
    Assert.True(gameplayEvent.TryGetField("target", out string target));
    Assert.Equal("SwordIron", target);
    Assert.True(gameplayEvent.TryGetField("projectile", out string projectile));
    Assert.Equal("true", projectile);
  }
}
