namespace ComfyNetworkSense.Tests;

using System;
using System.Collections.Generic;
using System.Linq;

using ComfyQuestLab;

using Xunit;

/// <summary>Pins the PlanBuild .blueprint parser to the format PlanBuild actually
/// writes. The golden fixtures below are verbatim copies of PlanBuild's own checked-in
/// test resources (PlanBuildTest/resources, MIT-licensed) — deliberately not the output
/// of our own generator, so format drift between the two projects fails here and not in
/// a half-placed building.</summary>
public class BlueprintFileTests {
  // PlanBuildTest/resources/TestBox_V2.blueprint, verbatim. Notable: "#Name: Custom
  // Name" carries a leading space in the value, "#Description" is a bare section whose
  // text is the following lines, and every piece line ends with an EMPTY additionalInfo
  // field (trailing semicolon) and no scale fields.
  const string TestBoxV2 = "#Name: Custom Name\n"
      + "#Description\n"
      + "Description with\n"
      + "newlines and such :)\n"
      + "#Pieces\n"
      + "stone_floor_2x2;Building;1;0;1;0;1;0;-4.371139E-08;\n"
      + "woodwall;Building;0;1.5;1;0;0.7071068;0;-0.7071068;\n"
      + "woodwall;Building;1;1.5;0;0;1;0;-4.371139E-08;\n"
      + "woodwall;Building;1;1.5;2;0;-1.748456E-07;0;1;\n"
      + "woodwall;Building;2;1.5;1;0;0.7071069;0;0.7071067;\n"
      + "wood_floor;Building;1;2.5;1;0;0.7071068;0;-0.7071068;\n";

  // PlanBuildTest/resources/Tree_V2_SnapPoints.blueprint, verbatim (head).
  const string TreeV2SnapPoints = "#SnapPoints\n"
      + "-0.7653809;4;1.847778\n"
      + "-1.847778;4;-0.7653809\n"
      + "0.7653809;4;-1.847778\n"
      + "1.847778;4;0.7653809\n"
      + "#Pieces\n"
      + "wood_pole_log_4;Building;0;2;0;0;0.1950903;0;-0.9807853;\n"
      + "woodiron_beam;Building;-0.9238892;4;-0.3826904;0;0.1950903;0;-0.9807853;\n";

  static string[] Lines(string text) {
    return text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
  }

  static BlueprintFile Parse(string text, out List<string> errors) {
    BlueprintFile bp;
    Assert.True(BlueprintFile.TryParse(Lines(text), out bp, out errors));
    return bp;
  }

  [Fact]
  public void GoldenTestBoxParses() {
    List<string> errors;
    BlueprintFile bp = Parse(TestBoxV2, out errors);
    Assert.Empty(errors);
    Assert.Equal("Custom Name", bp.Name);
    Assert.Equal("Description with\nnewlines and such :)", bp.Headers["Description"]);
    Assert.Equal(6, bp.Pieces.Count);
    Assert.Equal(6, bp.BuildablePieceCount);
    Assert.Equal(0, bp.ScaleRejectedCount);
    Assert.All(bp.Pieces, p => Assert.Equal("", p.Info));
    Assert.All(bp.Pieces, p => Assert.Equal("Building", p.Category));

    BpPiece floor = bp.Pieces[0];
    Assert.Equal("stone_floor_2x2", floor.Prefab);
    Assert.Equal(1f, floor.PosX);
    Assert.Equal(0f, floor.PosY);
    Assert.Equal(1f, floor.PosZ);
    Assert.Equal(-4.371139E-08f, floor.RotW, 6);

    Assert.Equal(0f, bp.MinX);
    Assert.Equal(2f, bp.MaxX);
    Assert.Equal(0f, bp.MinY);
    Assert.Equal(2.5f, bp.MaxY);
  }

  [Fact]
  public void GoldenSnapPointsCountedAndSkipped() {
    List<string> errors;
    BlueprintFile bp = Parse(TreeV2SnapPoints, out errors);
    Assert.Empty(errors);
    Assert.Equal(4, bp.SnapPointCount);
    Assert.Equal(2, bp.Pieces.Count);
  }

  [Fact]
  public void UnitScaleAcceptedNonUnitRejected() {
    List<string> errors;
    BlueprintFile bp = Parse(
        "#Pieces\n"
        + "wood_floor;Building;0;0;0;0;0;0;1;\"\";1;1;1\n"
        + "wood_floor;Building;2;0;0;0;0;0;1;\"\";2;1;1\n", out errors);
    Assert.Equal(2, bp.Pieces.Count);
    Assert.Equal(1, bp.BuildablePieceCount);
    Assert.Equal(1, bp.ScaleRejectedCount);
    Assert.False(bp.Pieces[0].ScaleRejected);
    Assert.True(bp.Pieces[1].ScaleRejected);
    // The rejected piece at x=2 must not stretch the buildable footprint.
    Assert.Equal(0f, bp.MaxX);
  }

