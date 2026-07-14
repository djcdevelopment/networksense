namespace ComfyNetworkSense;

using System;

using UnityEngine;

/// <summary>Decodes an authoritative Lumberjacks ZDO envelope into Valheim's wire package shape.</summary>
public static class ZdoRedirectEnvelopeCodec {
  [Serializable]
  public sealed class PendingResponse {
    public string window_id;
    public Envelope[] envelopes;
  }

  [Serializable]
  public sealed class Envelope {
    public long? seq;
    public long? uid_user;
    public long? uid_id;
    public long? owner;
    public int? owner_rev;
    public int? data_rev;
    public int? prefab;
    public double[] pos;
    public string body_b64;
  }

  public static PendingResponse Parse(string json) {
    if (string.IsNullOrWhiteSpace(json))
      throw new ArgumentException("pending envelope response is empty", nameof(json));
    PendingResponse response = JsonUtility.FromJson<PendingResponse>(json);
    if (response == null || response.envelopes == null)
      throw new InvalidOperationException("pending envelope response is malformed");
    return response;
  }

  public static ZPackage BuildPacket(Envelope envelope) {
    if (envelope == null || envelope.uid_user is null || envelope.uid_id is null
        || envelope.owner is null || envelope.owner_rev is null || envelope.data_rev is null
        || envelope.pos == null || envelope.pos.Length != 3 || string.IsNullOrWhiteSpace(envelope.body_b64))
      throw new InvalidOperationException("envelope is missing required ZDO fields");

    byte[] bodyBytes = Convert.FromBase64String(envelope.body_b64);
    ZPackage body = new(bodyBytes);
    ZPackage packet = new();
    packet.Write(0);
    packet.Write(new ZDOID(envelope.uid_user.Value, (uint)envelope.uid_id.Value));
    packet.Write((ushort)envelope.owner_rev.Value);
    packet.Write((uint)envelope.data_rev.Value);
    packet.Write(envelope.owner.Value);
    packet.Write(new Vector3((float)envelope.pos[0], (float)envelope.pos[1], (float)envelope.pos[2]));
    packet.Write(body);
    packet.Write(ZDOID.None);
    packet.SetPos(0);
    return packet;
  }
}
