using System.Buffers.Binary;
using System.Text;
using System.Text.Unicode;

namespace RhythmSync.Core.Protocol;

public readonly record struct PingPacket(uint SessionId, uint PingId, long T1);

public readonly record struct PongPacket(uint SessionId, uint PingId, long T1, long T2, long T3);

/// <summary>
/// Little-endian wire format for the UDP packets (spec section 4.1). Audio, ping and pong paths do not
/// allocate; beacons materialise the name string on read (discovery only, not the audio path).
/// </summary>
public static class PacketSerializer
{
    public const ushort Magic = 0x5253;
    public const byte Version = 1;

    public const int HeaderBytes = 8;
    public const int AudioPacketBytes = 982;
    public const int PingBytes = 20;
    public const int PongBytes = 36;
    public const int MaxBeaconNameBytes = 64;

    private const int BeaconFixedBytes = HeaderBytes + 4;

    public static int WriteAudio(Span<byte> dst, uint sessionId, uint seq, long playAt, ReadOnlySpan<short> pcm)
    {
        if (pcm.Length != AudioFormat.FrameFloats)
            throw new ArgumentException($"pcm must contain exactly {AudioFormat.FrameFloats} samples.", nameof(pcm));
        if (dst.Length < AudioPacketBytes)
            throw new ArgumentException($"dst must hold at least {AudioPacketBytes} bytes.", nameof(dst));

        WriteHeader(dst, PacketType.Audio, sessionId);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[8..], seq);
        BinaryPrimitives.WriteInt64LittleEndian(dst[12..], playAt);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[20..], AudioFormat.FrameSamples);
        var body = dst[22..];
        for (int i = 0; i < pcm.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(body[(i * 2)..], pcm[i]);
        return AudioPacketBytes;
    }

    public static int WritePing(Span<byte> dst, in PingPacket p)
    {
        if (dst.Length < PingBytes)
            throw new ArgumentException($"dst must hold at least {PingBytes} bytes.", nameof(dst));

        WriteHeader(dst, PacketType.Ping, p.SessionId);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[8..], p.PingId);
        BinaryPrimitives.WriteInt64LittleEndian(dst[12..], p.T1);
        return PingBytes;
    }

    public static int WritePong(Span<byte> dst, in PongPacket p)
    {
        if (dst.Length < PongBytes)
            throw new ArgumentException($"dst must hold at least {PongBytes} bytes.", nameof(dst));

        WriteHeader(dst, PacketType.Pong, p.SessionId);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[8..], p.PingId);
        BinaryPrimitives.WriteInt64LittleEndian(dst[12..], p.T1);
        BinaryPrimitives.WriteInt64LittleEndian(dst[20..], p.T2);
        BinaryPrimitives.WriteInt64LittleEndian(dst[28..], p.T3);
        return PongBytes;
    }

    public static int WriteBeacon(Span<byte> dst, BeaconPacket b)
    {
        ArgumentNullException.ThrowIfNull(b);
        int nameBytes = Encoding.UTF8.GetByteCount(b.Name);
        if (nameBytes > MaxBeaconNameBytes)
            throw new ArgumentException(
                $"Beacon name is {nameBytes} UTF-8 bytes; the maximum is {MaxBeaconNameBytes}.", nameof(b));
        int total = BeaconFixedBytes + nameBytes;
        if (dst.Length < total)
            throw new ArgumentException($"dst must hold at least {total} bytes.", nameof(dst));

        WriteHeader(dst, PacketType.Beacon, b.SessionId);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[8..], b.ControlPort);
        dst[10] = b.ProtocolVersion;
        dst[11] = (byte)nameBytes;
        Encoding.UTF8.GetBytes(b.Name, dst[BeaconFixedBytes..]);
        return total;
    }

    public static bool TryReadHeader(ReadOnlySpan<byte> src, out PacketType type, out uint sessionId)
    {
        type = default;
        sessionId = 0;
        if (src.Length < HeaderBytes
            || BinaryPrimitives.ReadUInt16LittleEndian(src) != Magic
            || src[2] != Version
            || !Enum.IsDefined((PacketType)src[3]))
            return false;

        type = (PacketType)src[3];
        sessionId = BinaryPrimitives.ReadUInt32LittleEndian(src[4..]);
        return true;
    }

    public static bool TryReadAudio(ReadOnlySpan<byte> src, out uint seq, out long playAt, Span<short> pcmDst)
    {
        seq = 0;
        playAt = 0;
        if (src.Length != AudioPacketBytes
            || pcmDst.Length != AudioFormat.FrameFloats
            || !HasHeader(src, PacketType.Audio)
            || BinaryPrimitives.ReadUInt16LittleEndian(src[20..]) != AudioFormat.FrameSamples)
            return false;

        seq = BinaryPrimitives.ReadUInt32LittleEndian(src[8..]);
        playAt = BinaryPrimitives.ReadInt64LittleEndian(src[12..]);
        var body = src[22..];
        for (int i = 0; i < pcmDst.Length; i++)
            pcmDst[i] = BinaryPrimitives.ReadInt16LittleEndian(body[(i * 2)..]);
        return true;
    }

    public static bool TryReadPing(ReadOnlySpan<byte> src, out PingPacket p)
    {
        p = default;
        if (src.Length != PingBytes || !HasHeader(src, PacketType.Ping))
            return false;

        p = new PingPacket(
            BinaryPrimitives.ReadUInt32LittleEndian(src[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(src[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(src[12..]));
        return true;
    }

    public static bool TryReadPong(ReadOnlySpan<byte> src, out PongPacket p)
    {
        p = default;
        if (src.Length != PongBytes || !HasHeader(src, PacketType.Pong))
            return false;

        p = new PongPacket(
            BinaryPrimitives.ReadUInt32LittleEndian(src[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(src[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(src[12..]),
            BinaryPrimitives.ReadInt64LittleEndian(src[20..]),
            BinaryPrimitives.ReadInt64LittleEndian(src[28..]));
        return true;
    }

    public static bool TryReadBeacon(ReadOnlySpan<byte> src, out BeaconPacket? b)
    {
        b = null;
        if (src.Length < BeaconFixedBytes || !HasHeader(src, PacketType.Beacon))
            return false;

        int nameLen = src[11];
        if (nameLen > MaxBeaconNameBytes || src.Length != BeaconFixedBytes + nameLen)
            return false;

        var nameBytes = src.Slice(BeaconFixedBytes, nameLen);
        if (!Utf8.IsValid(nameBytes))
            return false;

        b = new BeaconPacket(
            BinaryPrimitives.ReadUInt32LittleEndian(src[4..]),
            BinaryPrimitives.ReadUInt16LittleEndian(src[8..]),
            src[10],
            Encoding.UTF8.GetString(nameBytes));
        return true;
    }

    private static void WriteHeader(Span<byte> dst, PacketType type, uint sessionId)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(dst, Magic);
        dst[2] = Version;
        dst[3] = (byte)type;
        BinaryPrimitives.WriteUInt32LittleEndian(dst[4..], sessionId);
    }

    private static bool HasHeader(ReadOnlySpan<byte> src, PacketType expected) =>
        TryReadHeader(src, out var type, out _) && type == expected;
}
