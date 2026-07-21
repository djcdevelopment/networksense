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
}
