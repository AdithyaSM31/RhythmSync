using System.Text;
using RhythmSync.Core.Protocol;

namespace RhythmSync.App.Services;

/// <summary>Makes a session name fit the discovery beacon, which refuses names longer than 64 UTF-8 bytes.</summary>
public static class BeaconName
{
    public const string Fallback = "RhythmSync";

    /// <summary>
    /// The name trimmed and cut to at most <paramref name="maxBytes"/> UTF-8 bytes, cut between characters (a multibyte
    /// character or surrogate pair is dropped whole, never split). An empty name becomes <see cref="Fallback"/>.
    /// </summary>
    public static string Fit(string? name, int maxBytes = PacketSerializer.MaxBeaconNameBytes)
    {
        string trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0) return Fallback;
        if (Encoding.UTF8.GetByteCount(trimmed) <= maxBytes) return trimmed;

        var result = new StringBuilder();
        int bytes = 0;
        foreach (Rune rune in trimmed.EnumerateRunes())
        {
            int size = rune.Utf8SequenceLength;
            if (bytes + size > maxBytes) break;
            bytes += size;
            result.Append(rune.ToString());
        }

        string cut = result.ToString().TrimEnd();
        return cut.Length == 0 ? Fallback : cut;
    }
}
