namespace RhythmSync.Core.Audio.Eq;

/// <summary>
/// Immutable set of ten band gains in dB. Every gain is clamped to ±<see cref="EqBands.MaxGainDb"/> and rounded
/// to <see cref="EqBands.StepDb"/> (midpoints away from zero). Equality is by value.
/// </summary>
public sealed class EqGains : IEquatable<EqGains>
{
    private readonly float[] _gains;

    private EqGains(float[] snapped) => _gains = snapped;

    /// <summary>All bands at 0 dB.</summary>
    public static EqGains Flat { get; } = new(new float[EqBands.Count]);

    /// <summary>Gain of <paramref name="band"/> in dB.</summary>
    public float this[int band] => _gains[band];

    /// <summary>True when every band is 0 dB.</summary>
    public bool IsFlat => Array.TrueForAll(_gains, g => g == 0f);

    /// <summary>Creates gains from exactly ten finite values, clamping and rounding each.</summary>
    /// <exception cref="ArgumentException">Wrong length, or a value is NaN or infinite.</exception>
    public static EqGains Create(ReadOnlySpan<float> gainsDb)
    {
        if (gainsDb.Length != EqBands.Count)
            throw new ArgumentException($"Expected {EqBands.Count} gains, got {gainsDb.Length}.", nameof(gainsDb));
        var snapped = new float[EqBands.Count];
        for (int i = 0; i < snapped.Length; i++)
        {
            if (!float.IsFinite(gainsDb[i]))
                throw new ArgumentException($"Gain {i} is not a finite number.", nameof(gainsDb));
            snapped[i] = Snap(gainsDb[i]);
        }
        return new EqGains(snapped);
    }

    /// <summary>Like <see cref="Create"/> but reports failure instead of throwing. A null array yields <see cref="Flat"/>.</summary>
    public static bool TryCreate(float[]? gainsDb, out EqGains eq)
    {
        if (gainsDb is null)
        {
            eq = Flat;
            return true;
        }
        if (gainsDb.Length != EqBands.Count || !Array.TrueForAll(gainsDb, float.IsFinite))
        {
            eq = Flat;
            return false;
        }
        eq = Create(gainsDb);
        return true;
    }

    /// <summary>Reads gains from persisted data; anything invalid becomes <see cref="Flat"/>.</summary>
    public static EqGains FromStored(float[]? gainsDb) => TryCreate(gainsDb, out var eq) ? eq : Flat;

    /// <summary>Returns a copy with one band changed (clamped and rounded).</summary>
    public EqGains WithBand(int band, double gainDb)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(band);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(band, EqBands.Count);
        if (!double.IsFinite(gainDb)) throw new ArgumentException("Gain is not a finite number.", nameof(gainDb));
        var copy = (float[])_gains.Clone();
        copy[band] = Snap((float)gainDb);
        return new EqGains(copy);
    }

    /// <summary>A copy of the ten gains in dB.</summary>
    public float[] ToArray() => (float[])_gains.Clone();

    public bool Equals(EqGains? other) => other is not null && _gains.AsSpan().SequenceEqual(other._gains);

    public override bool Equals(object? obj) => obj is EqGains other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (float g in _gains) hash.Add(g);
        return hash.ToHashCode();
    }

    public static bool operator ==(EqGains? left, EqGains? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(EqGains? left, EqGains? right) => !(left == right);

    // "+ 0f" turns -0 into +0 so equal gains always hash alike.
    private static float Snap(float db) =>
        MathF.Round(Math.Clamp(db, -EqBands.MaxGainDb, EqBands.MaxGainDb) / EqBands.StepDb, MidpointRounding.AwayFromZero)
        * EqBands.StepDb + 0f;
}
