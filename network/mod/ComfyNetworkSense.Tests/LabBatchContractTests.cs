using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using ComfyQuestLab;
using Xunit;

namespace ComfyNetworkSense.Tests;

public class LabBatchContractTests {
  [Fact]
  public void AllSchoolsSuiteHasOneBindableExamplePerSchool() {
    LabBatchSuite suite = LabBatchContract.FindSuite("all-schools");

    Assert.NotNull(suite);
    Assert.Equal("live-gameplay", suite.EvidenceKind);
    Assert.Equal(LabCategory.All.OrderBy(x => x), suite.Expectations.Select(x => x.School).OrderBy(x => x));
    Assert.Equal(8, suite.Expectations.Select(x => x.EventName).Distinct().Count());

    LabQuestSet parsed = LabQuestSet.Build(
        new[] { new KeyValuePair<string, string>("batch.json", LabBatchContract.BuildQuestView(suite)) });
    Assert.Empty(parsed.Errors);
    Assert.Equal(8, parsed.Quests.Count);
    Assert.Equal(8, parsed.ArmedCount);
  }

  [Fact]
  public void FreshCharacterInstructionsPointAtEveryLocalCourseSupply() {
    LabBatchSuite suite = LabBatchContract.FindSuite("all-schools");
    Assert.Contains(suite.Expectations, x => x.EventName == "kill"
        && x.Instruction.Contains("bow and arrows at the combat spoke mouth", StringComparison.Ordinal));
    Assert.Contains(suite.Expectations, x => x.EventName == "resource_damaged"
        && x.Instruction.Contains("bronze axe beside the arrival portal", StringComparison.Ordinal));
    Assert.Contains(suite.Expectations, x => x.EventName == "piece_placed"
        && x.Instruction.Contains("Hammer and Wood in front of the building bench", StringComparison.Ordinal));
    Assert.Contains(suite.Expectations, x => x.EventName == "station_fuel_added"
        && x.Instruction.Contains("Coal directly in front of the crafting smelter", StringComparison.Ordinal));
    Assert.Contains(suite.Expectations, x => x.EventName == "sign_written"
        && x.Instruction.Contains("hub sign that says sign here", StringComparison.Ordinal));
  }

  [Fact]
  public void CreatorEventContractExercisesEverySafeCanonicalEvent() {
    LabBatchSession run = LabBatchContract.RunCreatorEventContract(
        "contract-test", "2026-08-08T12:00:00.0000000+00:00");

    Assert.Equal(34, run.Suite.Expectations.Length);
    Assert.Equal(34, run.WitnessedCount);
    Assert.Equal(34, run.CompletedQuestCount);
    Assert.Equal("pass", run.Verdict);
    Assert.Equal("complete", run.State);
    Assert.Equal(0, run.DoubleCompletionCount);
  }

  [Fact]
  public void ScenarioCatalogMirrorsEveryAuthoringEventAndSchool() {
    Assert.Equal(QuestEventCatalog.Count, LabScenarioCatalog.All.Count);
    Assert.Equal(34, LabScenarioCatalog.All.Select(x => x.EventName).Distinct().Count());
    Assert.Equal(
        LabCategory.All.OrderBy(x => x),
        LabScenarioCatalog.All.Select(x => x.School).Distinct().OrderBy(x => x));

    foreach (LabScenarioDefinition scenario in LabScenarioCatalog.All) {
      Assert.Equal("scenario-" + scenario.EventName, scenario.Id);
      Assert.Same(scenario.Suite, LabBatchContract.FindSuite(scenario.Id));
      Assert.Equal("synthetic-scenario", scenario.Suite.EvidenceKind);
      Assert.Single(scenario.Suite.Expectations);
      Assert.False(string.IsNullOrWhiteSpace(scenario.LiveAction));
      Assert.True(QuestEventCatalog.TryGet(
          scenario.EventName, out QuestEventCatalog.Definition metadata));
      Assert.Equal(metadata.Category, scenario.School);
      Assert.Equal(metadata.Profile, scenario.Profile);
      Assert.Equal(metadata.TargetKind, scenario.TargetKind);
      Assert.Equal(metadata.ExampleTarget, scenario.ExampleTarget);
    }
  }

