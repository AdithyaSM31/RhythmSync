using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RhythmSync.Core.Protocol;

/// <summary>Frames <see cref="ControlMessage"/>s on a stream: u32 little-endian length + UTF-8 JSON body.</summary>
public static class ControlCodec
{
    public const int MaxMessageBytes = 65536;

    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        // Computed stats can be NaN/Infinity (0/0 ratios, unwarmed estimator); let them round-trip.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static async Task WriteAsync(Stream s, ControlMessage m, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(m, Options);
        if (body.Length > MaxMessageBytes)
            throw new InvalidDataException($"Control message too large: {body.Length} bytes.");

        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        await s.WriteAsync(frame, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <returns>The next message, or null on clean EOF before a length prefix.</returns>
    /// <exception cref="InvalidDataException">Oversize, truncated, or malformed frame.</exception>
    public static async Task<ControlMessage?> ReadAsync(Stream s, CancellationToken ct)
    {
        var prefix = new byte[4];
        var got = await ReadFullAsync(s, prefix, ct).ConfigureAwait(false);
        if (got == 0) return null;
        if (got < prefix.Length) throw new InvalidDataException("Truncated control message length prefix.");

        var length = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        if (length > MaxMessageBytes)
            throw new InvalidDataException($"Control message length {length} exceeds {MaxMessageBytes}.");

        var body = new byte[length];
        if (await ReadFullAsync(s, body, ct).ConfigureAwait(false) < body.Length)
            throw new InvalidDataException("Truncated control message body.");

        try
        {
            return JsonSerializer.Deserialize<ControlMessage>(body, Options)
                   ?? throw new InvalidDataException("Control message body is null.");
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            throw new InvalidDataException("Malformed control message.", e);
        }
    }

    private static async Task<int> ReadFullAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await s.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
