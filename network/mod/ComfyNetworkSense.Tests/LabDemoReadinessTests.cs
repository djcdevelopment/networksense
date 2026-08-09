using ComfyQuestLab;
using Xunit;

namespace ComfyNetworkSense.Tests;

public sealed class LabDemoReadinessTests {
  static LabReadinessInput ReadyInput() => new LabReadinessInput {
    HooksApplied = 90,
    HooksExpected = 90,
    CatalogEventCount = 34,
    ManifestEventCount = 34,
    QuestsLoaded = 10,
    QuestsArmed = 10,
    ArmedSchoolCount = 8,
    QuestsEnabled = true,
    InputOwned = true,
    RuntimeProfile = "extended",
    ReleaseId = "test-release",
  };

  [Fact]
  public void Healthy_runtime_is_ready_without_claiming_a_live_witness() {
    LabReadinessSnapshot result = LabDemoReadiness.Assess(ReadyInput());

    Assert.Equal(LabReadinessSnapshot.ReadyForLap, result.Status);
    Assert.StartsWith("[READY]", result.Summary);
    Assert.Contains("live: not run in this process", result.Summary);
    Assert.Empty(result.Issues);
  }

  [Fact]
  public void Exact_passing_suite_promotes_the_summary_to_proved() {
    LabReadinessInput input = ReadyInput();
    input.HasLiveSuite = true;
    input.LiveSuiteRequired = 8;
    input.LiveSuiteWitnessed = 8;
    input.LiveSuiteCompleted = 8;
    input.LiveSuiteVerdict = "pass";

    LabReadinessSnapshot result = LabDemoReadiness.Assess(input);

    Assert.Equal(LabReadinessSnapshot.LiveLapProved, result.Status);
    Assert.StartsWith("[PROVED]", result.Summary);
  }

  [Fact]
  public void Drift_errors_missing_schools_and_lost_input_are_named_not_colored() {
    LabReadinessInput input = ReadyInput();
    input.HooksApplied = 88;
    input.ManifestEventCount = 33;
    input.QuestLoadErrors = 2;
    input.ArmedSchoolCount = 6;
    input.InputOwned = false;

    LabReadinessSnapshot result = LabDemoReadiness.Assess(input);

    Assert.Equal(LabReadinessSnapshot.NeedsAttention, result.Status);
    Assert.StartsWith("[CHECK]", result.Summary);
    Assert.Contains("2 integration hook(s) unavailable", result.Issues);
    Assert.Contains("creator event drift: evaluator 34 / manifest 33", result.Issues);
    Assert.Contains("2 quest file load error(s)", result.Issues);
    Assert.Contains("bindable example quests cover 6/8 schools", result.Issues);
    Assert.Contains("interactive panel input is not owned", result.Issues);
  }

  [Fact]
  public void School_count_ignores_duplicates_case_and_unknown_values() {
    int count = LabDemoReadiness.DistinctKnownSchools(new[] {
      "combat", "COMBAT", "harvest", "not-a-school", null,
    });

    Assert.Equal(2, count);
  }
}
