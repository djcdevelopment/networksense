using ComfyNetworkSense;
using Xunit;

namespace ComfyNetworkSense.Tests;

public class GameplayEventClassifierTests {
  [Fact]
  public void FirstHitOnACreature_EmitsFirstHit() {
    var classifier = new GameplayEventClassifier();
    Assert.Equal(GameplayEventKind.FirstHit, classifier.RegisterHit(1, 10.0));
  }

  [Fact]
  public void SubsequentHitsWithinWindow_EmitNothing() {
    var classifier = new GameplayEventClassifier(windowSeconds: 30.0);

    Assert.Equal(GameplayEventKind.FirstHit, classifier.RegisterHit(1, 10.0));
    Assert.Equal(GameplayEventKind.None, classifier.RegisterHit(1, 11.0));
    Assert.Equal(GameplayEventKind.None, classifier.RegisterHit(1, 25.0));
  }

  [Fact]
  public void HitAfterWindow_EmitsFirstHitAgain() {
    // Instance ids get recycled after a creature despawns; past the window a hit is a new engagement.
    var classifier = new GameplayEventClassifier(windowSeconds: 30.0);

    Assert.Equal(GameplayEventKind.FirstHit, classifier.RegisterHit(1, 10.0));
    Assert.Equal(GameplayEventKind.FirstHit, classifier.RegisterHit(1, 50.0));
  }

  [Fact]
  public void Death_EmitsKillingBlow() {
    var classifier = new GameplayEventClassifier();
    Assert.Equal(GameplayEventKind.KillingBlow, classifier.RegisterDeath(1, 10.0));
  }

  [Fact]
  public void DuplicateDeathWithinWindow_EmitsOnce() {
    var classifier = new GameplayEventClassifier(windowSeconds: 30.0);

    Assert.Equal(GameplayEventKind.KillingBlow, classifier.RegisterDeath(1, 10.0));
    Assert.Equal(GameplayEventKind.None, classifier.RegisterDeath(1, 10.5));
  }

  [Fact]
  public void DeathClearsFirstHit_SoAReusedIdEngagesAgain() {
    var classifier = new GameplayEventClassifier(windowSeconds: 30.0);

    Assert.Equal(GameplayEventKind.FirstHit, classifier.RegisterHit(1, 10.0));
    Assert.Equal(GameplayEventKind.KillingBlow, classifier.RegisterDeath(1, 11.0));
    // Same id reused shortly after death (a new creature) -> a fresh first_hit, not suppressed.
    Assert.Equal(GameplayEventKind.FirstHit, classifier.RegisterHit(1, 12.0));
  }

  [Fact]
  public void DistinctCreatures_EachEngageAndDie() {
    var classifier = new GameplayEventClassifier();

    Assert.Equal(GameplayEventKind.FirstHit, classifier.RegisterHit(1, 10.0));
    Assert.Equal(GameplayEventKind.FirstHit, classifier.RegisterHit(2, 10.0));
    Assert.Equal(GameplayEventKind.KillingBlow, classifier.RegisterDeath(1, 12.0));
    Assert.Equal(GameplayEventKind.KillingBlow, classifier.RegisterDeath(2, 12.0));
  }

  [Fact]
  public void FullSequence_FirstHitThenKillingBlow() {
    var classifier = new GameplayEventClassifier();

    Assert.Equal(GameplayEventKind.FirstHit, classifier.RegisterHit(7, 10.0));
    Assert.Equal(GameplayEventKind.None, classifier.RegisterHit(7, 10.5));  // more hits
    Assert.Equal(GameplayEventKind.KillingBlow, classifier.RegisterDeath(7, 11.0));
  }
}
