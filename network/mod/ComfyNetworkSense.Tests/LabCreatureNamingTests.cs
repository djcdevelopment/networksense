using System.IO;
using ComfyQuestLab;
using Xunit;

namespace ComfyNetworkSense.Tests;

/// <summary>
/// The two things a creator's first hour actually depends on: which of a creature's names a quest
/// can match on, and the starter file not eating their work.
///
/// Both used to be verifiable only by launching Valheim. Neither needs to be.
/// </summary>
public class LabCreatureNamingTests {
  // ---- the name a quest matches on --------------------------------------------------------

  /// <summary>The regression test for the bug this whole file exists because of.
  ///
  /// The console showed the GameObject name and promised the evaluator compared against exactly
  /// that. `Greydwarf_Elite` and `$enemy_greydwarfbrute` share no substring in either direction,
  /// so a quest targeting what the console displayed parsed cleanly, reported no problem, and
  /// could never fire. Nothing caught it because nothing could: the rule lived next to Unity types.</summary>
  [Fact]
  public void TheEliteGreydwarfIsTheCaseThatUsedToLieAndItIsCaughtNow() {
    string matcher = LabCreatureNaming.Normalize("$enemy_greydwarfbrute", "Greydwarf_Elite(Clone)");

    Assert.Equal("$enemy_greydwarfbrute", matcher);
    Assert.True(LabCreatureNaming.NamesDisagree(matcher, "Greydwarf_Elite"));
    Assert.Equal("$enemy_greydwarfbrute (prefab Greydwarf_Elite)",
        LabCreatureNaming.Display(matcher, "Greydwarf_Elite"));
  }

  /// <summary>Neck is the creature the starter quest targets, and it works — but by luck, because
  /// its token happens to contain its prefab name. Pinning it stops a future "simplification"
  /// from concluding the two names are interchangeable.</summary>
  [Fact]
  public void NeckWorksByLuckAndTheDisplayStaysQuietAboutIt() {
    string matcher = LabCreatureNaming.Normalize("$enemy_neck", "Neck(Clone)");

    Assert.False(LabCreatureNaming.NamesDisagree(matcher, "Neck"));
    Assert.Equal("$enemy_neck", LabCreatureNaming.Display(matcher, "Neck"));
  }

  [Fact]
  public void TheLocalizationTokenWinsOverTheGameObjectName() {
    Assert.Equal("$enemy_boar", LabCreatureNaming.Normalize("$enemy_boar", "Boar(Clone)"));
  }

  /// <summary>Mirrors the producer: fall back only when m_name is genuinely absent. Some prefabs
  /// carry no display name at all, and reporting "unknown" for those would be worse than the
  /// GameObject name.</summary>
  [Fact]
  public void AnEmptyDisplayNameFallsBackToTheGameObjectName() {
    Assert.Equal("Boar", LabCreatureNaming.Normalize(null, "Boar(Clone)"));
    Assert.Equal("Boar", LabCreatureNaming.Normalize("   ", "Boar(Clone)"));
  }

  [Fact]
  public void CloneIsStrippedAndNothingIsLeftDangling() {
    Assert.Equal("Greyling", LabCreatureNaming.Clean("Greyling(Clone)"));
    Assert.Equal("Greyling", LabCreatureNaming.Clean("Greyling (Clone)"));
    Assert.Equal("Greyling", LabCreatureNaming.Clean("Greyling"));
    Assert.Equal("unknown", LabCreatureNaming.Clean(null));
  }

  [Fact]
  public void DisplayDegradesRatherThanThrowingOnMissingNames() {
    Assert.Equal("unknown", LabCreatureNaming.Display(null, null));
    Assert.Equal("Boar", LabCreatureNaming.Display(null, "Boar"));
    Assert.Equal("$enemy_boar", LabCreatureNaming.Display("$enemy_boar", null));
  }

  // ---- the starter file ---------------------------------------------------------------------

  static string TempDir() {
    string path = Path.Combine(Path.GetTempPath(), "labseed-" + Path.GetRandomFileName());
    return path;
  }

  [Fact]
  public void SeedingCreatesTheDirectoryAndWritesAParseableStarter() {
    string dir = TempDir();
    try {
      string report = LabQuestSeed.EnsureSeeded(dir);

      Assert.NotNull(report);
      string written = Path.Combine(dir, LabQuestSeed.FileName);
      Assert.True(File.Exists(written));
      Assert.Equal(LabQuestSeed.Text, File.ReadAllText(written));
    } finally {
      if (Directory.Exists(dir)) {
        Directory.Delete(dir, true);
      }
    }
  }

  /// <summary>The guarantee that matters most, because getting it wrong destroys somebody's
  /// evening. lab_setup is a command people re-run — to rebuild the gallery, or just because they
  /// forgot they had — and it must never overwrite quests they wrote.</summary>
  [Fact]
  public void SeedingNeverOverwritesAFileThatIsAlreadyThere() {
    string dir = TempDir();
    try {
      Directory.CreateDirectory(dir);
      string mine = Path.Combine(dir, LabQuestSeed.FileName);
      File.WriteAllText(mine, "{ \"mine\": true }");

      string report = LabQuestSeed.EnsureSeeded(dir);

      Assert.Null(report);                                     // says nothing, does nothing
      Assert.Equal("{ \"mine\": true }", File.ReadAllText(mine));
    } finally {
      if (Directory.Exists(dir)) {
        Directory.Delete(dir, true);
      }
    }
  }

  /// <summary>Deliberately keyed on "any *.json", not on starter.json specifically: somebody who
  /// renamed their drafts should not get a starter file back, and somebody who deleted the starter
  /// on purpose should not have it reappear on the next lab_setup.</summary>
  [Fact]
  public void ARenamedDraftStillCountsAsWorkAndSuppressesTheSeed() {
    string dir = TempDir();
    try {
      Directory.CreateDirectory(dir);
      File.WriteAllText(Path.Combine(dir, "my-own-quests.json"), "{ }");

      Assert.Null(LabQuestSeed.EnsureSeeded(dir));
      Assert.False(File.Exists(Path.Combine(dir, LabQuestSeed.FileName)));
    } finally {
      if (Directory.Exists(dir)) {
        Directory.Delete(dir, true);
      }
    }
  }

  /// <summary>A read-only or otherwise unwritable config dir should cost the seed, not the
  /// gallery — lab_setup carries on either way. Reported, never thrown.</summary>
  [Fact]
  public void AnUnwritablePathIsReportedRatherThanThrown() {
    string report = LabQuestSeed.EnsureSeeded("\0:/nope");

    Assert.NotNull(report);
    Assert.Contains("could not write", report);
  }
}
