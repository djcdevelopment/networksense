namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

using UnityEngine;

/// <summary>Decodes an authoritative Lumberjacks ZDO envelope into Valheim's wire package shape.</summary>
public static class ZdoRedirectEnvelopeCodec {
  [Serializable]
  public sealed class PendingResponse {
    public int schema_version;
    public string window_id;
    public Envelope[] envelopes;
  }

  [Serializable]
  public sealed class Envelope {
    public long seq;
    public long uid_user;
    public long uid_id;
    public long owner;
    public int owner_rev;
    public int data_rev;
    public int prefab;
    public float[] pos;
    public string body_b64;
  }

  public static PendingResponse Parse(string json) {
    if (string.IsNullOrWhiteSpace(json))
      throw new ArgumentException("pending envelope response is empty", nameof(json));
    Match schema = Regex.Match(json, "\\\"schema_version\\\"\\s*:\\s*(\\d+)");
    Match window = Regex.Match(json, "\\\"window_id\\\"\\s*:\\s*\\\"([^\\\"]*)\\\"");
    List<Envelope> envelopes = new();
    foreach (Match match in Regex.Matches(json, "\\{([^{}]*\\\"body_b64\\\"[^{}]*)\\}")) {
      string item = match.Value;
      Envelope envelope = new() {
          seq = ReadLong(item, "seq"), uid_user = ReadLong(item, "uid_user"),
          uid_id = ReadLong(item, "uid_id"), owner = ReadLong(item, "owner"),
          owner_rev = (int)ReadLong(item, "owner_rev"), data_rev = (int)ReadLong(item, "data_rev"),
          prefab = (int)ReadLong(item, "prefab"), body_b64 = ReadString(item, "body_b64"),
          pos = ReadPosition(item)
      };
      envelopes.Add(envelope);
    }
    PendingResponse response = new() {
        schema_version = schema.Success ? int.Parse(schema.Groups[1].Value, CultureInfo.InvariantCulture) : 0,
        window_id = window.Success ? window.Groups[1].Value : string.Empty,
        envelopes = envelopes.ToArray()
    };
    if (response == null || response.schema_version != 1 || response.envelopes == null)
      throw new InvalidOperationException("pending envelope response is malformed");
    return response;
  }

  static long ReadLong(string json, string name) {
    Match match = Regex.Match(json, "\\\"" + name + "\\\"\\s*:\\s*(-?\\d+)");
    if (!match.Success) throw new InvalidOperationException("missing " + name);
    return long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
  }

  static string ReadString(string json, string name) {
    Match match = Regex.Match(json, "\\\"" + name + "\\\"\\s*:\\s*\\\"([^\\\"]*)\\\"");
    if (!match.Success) throw new InvalidOperationException("missing " + name);
    return match.Groups[1].Value;
  }

  static float[] ReadPosition(string json) {
    Match match = Regex.Match(json, "\\\"pos\\\"\\s*:\\s*\\[([^]]+)\\]");
    if (!match.Success) throw new InvalidOperationException("missing pos");
    string[] values = match.Groups[1].Value.Split(',');
    if (values.Length != 3) throw new InvalidOperationException("pos must have three values");
    return new[] {
        float.Parse(values[0], CultureInfo.InvariantCulture),
        float.Parse(values[1], CultureInfo.InvariantCulture),
        float.Parse(values[2], CultureInfo.InvariantCulture)
    };
  }

  public static ZPackage BuildPacket(Envelope envelope) {
    if (envelope == null || envelope.uid_user == 0 || envelope.uid_id == 0
        || envelope.pos == null || envelope.pos.Length != 3 || string.IsNullOrWhiteSpace(envelope.body_b64))
      throw new InvalidOperationException("envelope is missing required ZDO fields");

    byte[] bodyBytes = Convert.FromBase64String(envelope.body_b64);
    ZPackage body = new(bodyBytes);
    ZPackage packet = new();
    packet.Write(0);
    packet.Write(new ZDOID(envelope.uid_user, (uint)envelope.uid_id));
    packet.Write((ushort)envelope.owner_rev);
    packet.Write((uint)envelope.data_rev);
    packet.Write(envelope.owner);
    packet.Write(new Vector3((float)envelope.pos[0], (float)envelope.pos[1], (float)envelope.pos[2]));
    packet.Write(body);
    packet.Write(ZDOID.None);
    packet.SetPos(0);
    return packet;
  }
}
