namespace ComfyNetworkSense;

using System;

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
    PendingResponse response = JsonUtility.FromJson<PendingResponse>(json);
    if (response == null || response.schema_version != 1 || response.envelopes == null)
      throw new InvalidOperationException("pending envelope response is malformed");
    return response;
  }

  public static ZPackage BuildPacket(Envelope envelope) {
    if (envelope == null || envelope.uid_user == 0 || envelope.uid_id == 0
        || envelope.owner == 0 || envelope.owner_rev == 0 || envelope.data_rev == 0
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
