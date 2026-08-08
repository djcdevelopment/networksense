using ComfyQuestLab;
using Xunit;

namespace ComfyNetworkSense.Tests;

public class LabActionDedupeTests {
  [Fact]
  public void LocalAndRpcWitnessesShareOneActionKey() {
    var dedupe = new LabActionDedupe();

    string local = dedupe.Key(
        "character-damage", "victim-42", "Swords", "Character.Damage(HitData)", 10.0);
    string rpc = dedupe.Key(
        "character-damage", "victim-42", "Swords", "Character.RPC_Damage(long, HitData)", 10.1);

    Assert.Equal(local, rpc);
  }

  [Fact]
  public void RepeatingTheSameWitnessStartsARealNewAction() {
    var dedupe = new LabActionDedupe();

    string first = dedupe.Key("resource-damage", "tree-7", "Axes", "TreeBase.Damage(HitData)", 1.0);
    string second = dedupe.Key("resource-damage", "tree-7", "Axes", "TreeBase.Damage(HitData)", 1.1);

    Assert.NotEqual(first, second);
  }

  [Fact]
  public void SecondActionStillPairsWithItsRpcWitness() {
    var dedupe = new LabActionDedupe();
    dedupe.Key("resource-damage", "tree-7", "Axes", "TreeBase.Damage(HitData)", 1.0);
    dedupe.Key("resource-damage", "tree-7", "Axes", "TreeBase.RPC_Damage(long, HitData)", 1.1);

    string secondLocal = dedupe.Key(
        "resource-damage", "tree-7", "Axes", "TreeBase.Damage(HitData)", 1.2);
    string secondRpc = dedupe.Key(
        "resource-damage", "tree-7", "Axes", "TreeBase.RPC_Damage(long, HitData)", 1.3);

    Assert.Equal(secondLocal, secondRpc);
  }

  [Fact]
  public void SameShapeAfterTheWindowIsIndependent() {
    var dedupe = new LabActionDedupe(correlationSeconds: 0.5);

    string first = dedupe.Key("piece-repaired", "piece-1", "repair", "Player.Repair", 2.0);
    string late = dedupe.Key("piece-repaired", "piece-1", "repair", "WearNTear.RPC_Repair", 2.6);

    Assert.NotEqual(first, late);
  }

  [Fact]
  public void OverloadWitnessesCanJoinWithoutTreatingTheirSharedRoleAsTheIdentity() {
    var dedupe = new LabActionDedupe();

    string enumOverload = dedupe.Key(
        "global-key-set", "world", "defeated_eikthyr", "SetGlobalKey(GlobalKeys)", 3.0);
    string stringOverload = dedupe.Key(
        "global-key-set", "world", "defeated_eikthyr", "SetGlobalKey(string)", 3.1);

    Assert.Equal(enumOverload, stringOverload);
  }

  [Theory]
  [InlineData("core", "core", true)]
  [InlineData("core", "extended", false)]
  [InlineData("extended", "core", true)]
  [InlineData("extended", "extended", true)]
  [InlineData("diagnostic", "diagnostic", true)]
  [InlineData("diagnostic", "extended", true)]
  [InlineData("diagnostic", "disabled", false)]
  [InlineData("nonsense", "diagnostic", false)]
  public void RuntimeProfilesFailClosedAtTheirDeclaredBoundary(
      string configured, string required, bool expected) {
    Assert.Equal(expected, LabRuntimeProfile.Allows(configured, required));
  }
}
