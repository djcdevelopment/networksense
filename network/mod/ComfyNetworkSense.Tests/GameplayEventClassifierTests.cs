using ComfyNetworkSense;
using Xunit;

namespace ComfyNetworkSense.Tests;

public class GameplayEventClassifierTests {
  [Fact]
  public void PlayerKillingBlow_EmitsKillingBlow() {
    var classifier = new GameplayEventClassifier();

    var kind = classifier.ClassifyCreatureDamage(
        creatureInstanceId: 1, attackerIsPlayer: true, creatureDied: true, nowSeconds: 10.0);

    Assert.Equal(GameplayEventKind.KillingBlow, kind);
  }

  [Fact]
  public void NonFatalHit_EmitsNothing() {
    var classifier = new GameplayEventClassifier();

    var kind = classifier.ClassifyCreatureDamage(
        creatureInstanceId: 1, attackerIsPlayer: true, creatureDied: false, nowSeconds: 10.0);

    Assert.Equal(GameplayEventKind.None, kind);
  }

  [Fact]
  public void EnvironmentKill_NotAttributedToPlayer_EmitsNothing() {
    var classifier = new GameplayEventClassifier();

    var kind = classifier.ClassifyCreatureDamage(
        creatureInstanceId: 1, attackerIsPlayer: false, creatureDied: true, nowSeconds: 10.0);

    Assert.Equal(GameplayEventKind.None, kind);
  }

  [Fact]
  public void DuplicateDeathWithinWindow_EmitsOnce() {
    var classifier = new GameplayEventClassifier(dedupSeconds: 2.0);

    var first = classifier.ClassifyCreatureDamage(1, attackerIsPlayer: true, creatureDied: true, nowSeconds: 10.0);
    var second = classifier.ClassifyCreatureDamage(1, attackerIsPlayer: true, creatureDied: true, nowSeconds: 10.5);

    Assert.Equal(GameplayEventKind.KillingBlow, first);
    Assert.Equal(GameplayEventKind.None, second);
  }

  [Fact]
  public void SameCreatureIdAfterWindow_EmitsAgain() {
    // Instance ids can be reused by the engine after a creature despawns; once the dedup window has
    // elapsed a fresh kill on that id is a real, distinct killing blow.
    var classifier = new GameplayEventClassifier(dedupSeconds: 2.0);

    var first = classifier.ClassifyCreatureDamage(1, attackerIsPlayer: true, creatureDied: true, nowSeconds: 10.0);
    var later = classifier.ClassifyCreatureDamage(1, attackerIsPlayer: true, creatureDied: true, nowSeconds: 20.0);

    Assert.Equal(GameplayEventKind.KillingBlow, first);
    Assert.Equal(GameplayEventKind.KillingBlow, later);
  }

  [Fact]
  public void DistinctCreatures_EachEmit() {
    var classifier = new GameplayEventClassifier();

    var a = classifier.ClassifyCreatureDamage(1, attackerIsPlayer: true, creatureDied: true, nowSeconds: 10.0);
    var b = classifier.ClassifyCreatureDamage(2, attackerIsPlayer: true, creatureDied: true, nowSeconds: 10.0);

    Assert.Equal(GameplayEventKind.KillingBlow, a);
    Assert.Equal(GameplayEventKind.KillingBlow, b);
  }
}
