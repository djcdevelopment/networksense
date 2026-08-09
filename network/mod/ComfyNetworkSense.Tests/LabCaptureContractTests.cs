namespace ComfyNetworkSense.Tests;

using System;
using System.Linq;

using ComfyQuestLab;

using Xunit;

public sealed class LabCaptureContractTests {
  static LabCapturePiece Piece(string prefab, float x, float y, float z) {
    return new LabCapturePiece {
      Prefab = prefab,
      Category = "Building",
      X = x, Y = y, Z = z, Qw = 1f,
    };
  }

  [Fact]
  public void SameBuildAtAnotherWorldPosition_HasIdenticalBytesAndHash() {
    LabCaptureArtifact first = LabCaptureContract.Create("my-hall", "mine", 20f, new[] {
      Piece("wood_wall", 100f, 70f, -30f),
      Piece("stone_floor", 98f, 68f, -32f),
    });
    LabCaptureArtifact moved = LabCaptureContract.Create("my-hall", "mine", 20f, new[] {
      Piece("wood_wall", -400f, 170f, 970f),
      Piece("stone_floor", -402f, 168f, 968f),
    });

    Assert.Equal(first.PiecesSha256, moved.PiecesSha256);
    Assert.Equal(LabCaptureContract.Serialize(first), LabCaptureContract.Serialize(moved));
    Assert.Equal(0f, first.Pieces.Min(p => p.X));
    Assert.Equal(0f, first.Pieces.Min(p => p.Y));
    Assert.Equal(0f, first.Pieces.Min(p => p.Z));
  }

  [Fact]
  public void RoundTripPreservesSupportedMetadata() {
    LabCapturePiece sign = Piece("sign", 3f, 4f, 5f);
    sign.HasSignText = true;
    sign.SignText = "sign here\nthen look up";
    sign.HasItemStand = true;
    sign.ItemPrefab = "QueensJam";
    sign.ItemVariant = 2;
    sign.ItemQuality = 3;
    sign.ItemType = 1;
    sign.RuneSchool = "social";
    sign.RuneStyle = "sign-face";
    sign.TextGlowSchool = "social";
    LabCaptureArtifact source = LabCaptureContract.Create("metadata", "lab", 8f,
        new[] { sign });

    string json = LabCaptureContract.Serialize(source);
    LabCaptureArtifact parsed = LabCaptureContract.Deserialize(json);
    string error;

    Assert.True(LabCaptureContract.TryValidate(parsed, out error), error);
    Assert.Equal("sign here\nthen look up", parsed.Pieces[0].SignText);
    Assert.Equal("QueensJam", parsed.Pieces[0].ItemPrefab);
    Assert.Equal(3, parsed.Pieces[0].ItemQuality);
    Assert.Equal("sign-face", parsed.Pieces[0].RuneStyle);
  }

  [Fact]
  public void PlanBuildProjectionRoundTripsAndAgreesWithSidecar() {
    LabCaptureArtifact artifact = LabCaptureContract.Create("projection", "mine", 12f,
        new[] { Piece("wood_wall", 4.125f, 2f, -8.5f), Piece("sign", 1f, 2f, -3f) });
    string text = LabCaptureContract.ToBlueprintText(artifact);
    BlueprintFile blueprint;
    System.Collections.Generic.List<string> problems;

    Assert.True(BlueprintFile.TryParse(text.Split('\n'), out blueprint, out problems));
    Assert.Empty(problems);
    string error;
    Assert.True(LabCaptureContract.BlueprintMatches(artifact, blueprint, out error), error);
    Assert.Equal(artifact.PieceCount, blueprint.BuildablePieceCount);
    Assert.Contains("metadata is in the .capture.json sidecar", text);
  }

  [Fact]
  public void TamperAndOversizeFailClosed() {
    LabCaptureArtifact artifact = LabCaptureContract.Create("safe", "mine", 12f,
        new[] { Piece("wood_floor", 0f, 0f, 0f) });
    artifact.Pieces[0].X = 5f;
    string error;
    Assert.False(LabCaptureContract.TryValidate(artifact, out error));
    Assert.Contains("normalized", error);

    Assert.Throws<System.IO.InvalidDataException>(() => LabCaptureContract.Create(
        "too-big", "mine", 20f,
        Enumerable.Range(0, LabCaptureContract.MaxPieces + 1)
            .Select(i => Piece("wood_floor", i, 0f, 0f))));

    Assert.Throws<System.IO.InvalidDataException>(() => LabCaptureContract.Create(
        "too-wide", "mine", 40f,
        new[] { Piece("wood_floor", 0f, 0f, 0f), Piece("wood_floor", 81f, 0f, 0f) }));
  }

  [Fact]
  public void StructuralDiffIsTranslationIndependentAndMetadataSensitive() {
    LabCapturePiece expectedSign = Piece("sign", 10f, 2f, 10f);
    expectedSign.HasSignText = true;
    expectedSign.SignText = "hello";
    LabCapturePiece movedSign = Piece("sign", 110f, 52f, -90f);
    movedSign.HasSignText = true;
    movedSign.SignText = "hello";

    LabCaptureDiff same = LabCaptureContract.Diff(new[] { expectedSign }, new[] { movedSign });
    Assert.True(same.Equal);

    movedSign.SignText = "goodbye";
    LabCaptureDiff changed = LabCaptureContract.Diff(new[] { expectedSign }, new[] { movedSign });
    Assert.False(changed.Equal);
    Assert.Equal(1, changed.MissingCount);
    Assert.Equal(1, changed.ExtraCount);
  }

  [Fact]
  public void QuaternionSignAndLengthNormalizeDeterministically() {
    LabCapturePiece first = Piece("wood_wall", 0f, 0f, 0f);
    first.Qx = 0f; first.Qy = 1.4142135f; first.Qz = 0f; first.Qw = -1.4142135f;
    LabCapturePiece sameRotation = Piece("wood_wall", 0f, 0f, 0f);
    sameRotation.Qx = 0f; sameRotation.Qy = -0.7071068f;
    sameRotation.Qz = 0f; sameRotation.Qw = 0.7071068f;

    LabCaptureArtifact left = LabCaptureContract.Create("rotation", "mine", 5f,
        new[] { first });
    LabCaptureArtifact right = LabCaptureContract.Create("rotation", "mine", 5f,
        new[] { sameRotation });

    Assert.Equal(left.PiecesSha256, right.PiecesSha256);
    Assert.Equal(LabCaptureContract.Serialize(left), LabCaptureContract.Serialize(right));
  }

  [Theory]
  [InlineData("../escape")]
  [InlineData("spaces are not portable")]
  [InlineData("")]
  public void UnsafeNamesAreRejected(string name) {
    Assert.Throws<System.IO.InvalidDataException>(() => LabCaptureContract.Create(
        name, "mine", 5f, new[] { Piece("wood_floor", 0f, 0f, 0f) }));
  }
}
