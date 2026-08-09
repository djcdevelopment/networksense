using System;
using System.Linq;
using ComfyQuestLab;
using Xunit;

namespace ComfyNetworkSense.Tests;

public sealed class LabTreeRecoveryContractTests {
  [Fact]
  public void FortyThreeRecordLedger_RoundTripsEveryRecord() {
    LabTreeRecoveryRecord[] records = Enumerable.Range(0, 43)
        .Select(index => new LabTreeRecoveryRecord {
          Prefab = index % 7 == 0 ? "Birch1" : "Beech1",
          PrefabHash = -493262268 + index,
          X = index + 0.125f,
          Y = 70f + index / 10f,
          Z = -index - 0.375f,
          Qw = 1f,
          HasEuler = true,
          Rx = index,
          Ry = index * 7f,
          Rz = 360f - index,
          Sx = 1.2f,
          Sy = 1.2f,
          Sz = 1.2f,
        })
        .ToArray();
    var ledger = new LabTreeRecoveryLedger {
      Schema = "comfy-questlab-tree-recovery/v1",
      PluginRelease = "questlab-test",
      ProfileId = "marble-grand",
      BuildId = "forensic-43",
      CreatedUtc = DateTimeOffset.UtcNow.ToString("O"),
      RestoredUtc = string.Empty,
      RecordCount = records.Length,
      RemovedCount = records.Length,
      Trees = records,
    };

    string json = LabTreeRecoveryContract.Serialize(ledger);
    LabTreeRecoveryLedger parsed = LabTreeRecoveryContract.Deserialize(json);

    Assert.Contains("\"Trees\":[", json);
    Assert.NotNull(parsed);
    Assert.Equal(43, parsed.RecordCount);
    Assert.Equal(43, parsed.Trees.Length);
    Assert.Equal("Birch1", parsed.Trees[42].Prefab);
    Assert.Equal(42.125f, parsed.Trees[42].X);
    Assert.Equal(294f, parsed.Trees[42].Ry);
  }

  [Fact]
  public void ForensicV1FieldShape_DeserializesRecordArray() {
    const string json = "{\"Schema\":\"comfy-questlab-tree-recovery/v1\","
        + "\"PluginRelease\":\"questlab-r18\",\"ProfileId\":\"marble-grand\","
        + "\"BuildId\":\"old-build\",\"CreatedUtc\":\"2026-08-09T00:00:00Z\","
        + "\"RestoredUtc\":\"\",\"Restored\":false,\"RecordCount\":1,"
        + "\"RecordsSha256\":\"\",\"RemovedCount\":1,\"RestoredCount\":0,"
        + "\"Trees\":[{\"Prefab\":\"Beech1\",\"PrefabHash\":-493262268,"
        + "\"X\":-15.366909,\"Y\":81.67186,\"Z\":-22.587196,"
        + "\"Qx\":0,\"Qy\":0,\"Qz\":0,\"Qw\":1,\"HasEuler\":true,"
        + "\"Rx\":2.9461212,\"Ry\":327,\"Rz\":359.95813,"
        + "\"Sx\":1.2195652,\"Sy\":1.2195652,\"Sz\":1.2195652,"
        + "\"HasHealth\":false,\"Health\":0}]}";

    LabTreeRecoveryLedger parsed = LabTreeRecoveryContract.Deserialize(json);

    Assert.Single(parsed.Trees);
    Assert.Equal("Beech1", parsed.Trees[0].Prefab);
    Assert.Equal(-493262268, parsed.Trees[0].PrefabHash);
    Assert.True(parsed.Trees[0].HasEuler);
  }
}
