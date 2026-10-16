using System.Buffers.Binary;
using RhythmSync.Core.Protocol;

namespace RhythmSync.Tests.Protocol;

public class PacketSerializerTests
{
    private static byte[] ValidAudio()
    {
        var pcm = Enumerable.Range(0, 480).Select(i => (short)(i * 37 - 8000)).ToArray();
        var buf = new byte[PacketSerializer.AudioPacketBytes];
        PacketSerializer.WriteAudio(buf, 0xA1B2C3D4, 77, 123_456_789_012, pcm);
        return buf;
    }

    [Fact]
    public void Audio_round_trips()
    {
        var pcm = Enumerable.Range(0, 480).Select(i => (short)(i * 37 - 8000)).ToArray();
        var buf = new byte[1500];
        Assert.Equal(982, PacketSerializer.WriteAudio(buf, 0xA1B2C3D4, 77, 123_456_789_012, pcm));
        Assert.True(PacketSerializer.TryReadHeader(buf.AsSpan(0, 982), out var t, out var sid));
        Assert.Equal(PacketType.Audio, t);
        Assert.Equal(0xA1B2C3D4u, sid);
        var outPcm = new short[480];
        Assert.True(PacketSerializer.TryReadAudio(buf.AsSpan(0, 982), out var seq, out var playAt, outPcm));
        Assert.Equal(77u, seq);
        Assert.Equal(123_456_789_012, playAt);
        Assert.Equal(pcm, outPcm);
    }

    [Fact]
    public void Ping_round_trips()
    {
        var buf = new byte[64];
        var ping = new PingPacket(0xDEADBEEF, 42, 9_876_543_210_123);
        Assert.Equal(20, PacketSerializer.WritePing(buf, ping));
        Assert.True(PacketSerializer.TryReadHeader(buf.AsSpan(0, 20), out var t, out var sid));
        Assert.Equal(PacketType.Ping, t);
        Assert.Equal(0xDEADBEEFu, sid);
        Assert.True(PacketSerializer.TryReadPing(buf.AsSpan(0, 20), out var read));
        Assert.Equal(ping, read);
    }

    [Fact]
    public void Pong_round_trips()
    {
        var buf = new byte[64];
        var pong = new PongPacket(0xCAFEF00D, 7, -5, 123_456_789_012_345, long.MaxValue);
        Assert.Equal(36, PacketSerializer.WritePong(buf, pong));
        Assert.True(PacketSerializer.TryReadHeader(buf.AsSpan(0, 36), out var t, out var sid));
        Assert.Equal(PacketType.Pong, t);
        Assert.Equal(0xCAFEF00Du, sid);
        Assert.True(PacketSerializer.TryReadPong(buf.AsSpan(0, 36), out var read));
        Assert.Equal(pong, read);
    }

    [Fact]
    public void Beacon_round_trips()
    {
        var buf = new byte[200];
        var beacon = new BeaconPacket(0x01020304, 47801, 1, "Living Room PC");
        int n = PacketSerializer.WriteBeacon(buf, beacon);
        Assert.Equal(12 + "Living Room PC".Length, n);
        Assert.True(PacketSerializer.TryReadHeader(buf.AsSpan(0, n), out var t, out var sid));
        Assert.Equal(PacketType.Beacon, t);
        Assert.Equal(0x01020304u, sid);
        Assert.True(PacketSerializer.TryReadBeacon(buf.AsSpan(0, n), out var read));
        Assert.Equal(beacon, read);
    }

    [Fact]
    public void Beacon_with_non_ascii_name_round_trips()
    {
        var buf = new byte[200];
        var beacon = new BeaconPacket(9, 47801, 1, new string('é', 32)); // exactly 64 UTF-8 bytes
        int n = PacketSerializer.WriteBeacon(buf, beacon);
        Assert.Equal(12 + 64, n);
        Assert.True(PacketSerializer.TryReadBeacon(buf.AsSpan(0, n), out var read));
        Assert.Equal(beacon, read);
    }