  [Fact]
  public void EveryScenarioDraftAndRunUsesTheSharedLoaderAndEvaluator() {
    foreach (LabScenarioDefinition scenario in LabScenarioCatalog.All) {
      string questView = scenario.BuildQuestView();
      List<TrackedQuest> parsed = QuestViewLoader.Parse(questView, out string _);
      TrackedQuest quest = Assert.Single(parsed);
      Assert.Equal(scenario.EventName, quest.TriggerEvent);

      LabBatchSession run = LabScenarioCatalog.Run(scenario, "test-" + scenario.Id, "now");
      Assert.Equal("complete", run.State);
      Assert.Equal("pass", run.Verdict);
      Assert.Equal(1, run.WitnessedCount);
      Assert.Equal(1, run.CompletedQuestCount);
      Assert.Equal(0, run.DoubleCompletionCount);
      Assert.Equal("synthetic-scenario", Assert.Single(run.Witnesses).Source);
    }
  }

  [Fact]
  public void ScenarioPreparationManifestIsDeterministicAndHonest() {
    LabScenarioDefinition scenario = LabScenarioCatalog.Find("scenario-station_fuel_added");
    string questView = scenario.BuildQuestView();
    string first = scenario.BuildManifest(questView);
    string second = scenario.BuildManifest(questView);

    Assert.Equal(first, second);
    using JsonDocument parsed = JsonDocument.Parse(first);
    JsonElement root = parsed.RootElement;
    Assert.Equal(LabScenarioCatalog.ManifestSchema, root.GetProperty("schema").GetString());
    Assert.Equal("synthetic-scenario", root.GetProperty("evidence_kind").GetString());
    Assert.Contains("not a live gameplay witness", root.GetProperty("claim").GetString());
    Assert.Equal("Coal", root.GetProperty("example").GetProperty("target").GetString());
    Assert.Equal(
        "smelter",
        root.GetProperty("example").GetProperty("where").GetProperty("station").GetString());
    Assert.Matches("^[0-9a-f]{64}$", root.GetProperty("quest_view_sha256").GetString());
  }

  [Fact]
  public void AlternativeWitnessesCollapseToOneCanonicalAction() {
    LabBatchSuite suite = LabBatchContract.FindSuite("all-schools");
    var run = new LabBatchSession(suite, "dedupe-test", "start");

    run.Observe("combat", "kill", "Character.OnDeath()", "Greyling", "action-1",
        true, true, "gameplay", "one");
    run.Observe("combat", "kill", "Character.RPC_OnDeath()", "Greyling", "action-1",
        false, true, "gameplay", "two");
    run.Complete("questlab_suite_combat", "kill", "action-1", "gameplay", "three");

    Assert.Equal(2, run.RawWitnessCount);
    Assert.Equal(1, run.CanonicalActionCount);
    Assert.Equal(1, run.CoalescedWitnessCount);
    Assert.Single(run.Witnesses);
    Assert.Equal(2, run.Witnesses[0].RawWitnessCount);
  }

  [Fact]
  public void ASecondCompletionForTheSameActionFailsClosed() {
    LabBatchSuite suite = LabBatchContract.FindSuite("all-schools");
    var run = new LabBatchSession(suite, "double-test", "start");
    run.Observe("combat", "kill", "Character.OnDeath()", "Greyling", "action-1",
        true, true, "gameplay", "one");

    run.Complete("questlab_suite_combat", "kill", "action-1", "gameplay", "two");
    run.Complete("questlab_suite_combat", "kill", "action-1", "gameplay", "three");

    Assert.Equal(1, run.DoubleCompletionCount);
    Assert.Equal("fail_double_completion", run.Verdict);
    Assert.Equal("failed", run.State);
  }

