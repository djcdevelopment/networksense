using System;
using System.IO;
using System.Linq;
using System.Text.Json;

using Lumberjacks.Contracts.Valheim;

using Xunit;

namespace ComfyNetworkSense.Tests;

public sealed class ValheimRoutedRpcAdmissionsTests
{
  [Fact]
  public void Catalog_ContainsP1RuntimeObservedP2AndReplacementOwnedAdmissions() {
    ValheimRoutedRpcAdmission[] harness =
        ValheimRoutedRpcAdmissions.Entries
            .Where(entry => entry.Priority == ValheimRoutedRpcPriority.Harness)
            .ToArray();
    ValheimRoutedRpcAdmission[] p1 =
        ValheimRoutedRpcAdmissions.Entries
            .Where(entry => entry.Priority == ValheimRoutedRpcPriority.P1)
            .ToArray();
    ValheimRoutedRpcAdmission[] runtime =
        ValheimRoutedRpcAdmissions.Entries
            .Where(entry => entry.Priority == ValheimRoutedRpcPriority.Runtime)
            .ToArray();
    ValheimRoutedRpcAdmission[] p2 =
        ValheimRoutedRpcAdmissions.Entries
            .Where(entry => entry.Priority == ValheimRoutedRpcPriority.P2)
            .ToArray();
    ValheimRoutedRpcAdmission[] superseded =
        ValheimRoutedRpcAdmissions.Entries
            .Where(entry =>
                entry.Priority == ValheimRoutedRpcPriority.Superseded)
            .ToArray();

    Assert.Equal(7, harness.Length);
    Assert.Equal(
        new[] {
            ValheimRoutedRpcAdmissions.ModAutoPort,
            ValheimRoutedRpcAdmissions.ModGameplayEvent,
            ValheimRoutedRpcAdmissions.ModServerPulse
        },
        runtime.Select(entry => entry.Name)
            .OrderBy(name => name, StringComparer.Ordinal));
    Assert.Equal(
        new[] {
            "RPC_DamageText",
            "RPC_HealthChanged",
            "RPC_UpdateMaterial",
            "SetEvent",
            "Step"
        },
        p2.Select(entry => entry.Name)
            .OrderBy(name => name, StringComparer.Ordinal));
    Assert.Equal(8, superseded.Length);
    Assert.All(
        superseded,
        entry => {
          Assert.Equal(
              ValheimRoutedRpcDisposition.Supersede,
              entry.Disposition);
          Assert.False(string.IsNullOrWhiteSpace(entry.ReplacementLane));
        });
    Assert.Equal("zdo_journal", Find("DestroyZDO").ReplacementLane);
    Assert.Equal("world_zone_descriptor", Find("GlobalKeys").ReplacementLane);
    Assert.Equal("world_zone_descriptor", Find("LocationIcons").ReplacementLane);
    Assert.Equal("logical_peer_session", Find("Ping").ReplacementLane);
    Assert.Equal("logical_peer_session", Find("Pong").ReplacementLane);
    Assert.Equal("world_zone_descriptor", Find("RemoveGlobalKey").ReplacementLane);
    Assert.Equal("zdo_journal", Find("RequestZDO").ReplacementLane);
    Assert.Equal("world_zone_descriptor", Find("SetGlobalKey").ReplacementLane);
    Assert.All(
        harness.Concat(runtime).Concat(p2).Concat(p1),
        entry => {
          Assert.Equal(ValheimRoutedRpcDisposition.Route, entry.Disposition);
          Assert.Equal(string.Empty, entry.ReplacementLane);
        });
    Assert.Equal(33, p1.Length);
    Assert.Equal(29, p1.Count(entry => entry.Scope == ValheimRoutedRpcScope.Instance));
    Assert.Equal(4, p1.Count(entry => entry.Scope == ValheimRoutedRpcScope.Global));
    Assert.Equal(
        new[] { "ChatMessage", "ShowMessage", "SleepStart", "SleepStop" },
        p1.Where(entry => entry.Scope == ValheimRoutedRpcScope.Global)
            .Select(entry => entry.Name)
            .OrderBy(name => name, StringComparer.Ordinal));
  }

  [Fact]
  public void Catalog_HashesMatchValheimAndHaveNoCollisions() {
    Assert.Equal(213315071,
        ValheimRoutedRpcAdmissions.StableHash("RPC_ResetCloth"));
    Assert.Equal(1130726949,
        ValheimRoutedRpcAdmissions.StableHash("RPC_Damage"));
    Assert.Equal(-1182660091,
        ValheimRoutedRpcAdmissions.StableHash("ChatMessage"));
    Assert.Equal(-543064489,
        ValheimRoutedRpcAdmissions.StableHash("SleepStop"));
    Assert.Equal(-461013576,
        ValheimRoutedRpcAdmissions.StableHash("Step"));
    Assert.Equal(-257317232,
        ValheimRoutedRpcAdmissions.StableHash("RPC_HealthChanged"));
    Assert.Equal(500148310,
        ValheimRoutedRpcAdmissions.StableHash("RPC_UpdateMaterial"));
    Assert.Equal(617879363,
        ValheimRoutedRpcAdmissions.StableHash(
            ValheimRoutedRpcAdmissions.ModServerPulse));

    Assert.Equal(
        ValheimRoutedRpcAdmissions.Entries.Count,
        ValheimRoutedRpcAdmissions.Entries
            .Select(entry => entry.MethodHash)
            .Distinct()
            .Count());
    Assert.All(
        ValheimRoutedRpcAdmissions.Entries,
        entry => Assert.Equal(
            entry.MethodHash,
            ValheimRoutedRpcAdmissions.StableHash(entry.Name)));
  }