    [Fact]
    public void Beacon_name_over_64_bytes_throws() => Assert.Throws<ArgumentException>(() =>
        PacketSerializer.WriteBeacon(new byte[200], new BeaconPacket(1, 47801, 1, new string('é', 33))));

    [Theory] // each must return false, never throw
    [InlineData("bad-magic")]
    [InlineData("version-2")]
    [InlineData("truncated-981")]
    [InlineData("framecount-239")]
    [InlineData("empty")]
    public void Malformed_audio_rejected(string kind)
    {
        var buf = ValidAudio();
        var pcm = new short[480];
        // sanity: the uncorrupted packet is accepted
        Assert.True(PacketSerializer.TryReadAudio(buf, out _, out _, pcm));

        ReadOnlySpan<byte> src;
        switch (kind)
        {
            case "bad-magic":
                BinaryPrimitives.WriteUInt16LittleEndian(buf, 0x5254);
                src = buf;
                Assert.False(PacketSerializer.TryReadHeader(src, out _, out _));
                break;
            case "version-2":
                buf[2] = 2;
                src = buf;
                Assert.False(PacketSerializer.TryReadHeader(src, out _, out _));
                break;
            case "truncated-981":
                src = buf.AsSpan(0, 981);
                break;
            case "framecount-239":
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(20), 239);
                src = buf;
                Assert.True(PacketSerializer.TryReadHeader(src, out _, out _)); // header itself is fine
                break;
            case "empty":
                src = ReadOnlySpan<byte>.Empty;
                Assert.False(PacketSerializer.TryReadHeader(src, out _, out _));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        Assert.False(PacketSerializer.TryReadAudio(src, out _, out _, pcm));
    }

    [Fact]
    public void Wrong_type_and_wrong_pcm_buffer_rejected()
    {
        var audio = ValidAudio();
        Assert.False(PacketSerializer.TryReadPing(audio, out _));
        Assert.False(PacketSerializer.TryReadPong(audio, out _));
        Assert.False(PacketSerializer.TryReadBeacon(audio, out _));
        Assert.False(PacketSerializer.TryReadAudio(audio, out _, out _, new short[479]));

        var ping = new byte[20];
        PacketSerializer.WritePing(ping, new PingPacket(1, 2, 3));
        Assert.False(PacketSerializer.TryReadAudio(ping, out _, out _, new short[480]));
    }

    [Fact]
    public void Beacon_with_inconsistent_length_or_invalid_utf8_rejected()
    {
        var buf = new byte[200];
        int n = PacketSerializer.WriteBeacon(buf, new BeaconPacket(1, 47801, 1, "abc"));
        Assert.False(PacketSerializer.TryReadBeacon(buf.AsSpan(0, n - 1), out _));
        Assert.False(PacketSerializer.TryReadBeacon(buf.AsSpan(0, n + 1), out _));

        buf[12] = 0xFF; // invalid UTF-8 lead byte
        Assert.False(PacketSerializer.TryReadBeacon(buf.AsSpan(0, n), out _));

        buf[12] = (byte)'a';
        buf[11] = 65; // nameLen above the maximum
        Assert.False(PacketSerializer.TryReadBeacon(buf.AsSpan(0, 12 + 65), out _));
    }

    [Fact]
    public void Random_garbage_never_throws()
    {
        var rng = new Random(1234);
        var pcm = new short[480];
        for (int i = 0; i < 10_000; i++)
        {
            var b = new byte[rng.Next(0, 1201)];
            rng.NextBytes(b);
            PacketSerializer.TryReadHeader(b, out _, out _);
            PacketSerializer.TryReadAudio(b, out _, out _, pcm);
            PacketSerializer.TryReadPing(b, out _);
            PacketSerializer.TryReadPong(b, out _);
            PacketSerializer.TryReadBeacon(b, out _);
        }
    }
}