  [Fact]
  public void CommaDecimalsRetriedPerField() {
    List<string> errors;
    BlueprintFile bp = Parse("#Pieces\nwood_floor;Building;1,5;0;-2,25;0;0;0;1;\n",
                             out errors);
    Assert.Empty(errors);
    Assert.Equal(1.5f, bp.Pieces[0].PosX);
    Assert.Equal(-2.25f, bp.Pieces[0].PosZ);
  }

  [Fact]
  public void UnknownSectionIsFreeTextNotError() {
    List<string> errors;
    BlueprintFile bp = Parse(
        "#SomeFutureSection\nwhatever;this;is\n#Pieces\nwood_floor;Building;0;0;0;0;0;0;1;\n",
        out errors);
    Assert.Empty(errors);
    Assert.Single(bp.Pieces);
    Assert.Equal("whatever;this;is", bp.Headers["SomeFutureSection"]);
  }

  [Fact]
  public void SemicolonedInfoWithScaleTailRejoined() {
    List<string> errors;
    BlueprintFile bp = Parse(
        "#Pieces\nsign;Misc;0;0;0;0;0;0;1;left;middle;right;1;1;1\n", out errors);
    Assert.Empty(errors);
    Assert.Equal("left;middle;right", bp.Pieces[0].Info);
    Assert.False(bp.Pieces[0].ScaleRejected);
  }

  [Fact]
  public void SemicolonedInfoWithoutNumericTailAllInfo() {
    List<string> errors;
    BlueprintFile bp = Parse(
        "#Pieces\nsign;Misc;0;0;0;0;0;0;1;one;two;three\n", out errors);
    Assert.Empty(errors);
    Assert.Equal("one;two;three", bp.Pieces[0].Info);
  }

  [Fact]
  public void BadLinesRecordedParseContinues() {
    List<string> errors;
    BlueprintFile bp = Parse(
        "#Pieces\n"
        + "garbage line without semicolons\n"
        + "wood_floor;Building;zzz;0;0;0;0;0;1;\n"
        + ";Building;0;0;0;0;0;0;1;\n"
        + "wood_floor;Building;0;0;0;0;0;0;1;\n", out errors);
    Assert.Single(bp.Pieces);
    Assert.Equal(3, bp.BadLineCount);
    Assert.Equal(3, errors.Count);
    Assert.Contains(errors, e => e.Contains("line 2"));
    Assert.Contains(errors, e => e.Contains("unparseable"));
    Assert.Contains(errors, e => e.Contains("empty prefab"));
  }

  [Fact]
  public void CarriageReturnsAndBlankLinesTolerated() {
    List<string> errors;
    string[] lines = {
      "#Name:X\r", "", "#Pieces\r", "wood_floor;Building;0;0;0;0;0;0;1;\r", "  ",
    };
    BlueprintFile bp;
    Assert.True(BlueprintFile.TryParse(lines, out bp, out errors));
    Assert.Empty(errors);
    Assert.Single(bp.Pieces);
    Assert.Equal("X", bp.Name);
  }

  [Fact]
  public void NoBuildablePiecesReturnsFalse() {
    BlueprintFile bp;
    List<string> errors;
    Assert.False(BlueprintFile.TryParse(Lines("#Name:Empty\n#Pieces\n"), out bp,
                                        out errors));
    Assert.False(BlueprintFile.TryParse(
        Lines("#Pieces\nwood_floor;Building;0;0;0;0;0;0;1;\"\";2;2;2\n"), out bp,
        out errors));
    Assert.Equal(1, bp.ScaleRejectedCount);
  }

  [Fact]
  public void InfoUnwrapsPlanBuildJsonForms() {
    List<string> errors;
    BlueprintFile bp = Parse(
        "#Pieces\n"
        + "sign;Misc;0;0;0;0;0;0;1;\"hello \\\"world\\\"\";1;1;1\n"
        + "sign;Misc;2;0;0;0;0;0;1;\"\"\n"
        + "sign;Misc;4;0;0;0;0;0;1;null\n", out errors);
    Assert.Empty(errors);
    Assert.Equal("hello \"world\"", bp.Pieces[0].Info);
    Assert.Equal("", bp.Pieces[1].Info);
    Assert.Equal("", bp.Pieces[2].Info);
  }

  [Fact]
  public void NineFieldLineAcceptedWithEmptyInfo() {
    List<string> errors;
    BlueprintFile bp = Parse("#Pieces\nwood_floor;Building;0;0;0;0;0;0;1\n", out errors);
    Assert.Empty(errors);
    Assert.Equal("", bp.Pieces[0].Info);
  }

  [Fact]
  public void HeaderValuesTrimmedAndCaseInsensitive() {
    List<string> errors;
    BlueprintFile bp = Parse(
        "#Creator:  Derek \n#Coordinates:12.5,30,-7\n#Pieces\nwood_floor;Building;0;0;0;0;0;0;1;\n",
        out errors);
    Assert.Equal("Derek", bp.Headers["creator"]);
    Assert.Equal("12.5,30,-7", bp.Headers["Coordinates"]);
  }
}