  [Fact]
  public void NativePayloadSignatures_MatchPinnedExtractorV2Inventory() {
    string path = Path.Combine(
        AppContext.BaseDirectory,
        "synthetic_baseline_v2.json");
    using JsonDocument baseline = JsonDocument.Parse(File.ReadAllText(path));

    foreach (ValheimRoutedRpcAdmission admission in
             ValheimRoutedRpcAdmissions.Entries.Where(
                 entry => entry.Priority == ValheimRoutedRpcPriority.P1
                     || entry.Priority == ValheimRoutedRpcPriority.P2
                     || entry.Priority == ValheimRoutedRpcPriority.Superseded)) {
      string sectionName = admission.Scope == ValheimRoutedRpcScope.Instance
          ? "InstanceRPCs"
          : "RoutedRPCs";
      JsonElement section = baseline.RootElement.GetProperty(sectionName);
      Assert.True(
          section.TryGetProperty(admission.Name, out JsonElement extracted),
          $"{admission.Name} missing from {sectionName}");

      string[] extractedSignatures = ReadSignatures(extracted);
      Assert.Equal(
          admission.PayloadSignatures.OrderBy(value => value, StringComparer.Ordinal),
          extractedSignatures.OrderBy(value => value, StringComparer.Ordinal));
    }
  }

  [Fact]
  public void EnvelopeGate_RequiresExactNameHashScopeAndSize() {
    ValheimRoutedRpcAdmission global = Find("ChatMessage");
    ValheimRoutedRpcAdmission instance = Find("RPC_Damage");

    Assert.True(ValheimRoutedRpcAdmissions.AllowsEnvelope(
        global.Name, global.MethodHash, 0, 0, BuildChatMessage()));
    Assert.False(ValheimRoutedRpcAdmissions.AllowsEnvelope(
        global.Name, global.MethodHash, 10, 2, BuildChatMessage()));
    Assert.False(ValheimRoutedRpcAdmissions.AllowsEnvelope(
        global.Name, global.MethodHash, 0, 2, BuildChatMessage()));
    Assert.True(ValheimRoutedRpcAdmissions.AllowsEnvelope(
        instance.Name, instance.MethodHash, 10, 2, BuildHitData()));
    Assert.False(ValheimRoutedRpcAdmissions.AllowsEnvelope(
        instance.Name, instance.MethodHash, 0, 0, BuildHitData()));
    Assert.False(ValheimRoutedRpcAdmissions.AllowsEnvelope(
        instance.Name, instance.MethodHash, 10, 0, BuildHitData()));
    Assert.False(ValheimRoutedRpcAdmissions.AllowsEnvelope(
        global.Name + "_wrong", global.MethodHash, 0, 0, BuildChatMessage()));
    Assert.False(ValheimRoutedRpcAdmissions.AllowsEnvelope(
        global.Name, global.MethodHash + 1, 0, 0, BuildChatMessage()));
    Assert.False(ValheimRoutedRpcAdmissions.AllowsEnvelope(
        global.Name,
        global.MethodHash,
        0,
        0,
        BuildChatMessage().Concat(new byte[] { 0 }).ToArray()));
    Assert.False(ValheimRoutedRpcAdmissions.AllowsEnvelope(
        global.Name,
        global.MethodHash,
        0,
        0,
        new byte[ValheimRoutedRpcAdmissions.MaximumParameterBytes + 1]));
  }

  [Fact]
  public void PortalConnectionRpc_RemainsUnadmittedAsAPoisonTripwire() {
    const string method = "RPC_SetConnection";
    int hash = ValheimRoutedRpcAdmissions.StableHash(method);
    byte[] extractedPayload = BuildPayload("ZDOID,ZDOID");

    Assert.False(ValheimRoutedRpcAdmissions.TryGet(
        method, hash, out _));
    Assert.DoesNotContain(
        ValheimRoutedRpcAdmissions.Entries,
        entry => string.Equals(entry.Name, method, StringComparison.Ordinal));
    Assert.False(ValheimRoutedRpcAdmissions.AllowsEnvelope(
        method, hash, 0, 0, extractedPayload));
    Assert.False(ValheimRoutedRpcAdmissions.AllowsRoutedEnvelope(
        method, hash, 0, 0, extractedPayload));
  }

