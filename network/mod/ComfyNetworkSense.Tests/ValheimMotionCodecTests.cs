namespace ComfyNetworkSense.Tests;

using System;
using Xunit;

public sealed class ValheimMotionCodecTests {
  [Fact]
  public void UdpPacketRoundTripsAtFixedFiftyBytes() {
    ValheimMotionSnapshot source = new(
        -9876543210, 4242, 123.456f, -7.891f, -432.109f,
        6.789f, -1.234f, 0.125f, -45.0f, 123456789);

    byte[] packet = ValheimMotionCodec.BuildUdpPacket(0x0102030405060708, 65534, source);
    Assert.Equal(50, packet.Length);
    Assert.Equal(
        "080706050403020114bfffc00480fffffffdb34fe916000010920000303afffffcebffff573502a7ff85000c0c4e075bcd15",
        Convert.ToHexString(packet).ToLowerInvariant());
    Assert.True(ValheimMotionCodec.TryRead(packet, tokenPrefixed: true, out ushort sequence, out ValheimMotionSnapshot parsed));
    Assert.Equal(65534, sequence);
    Assert.Equal(source.ZdoUserId, parsed.ZdoUserId);
    Assert.Equal(source.ZdoId, parsed.ZdoId);
    Assert.Equal(123.46f, parsed.X, 2);
    Assert.Equal(-7.89f, parsed.Y, 2);
    Assert.Equal(-432.11f, parsed.Z, 2);
    Assert.Equal(6.79f, parsed.VelocityX, 2);
    Assert.Equal(315.0f, parsed.Yaw, 1);
    Assert.Equal(source.SentMilliseconds, parsed.SentMilliseconds);
  }

  [Fact]
  public void SequenceComparisonHandlesWrapAndRejectsOldPackets() {
    Assert.True(ValheimMotionCodec.IsNewer(65535, 65534));
    Assert.True(ValheimMotionCodec.IsNewer(0, 65535));
    Assert.False(ValheimMotionCodec.IsNewer(65535, 0));
    Assert.False(ValheimMotionCodec.IsNewer(10, 10));
  }
}
