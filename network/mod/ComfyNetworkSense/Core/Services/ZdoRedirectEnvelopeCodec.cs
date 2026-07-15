namespace ComfyNetworkSense;

using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

using UnityEngine;

/// <summary>Decodes an authoritative Lumberjacks ZDO envelope into Valheim's wire package shape.</summary>
public static class ZdoRedirectEnvelopeCodec {
  [Serializable]
  [DataContract]
  public sealed class PendingResponse {
    [DataMember] 
    public int schema_version;
    [DataMember]
    public string window_id;
    [DataMember]
    public Envelope[] envelopes;
  }

  [Serializable]
  [DataContract]
  public sealed class Envelope {
    [DataMember]
    public long seq;
    [DataMember]
    public long uid_user;
    [DataMember]
    public long uid_id;
    [DataMember]
    public long owner;
    [DataMember]
    public int owner_rev;
    [DataMember]
    public int data_rev;
    [DataMember]
    public int prefab;
    [DataMember]
    public float[] pos;
    [DataMember]
    public string body_b64;
  }

  public static PendingResponse Parse(string json) {
    if (string.IsNullOrWhiteSpace(json))
      throw new ArgumentException("pending envelope response is empty", nameof(json));
    DataContractJsonSerializer serializer = new(typeof(PendingResponse));
    PendingResponse response;
    using (MemoryStream stream = new(Encoding.UTF8.GetBytes(json))) {
      response = serializer.ReadObject(stream) as PendingResponse;
    }
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
