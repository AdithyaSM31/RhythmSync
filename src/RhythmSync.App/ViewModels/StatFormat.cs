using System.Globalization;

namespace RhythmSync.App.ViewModels;

/// <summary>Display formatting for statistics: a missing or non-finite value (NaN, infinity, null) is shown as a dash.</summary>
internal static class StatFormat
{
    public const string Unknown = "—";

    /// <summary>The value rounded to <paramref name="decimals"/> places, or a dash. Negative zero is shown as zero.</summary>
    public static string Number(double? value, int decimals)
        => value is { } v && double.IsFinite(v)
            ? (Math.Round(v, decimals) + 0.0).ToString("F" + decimals, CultureInfo.InvariantCulture)
            : Unknown;

    /// <summary>The number followed by a space and <paramref name="unit"/>, or a dash without a unit.</summary>
    public static string WithUnit(double? value, int decimals, string unit)
        => value is { } v && double.IsFinite(v) ? $"{Number(v, decimals)} {unit}" : Unknown;
}