  [Fact]
  public void APassReceiptCanStillTurnRedWhenALateAlternativeDoubleCompletes() {
    LabBatchSession run = LabBatchContract.RunCreatorEventContract("late-double", "start");
    LabBatchExpectation expected = run.Suite.Expectations[0];

    run.Complete(expected.QuestId, expected.EventName, "contract:" + expected.EventName,
        "synthetic-contract", "late");

    Assert.Equal("failed", run.State);
    Assert.Equal("fail_double_completion", run.Verdict);
  }

  [Fact]
  public void RemotePolicyIsAClosedAllowlistWithNoConsoleOrKeystrokeLane() {
    Assert.Equal(11, LabBatchRequestPolicy.Operations.Length);
    Assert.True(LabBatchRequestPolicy.Validate(
        "prepare", "all-schools", null, null, null, out string _));
    Assert.True(LabBatchRequestPolicy.Validate(
        "run", "scenario-sign_written", null, null, null, out _));
    Assert.True(LabBatchRequestPolicy.Validate(
        "gallery_compare", null, "marble-wide", "marble-grand", null, out _));
    Assert.True(LabBatchRequestPolicy.Validate(
        "gallery_clear", null, null, null, "compare-20260808T120000Z-01", out _));
    Assert.True(LabBatchRequestPolicy.Validate(
        "gallery_evidence", null, null, null, "marble-grand", out _));

    Assert.False(LabBatchRequestPolicy.Validate(
        "console", null, null, null, null, out string consoleError));
    Assert.Equal("operation_not_allowlisted", consoleError);
    Assert.False(LabBatchRequestPolicy.Validate(
        "keypress", null, null, null, null, out string keyError));
    Assert.Equal("operation_not_allowlisted", keyError);
    Assert.False(LabBatchRequestPolicy.Validate(
        "run", "all-schools", "arbitrary-extra", null, null, out string extraError));
    Assert.Equal("request_argument_not_allowed", extraError);
    Assert.False(LabBatchRequestPolicy.Validate(
        "run", "scenario-not_real", null, null, null, out string scenarioError));
    Assert.Equal("suite_not_allowlisted", scenarioError);
    Assert.False(LabBatchRequestPolicy.Validate(
        "gallery_build", null, "not-a-profile", null, null, out string profileError));
    Assert.Equal("gallery_profile_not_allowlisted", profileError);
    Assert.False(LabBatchRequestPolicy.Validate(
        "gallery_evidence", null, null, null, "../../outside", out string selectorError));
    Assert.Equal("gallery_selector_invalid", selectorError);
    Assert.False(LabBatchRequestPolicy.Validate(
        "gallery_evidence", null, "unexpected-profile-field", null, "all",
        out string evidenceExtraError));
    Assert.Equal("request_argument_not_allowed", evidenceExtraError);
  }

  [Fact]
  public void ReceiptIsMachineReadableAndKeepsEvidenceKindExplicit() {
    LabBatchSession run = LabBatchContract.RunCreatorEventContract("json-test", "start");
    string json = run.ToJson(new LabBatchReceiptContext {
      Machine = "test-machine",
      PluginVersion = "0.1.0",
      ReleaseId = "dev",
      RuntimeProfile = "synthetic contract",
      GeneratedUtc = "finish",
    });

    using JsonDocument parsed = JsonDocument.Parse(json);
    JsonElement root = parsed.RootElement;
    Assert.Equal(LabBatchContract.ReceiptSchema, root.GetProperty("schema").GetString());
    Assert.Equal("synthetic-contract", root.GetProperty("evidence_kind").GetString());
    Assert.Equal("pass", root.GetProperty("verdict").GetString());
    Assert.Equal(34, root.GetProperty("expectations").GetArrayLength());
    Assert.Equal(34, root.GetProperty("witnesses").GetArrayLength());
  }
}
