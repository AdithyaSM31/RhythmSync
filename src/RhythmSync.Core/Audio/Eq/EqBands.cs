namespace RhythmSync.Core.Audio.Eq;

/// <summary>Fixed layout of the 10-band graphic equaliser: octave-spaced centres from 31.25 Hz to 16 kHz.</summary>
public static class EqBands
{
    /// <summary>Number of bands.</summary>
    public const int Count = 10;

    /// <summary>Quality factor shared by every peaking band.</summary>
    public const double Q = 1.41;

    /// <summary>Largest boost or cut per band, in dB.</summary>
    public const float MaxGainDb = 12f;

    /// <summary>Gain resolution, in dB.</summary>
    public const float StepDb = 0.5f;

    /// <summary>Band centre frequencies in Hz: <c>1000 * 2^(k-5)</c>.</summary>
    public static IReadOnlyList<double> CentersHz { get; } =
        Array.AsReadOnly(Enumerable.Range(0, Count).Select(k => 1000.0 * Math.Pow(2, k - 5)).ToArray());

    /// <summary>Short display labels for the bands, lowest first.</summary>
    public static IReadOnlyList<string> Labels { get; } =
        Array.AsReadOnly(new[] { "31", "63", "125", "250", "500", "1k", "2k", "4k", "8k", "16k" });
}
