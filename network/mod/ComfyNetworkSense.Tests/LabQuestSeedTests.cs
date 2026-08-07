using System.Collections.Generic;
using System.Linq;
using ComfyNetworkSense;
using ComfyQuestLab;
using Xunit;

namespace ComfyNetworkSense.Tests;

/// <summary>
/// The starter file and the advisories — the two surfaces a creator meets before they have any
/// reason to trust the lab.
/// </summary>
public class LabQuestSeedTests {
  static LabQuestSet Seeded() =>
      LabQuestSet.Build(
          new[] { new KeyValuePair<string, string>(LabQuestSeed.FileName, LabQuestSeed.Text) });

  /// <summary>The load-bearing test of the whole seed.
  ///
  /// A newcomer's first launch has to show one quest that works and one that silently cannot,
  /// because the difference between them is the lesson. Asserting it through the real parser and
  /// the real evaluator means this goes red the day either contract moves underneath the seed —
  /// which is exactly when the starter file would otherwise start teaching the wrong thing.</summary>
  [Fact]
  public void TheSeedShowsOneArmedQuestAndOneThatSilentlyCannotFire() {
    LabQuestSet set = Seeded();

    Assert.Empty(set.Errors);
    Assert.Equal(2, set.Quests.Count);
    Assert.Equal(1, set.ArmedCount);

    LabQuest neck = set.Quests.Single(q => q.QuestId == "neck_romancer");
    LabQuest wood = set.Quests.Single(q => q.QuestId == "punchwood");

    Assert.True(neck.IsArmed);
    Assert.Equal(LabArmed.VerbNotKill, wood.Armed);
    Assert.Contains("'hit'", wood.ArmedLine());
  }

  [Fact]
  public void TheSeedIsAWholeQuestViewSoItCanBeCopiedStraightToTheShippingMod() {
    List<TrackedQuest> quests = QuestViewLoader.Parse(LabQuestSeed.Text, out string player);

    Assert.Equal("you", player);
    Assert.Equal(2, quests.Count);
    Assert.All(quests, q => Assert.False(string.IsNullOrWhiteSpace(q.Guild)));
  }

  // ---- advisories --------------------------------------------------------------------------

  static TrackedQuest Kill(string target = "Neck", string skill = null, bool projectile = false) =>
      new TrackedQuest {
        QuestId = "q", Name = "q", Guild = "g", Venue = "in_game",
        TriggerEvent = "kill", TriggerTarget = target,
        TriggerWeaponSkill = skill, TriggerProjectile = projectile,
      };

  static readonly string[] Skills = { "Swords", "Axes", "Bows", "Unarmed", "Spears" };

  [Fact]
  public void NothingIsSaidAboutAQuestWithNothingWrongWithIt() {
    var facts = new LabWorldFacts { KnownSkills = Skills, PrefabKnown = _ => true };

    Assert.Empty(LabQuestAdvisor.Advise(Kill(skill: "Swords"), facts));
  }

  [Fact]
  public void AMistypedSkillIsCaughtAndTheNearestRealOneSuggested() {
    var facts = new LabWorldFacts { KnownSkills = Skills, PrefabKnown = _ => true };

    string note = Assert.Single(LabQuestAdvisor.Advise(Kill(skill: "Sword"), facts));

    Assert.Contains("'Sword'", note);
    Assert.Contains("'Swords'", note);
  }

  [Fact]
  public void AnUnrelatedSkillIsFlaggedWithoutAWildSuggestion() {
    var facts = new LabWorldFacts { KnownSkills = Skills, PrefabKnown = _ => true };

    string note = Assert.Single(LabQuestAdvisor.Advise(Kill(skill: "Xylophone"), facts));

    Assert.DoesNotContain("did you mean", note);
  }

  [Fact]
  public void ATargetThatIsInNoCatalogPointsAtTheSearchCommand() {
    var facts = new LabWorldFacts { KnownSkills = Skills, PrefabKnown = _ => false };

    Assert.Contains("questlab_prefabs", Assert.Single(LabQuestAdvisor.Advise(Kill("Nek"), facts)));
  }

  /// <summary>The advisory that exists because the lab's own console used to disagree with the
  /// matcher. A creator reads "Greydwarf_Elite" off questlab_prefabs, types it, and the matcher
  /// is comparing against "$enemy_greydwarfbrute" — no shared substring, no error, never fires.</summary>
  [Fact]
  public void APrefabNameTheMatcherWillNeverSeeIsCaughtAndTheRealStringGiven() {
    var facts = new LabWorldFacts {
      KnownSkills = Skills,
      PrefabKnown = _ => true,
      MatcherNameFor = name => name == "Greydwarf_Elite" ? "$enemy_greydwarfbrute" : null,
    };

    string note = Assert.Single(LabQuestAdvisor.Advise(Kill("Greydwarf_Elite"), facts));

    Assert.Contains("$enemy_greydwarfbrute", note);
  }

  [Fact]
  public void ATargetTheMatcherDoesSeeIsLeftAlone() {
    var facts = new LabWorldFacts {
      KnownSkills = Skills,
      PrefabKnown = _ => true,
      MatcherNameFor = name => name == "Neck" ? "$enemy_neck" : null,
    };

    Assert.Empty(LabQuestAdvisor.Advise(Kill("Neck"), facts));
  }

  [Fact]
  public void ProjectileWithAMeleeOnlySkillIsImpossibleAndSaidSo() {
    var facts = new LabWorldFacts { KnownSkills = Skills, PrefabKnown = _ => true };

    string note = Assert.Single(
        LabQuestAdvisor.Advise(Kill(skill: "Swords", projectile: true), facts));

    Assert.Contains("never be a ranged hit", note);
  }

  /// <summary>Spears are the reason the melee list is a list and not "anything that is not a bow":
  /// a thrown spear is a genuine ranged hit, and flagging it would be a false alarm.</summary>
  [Fact]
  public void ProjectileWithSpearsIsLeftAlone() {
    var facts = new LabWorldFacts { KnownSkills = Skills, PrefabKnown = _ => true };

    Assert.Empty(LabQuestAdvisor.Advise(Kill(skill: "Spears", projectile: true), facts));
  }

  [Fact]
  public void ShotsAreReportedAsCarryingNoBehaviour() {
    TrackedQuest quest = Kill();
    quest.TriggerShots = new List<string> { "on_first_hit", "on_death" };

    Assert.Contains("no behaviour", Assert.Single(LabQuestAdvisor.Advise(quest, null)));
  }

  /// <summary>The lab loads quests during Awake, long before ZNetScene exists. Checks that would
  /// have to guess must stay silent rather than report every target as unknown.</summary>
  [Fact]
  public void NothingIsGuessedWhenTheWorldIsNotLoadedYet() {
    Assert.Empty(LabQuestAdvisor.Advise(Kill("Neck", "Swords"), LabWorldFacts.None));
  }
}
