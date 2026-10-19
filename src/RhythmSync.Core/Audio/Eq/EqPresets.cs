namespace RhythmSync.Core.Audio.Eq;

/// <summary>The built-in EQ presets, in the order the UI lists them.</summary>
public static class EqPresets
{
    /// <summary>Name shown when the gains match no preset.</summary>
    public const string CustomName = "Custom";

    /// <summary>Presets from 31 Hz up to 16 kHz.</summary>
    public static IReadOnlyList<(string Name, EqGains Gains)> All { get; } = new (string Name, EqGains Gains)[]
    {
        ("Flat", EqGains.Flat),
        ("Laptop speakers", EqGains.Create(new float[] { -9, -6, -1, 2, 1, 0, 0, 1, 2, 1 })),
        ("Bass boost", EqGains.Create(new float[] { 6, 5, 4, 2, 0, 0, 0, 0, 0, 0 })),
        ("Treble cut", EqGains.Create(new float[] { 0, 0, 0, 0, 0, 0, -1, -3, -5, -6 })),
    };

    /// <summary>The name of the preset equal to <paramref name="gains"/>, or null when it is a custom curve.</summary>
    public static string? NameOf(EqGains gains)
    {
        foreach (var (name, preset) in All)
        {
            if (preset == gains) return name;
        }
        return null;
    }
}
