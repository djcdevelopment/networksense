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
  /// A newcomer's first launch has to prove both the old kill shape and the broader schema-1 hit
  /// alias. Asserting through the real parser/evaluator turns red if either contract moves.</summary>
  [Fact]
  public void TheSeedShowsTwoArmedQuestShapes() {
    LabQuestSet set = Seeded();

    Assert.Empty(set.Errors);
    Assert.Equal(2, set.Quests.Count);
    Assert.Equal(2, set.ArmedCount);

    LabQuest armed = set.Quests.Single(q => q.QuestId == "first_blood");
    LabQuest wood = set.Quests.Single(q => q.QuestId == "punchwood");

    Assert.True(armed.IsArmed);
    Assert.True(wood.IsArmed);
    Assert.Equal("hit", wood.Quest.TriggerEvent);
  }

  /// <summary>The seed's armed quest must target something the practice gallery actually puts in
  /// front of you.
  ///
  /// This is the test that would have caught the mistake it exists for. The seed originally
  /// targeted a Neck — lifted from the test fixture because it was schema-valid — so a creator's
  /// first act after raising an eight-monument practice ground was to walk away from it and go
  /// find a shoreline. Building the gallery is what buys back that time; a seed that sends people
  /// hunting spends it again.
  ///
  /// Asserted against the gallery plan rather than a hardcoded name, so moving or renaming the
  /// combat station moves this with it instead of quietly making the seed wrong again.</summary>
  [Fact]
  public void TheArmedSeedQuestTargetsACreatureTheGalleryStandsUpForYou() {
    string combatStation = LabGalleryPlan.Monuments
        .Single(m => m.Category == LabCategory.Combat).Station.Prefab;

    TrackedQuest armed = Seeded().Quests.Single(q => q.QuestId == "first_blood").Quest;

    Assert.Contains(armed.TriggerTarget.ToLowerInvariant(), combatStation.ToLowerInvariant());
  }

  /// <summary>No filters on the armed quest, so the very first kill fires it. A creator who has
  /// to satisfy a weapon_skill they did not notice learns "this thing is broken", not "this is
  /// how triggers work" — the requirements text invites adding one as the next edit instead.</summary>
  [Fact]
  public void TheArmedSeedQuestFiresOnAnyKillOfItsTarget() {
    TrackedQuest armed = Seeded().Quests.Single(q => q.QuestId == "first_blood").Quest;

    Assert.True(string.IsNullOrWhiteSpace(armed.TriggerWeaponSkill));
    Assert.False(armed.TriggerProjectile);
  }

  [Fact]
  public void PunchwoodRetainsThePublishedHitVerbAndHarvestTarget() {
    TrackedQuest wood = Seeded().Quests.Single(q => q.QuestId == "punchwood").Quest;

    Assert.Equal("hit", wood.TriggerEvent);
    Assert.Equal("tree_or_bush", wood.TriggerTarget);
    Assert.Equal("Unarmed", wood.TriggerWeaponSkill);
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
