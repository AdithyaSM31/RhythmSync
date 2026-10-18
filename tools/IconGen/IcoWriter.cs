using System.Buffers.Binary;
using System.IO;

namespace IconGen;

/// <summary>
/// Writes a multi-frame .ico whose frames are PNG-compressed (Windows Vista and later): an ICONDIR, one
/// ICONDIRENTRY per frame, then the PNG payloads in the same order.
/// </summary>
internal static class IcoWriter
{
    private const int HeaderSize = 6;       // ICONDIR: reserved (2), type (2), count (2)
    private const int EntrySize = 16;       // ICONDIRENTRY

    public static byte[] Write(IReadOnlyList<(int Size, byte[] Png)> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count is 0 or > ushort.MaxValue)
            throw new ArgumentException("An icon needs between 1 and 65535 frames.", nameof(frames));

        int offset = HeaderSize + EntrySize * frames.Count;
        using var stream = new MemoryStream();
        var buffer = new byte[Math.Max(HeaderSize, EntrySize)];

        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(0), 0);                       // reserved
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(2), 1);                       // type 1 = icon
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), (ushort)frames.Count);
        stream.Write(buffer, 0, HeaderSize);

        foreach (var (size, png) in frames)
        {
            if (size is < 1 or > 256)
                throw new ArgumentException($"Frame size {size} is outside 1..256.", nameof(frames));

            buffer[0] = (byte)(size == 256 ? 0 : size);                                      // width, 0 means 256
            buffer[1] = (byte)(size == 256 ? 0 : size);                                      // height
            buffer[2] = 0;                                                                   // colour count (0: not palettised)
            buffer[3] = 0;                                                                   // reserved
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), 1);                   // colour planes
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(6), 32);                  // bits per pixel
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), (uint)png.Length);    // payload bytes
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12), (uint)offset);       // payload offset
            stream.Write(buffer, 0, EntrySize);
            offset += png.Length;
        }

        foreach (var (_, png) in frames)
            stream.Write(png, 0, png.Length);

        return stream.ToArray();
    }
}
