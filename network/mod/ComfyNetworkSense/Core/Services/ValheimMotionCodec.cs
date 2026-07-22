namespace ComfyNetworkSense;

using System;

/// <summary>Unity-free mirror of Lumberjacks' 36-byte Valheim motion payload.</summary>
public static class ValheimMotionCodec {
  public const int TokenBytes = 8;
  public const int HeaderBytes = 6;
  public const int PayloadBytes = 36;
  public const int FrameBytes = HeaderBytes + PayloadBytes;
  public const int PacketBytes = TokenBytes + FrameBytes;
  public const int MessageTypeId = 18;

  public static byte[] BuildUdpPacket(ulong token, ushort sequence, ValheimMotionSnapshot snapshot) {
    byte[] packet = new byte[PacketBytes];
    Array.Copy(BitConverter.GetBytes(token), 0, packet, 0, TokenBytes);
    WriteFrame(packet, TokenBytes, sequence, snapshot);
    return packet;
  }

  public static byte[] BuildWebSocketFrame(ushort sequence, ValheimMotionSnapshot snapshot) {
    byte[] frame = new byte[FrameBytes];
    WriteFrame(frame, 0, sequence, snapshot);
    return frame;
  }

  public static bool TryRead(byte[] data, bool tokenPrefixed, out ushort sequence, out ValheimMotionSnapshot snapshot) {
    sequence = 0;
    snapshot = default;
    int offset = tokenPrefixed ? TokenBytes : 0;
    if (data == null || data.Length != offset + FrameBytes) return false;

    ulong header = 0;
    for (int i = 0; i < HeaderBytes; i++) header = (header << 8) | data[offset + i];
    int version = (int) ((header >> 44) & 0xF);
    int type = (int) ((header >> 38) & 0x3F);
    bool datagram = ((header >> 37) & 1) != 0;
    sequence = (ushort) ((header >> 21) & 0xFFFF);
    int payloadLength = (int) ((header >> 5) & 0xFFFF);
    if (version != 1 || type != MessageTypeId || !datagram || payloadLength != PayloadBytes) return false;

    int p = offset + HeaderBytes;
    ulong userBits = ((ulong) ReadUInt32(data, ref p) << 32) | ReadUInt32(data, ref p);
    uint zdoId = ReadUInt32(data, ref p);
    float x = ReadInt32(data, ref p) / 100.0f;
    float y = ReadInt32(data, ref p) / 100.0f;
    float z = ReadInt32(data, ref p) / 100.0f;
    float vx = ReadInt16(data, ref p) / 100.0f;
    float vy = ReadInt16(data, ref p) / 100.0f;
    float vz = ReadInt16(data, ref p) / 100.0f;
    float yaw = ReadUInt16(data, ref p) / 10.0f;
    uint sentMilliseconds = ReadUInt32(data, ref p);
    snapshot = new(
        unchecked((long) userBits), zdoId, x, y, z, vx, vy, vz, yaw, sentMilliseconds);
    return true;
  }

  public static bool IsNewer(ushort candidate, ushort previous) {
    ushort delta = unchecked((ushort) (candidate - previous));
    return delta != 0 && delta < 0x8000;
  }

  static void WriteFrame(byte[] target, int offset, ushort sequence, ValheimMotionSnapshot snapshot) {
    ulong header = (1UL << 44)
        | ((ulong) MessageTypeId << 38)
        | (1UL << 37)
        | ((ulong) sequence << 21)
        | ((ulong) PayloadBytes << 5);
    for (int i = HeaderBytes - 1; i >= 0; i--) {
      target[offset + i] = (byte) header;
      header >>= 8;
    }

    int p = offset + HeaderBytes;
    ulong userBits = unchecked((ulong) snapshot.ZdoUserId);
    WriteUInt32(target, ref p, (uint) (userBits >> 32));
    WriteUInt32(target, ref p, (uint) userBits);
    WriteUInt32(target, ref p, snapshot.ZdoId);
    WriteInt32(target, ref p, ToCentimetres(snapshot.X));
    WriteInt32(target, ref p, ToCentimetres(snapshot.Y));
    WriteInt32(target, ref p, ToCentimetres(snapshot.Z));
    WriteInt16(target, ref p, ToCentimetresPerSecond(snapshot.VelocityX));
    WriteInt16(target, ref p, ToCentimetresPerSecond(snapshot.VelocityY));
    WriteInt16(target, ref p, ToCentimetresPerSecond(snapshot.VelocityZ));
    float yaw = ((snapshot.Yaw % 360.0f) + 360.0f) % 360.0f;
    WriteUInt16(target, ref p, (ushort) Math.Round(yaw * 10.0f));
    WriteUInt32(target, ref p, snapshot.SentMilliseconds);
  }

  static int ToCentimetres(float value) {
    double scaled = Math.Round(value * 100.0);
    if (scaled < int.MinValue) return int.MinValue;
    if (scaled > int.MaxValue) return int.MaxValue;
    return (int) scaled;
  }

  static short ToCentimetresPerSecond(float value) {
    double scaled = Math.Round(value * 100.0);
    if (scaled < short.MinValue) return short.MinValue;
    if (scaled > short.MaxValue) return short.MaxValue;
    return (short) scaled;
  }

  static void WriteUInt16(byte[] data, ref int p, ushort value) {
    data[p++] = (byte) (value >> 8); data[p++] = (byte) value;
  }
  static void WriteInt16(byte[] data, ref int p, short value) => WriteUInt16(data, ref p, unchecked((ushort) value));
  static void WriteUInt32(byte[] data, ref int p, uint value) {
    data[p++] = (byte) (value >> 24); data[p++] = (byte) (value >> 16);
    data[p++] = (byte) (value >> 8); data[p++] = (byte) value;
  }
  static void WriteInt32(byte[] data, ref int p, int value) => WriteUInt32(data, ref p, unchecked((uint) value));
  static ushort ReadUInt16(byte[] data, ref int p) => (ushort) ((data[p++] << 8) | data[p++]);
  static short ReadInt16(byte[] data, ref int p) => unchecked((short) ReadUInt16(data, ref p));
  static uint ReadUInt32(byte[] data, ref int p) =>
      ((uint) data[p++] << 24) | ((uint) data[p++] << 16) | ((uint) data[p++] << 8) | data[p++];
  static int ReadInt32(byte[] data, ref int p) => unchecked((int) ReadUInt32(data, ref p));
}

public readonly struct ValheimMotionSnapshot {
  public readonly long ZdoUserId;
  public readonly uint ZdoId;
  public readonly float X;
  public readonly float Y;
  public readonly float Z;
  public readonly float VelocityX;
  public readonly float VelocityY;
  public readonly float VelocityZ;
  public readonly float Yaw;
  public readonly uint SentMilliseconds;

  public ValheimMotionSnapshot(long zdoUserId, uint zdoId, float x, float y, float z,
      float velocityX, float velocityY, float velocityZ, float yaw, uint sentMilliseconds) {
    ZdoUserId = zdoUserId; ZdoId = zdoId; X = x; Y = y; Z = z;
    VelocityX = velocityX; VelocityY = velocityY; VelocityZ = velocityZ;
    Yaw = yaw; SentMilliseconds = sentMilliseconds;
  }
}