  [Fact]
  public void PayloadGate_AcceptsEveryExtractedShapeAndRejectsMalformedBounds() {
    foreach (ValheimRoutedRpcAdmission admission in
             ValheimRoutedRpcAdmissions.Entries) {
      long targetUser = admission.Scope == ValheimRoutedRpcScope.Instance ? 10 : 0;
      uint targetId = admission.Scope == ValheimRoutedRpcScope.Instance ? 2U : 0U;
      foreach (string signature in admission.PayloadSignatures) {
        byte[] payload = BuildPayload(signature);
        Assert.True(
            ValheimRoutedRpcAdmissions.AllowsEnvelope(
                admission.Name,
                admission.MethodHash,
                targetUser,
                targetId,
                payload),
            $"valid {admission.Name} payload {signature}");
        Assert.Equal(
            admission.Disposition == ValheimRoutedRpcDisposition.Route,
            ValheimRoutedRpcAdmissions.AllowsRoutedEnvelope(
                admission.Name,
                admission.MethodHash,
                targetUser,
                targetId,
                payload));
        Assert.False(
            ValheimRoutedRpcAdmissions.AllowsEnvelope(
                admission.Name,
                admission.MethodHash,
                targetUser,
                targetId,
                payload.Concat(new byte[] { 0 }).ToArray()),
            $"trailing byte for {admission.Name} payload {signature}");
        if (payload.Length > 0) {
          Assert.False(
              ValheimRoutedRpcAdmissions.AllowsEnvelope(
                  admission.Name,
                  admission.MethodHash,
                  targetUser,
                  targetId,
                  payload.Take(payload.Length - 1).ToArray()),
              $"truncated {admission.Name} payload {signature}");
        }
      }
    }

    ValheimRoutedRpcAdmission useDoor = Find("UseDoor");
    Assert.False(ValheimRoutedRpcAdmissions.AllowsEnvelope(
        useDoor.Name, useDoor.MethodHash, 10, 2, new byte[] { 2 }));
    ValheimRoutedRpcAdmission addItem = Find("RPC_AddItem");
    Assert.False(ValheimRoutedRpcAdmissions.AllowsEnvelope(
        addItem.Name,
        addItem.MethodHash,
        10,
        2,
        new byte[] { 2, 0xc3, 0x28 }));
    ValheimRoutedRpcAdmission globalKeys = Find("GlobalKeys");
    Assert.False(ValheimRoutedRpcAdmissions.AllowsEnvelope(
        globalKeys.Name,
        globalKeys.MethodHash,
        0,
        0,
        new byte[] { 0xff, 0xff, 0xff, 0xff }));
  }

  static ValheimRoutedRpcAdmission Find(string name) =>
      Assert.Single(
          ValheimRoutedRpcAdmissions.Entries,
          entry => string.Equals(entry.Name, name, StringComparison.Ordinal));

  static string[] ReadSignatures(JsonElement extracted) {
    if (extracted.TryGetProperty("signatures", out JsonElement variants))
      return variants.EnumerateArray().Select(JoinSignature).ToArray();
    return new[] { JoinSignature(extracted.GetProperty("signature")) };
  }

  static string JoinSignature(JsonElement signature) =>
      string.Join(
          ",",
          signature.EnumerateArray().Select(element => element.GetString()));

  static byte[] BuildChatMessage() =>
      BuildPayload("Vector3,Int32,UserInfo,String");

  static byte[] BuildHitData() => BuildPayload("HitData");

  static byte[] BuildPayload(string signature) {
    using MemoryStream stream = new();
    using BinaryWriter writer = new(stream);
    if (signature.Length == 0) return Array.Empty<byte>();
    foreach (string type in signature.Split(',')) {
      switch (type) {
        case "Boolean":
          writer.Write(true);
          break;
        case "Int32":
          writer.Write(17);
          break;
        case "Int64":
          writer.Write(23L);
          break;
        case "Single":
          writer.Write(1.25f);
          break;
        case "String":
          writer.Write("payload");
          break;
        case "List`1":
          writer.Write(2);
          writer.Write("first");
          writer.Write("second");
          break;
        case "Vector3":
          writer.Write(1.0f);
          writer.Write(2.0f);
          writer.Write(3.0f);
          break;
        case "Quaternion":
          writer.Write(0.0f);
          writer.Write(0.0f);
          writer.Write(0.0f);
          writer.Write(1.0f);
          break;
        case "ZPackage":
          writer.Write(3);
          writer.Write(new byte[] { 4, 5, 6 });
          break;
        case "ZDOID":
          writer.Write(23L);
          writer.Write(5U);
          break;
        case "UserInfo":
          writer.Write("character");
          writer.Write("platform-user");
          break;
        case "HitData":
          WriteMinimalHitData(writer);
          break;
        default:
          throw new InvalidOperationException("unsupported test type " + type);
      }
    }
    writer.Flush();
    return stream.ToArray();
  }

  static void WriteMinimalHitData(BinaryWriter writer) {
    writer.Write((ushort)0);
    writer.Write((short)0);
    writer.Write((byte)0);
    for (var index = 0; index < 6; index++) writer.Write(0.0f);
    writer.Write(0);
    writer.Write((short)0);
    writer.Write((char)0);
    writer.Write(0.0f);
    writer.Write((short)0);
    writer.Write((byte)0);
    writer.Write((byte)0);
    writer.Write(0.0f);
    writer.Write(0.0f);
  }
}
