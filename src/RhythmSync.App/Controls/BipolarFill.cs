using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RhythmSync.App.Controls;

/// <summary>
/// Geometry of the fill of a bipolar slider such as an EQ band: a bar from the middle of the range to the thumb.
/// Coordinates are top-down (0 is the top of the track, where a vertical slider's maximum is).
/// </summary>
public static class BipolarFill
{
    /// <summary>
    /// The fill's top edge and height on a track <paramref name="trackLength"/> long. A zero or invalid length,
    /// a degenerate range or a NaN value gives (0, 0); a value outside the range counts as the nearest end.
    /// </summary>
    public static (double Top, double Height) Compute(double value, double min, double max, double trackLength)
    {
        if (double.IsNaN(value) || double.IsNaN(min) || double.IsNaN(max) || double.IsNaN(trackLength)
            || double.IsInfinity(trackLength) || trackLength <= 0 || !(max > min))
            return (0, 0);

        double span = max - min;
        double clamped = Math.Clamp(value, min, max);
        double thumb = (max - clamped) / span * trackLength;
        double middle = trackLength / 2;
        return (Math.Min(thumb, middle), Math.Abs(thumb - middle));
    }
}

/// <summary>
/// For the EQ band slider's template: (Value, Minimum, Maximum, ActualHeight) become the margin of a stretched
/// fill bar. The ConverterParameter is the thumb size: the thumb's centre travels over the track minus one thumb,
/// starting half a thumb down, and the fill must follow the centre.
/// </summary>
public sealed class BipolarFillConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 4 || values[0] is not double value || values[1] is not double min
            || values[2] is not double max || values[3] is not double height)
            return new Thickness(0);

        double thumb = parameter is null ? 0 : double.Parse(System.Convert.ToString(parameter, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
        var (top, fill) = BipolarFill.Compute(value, min, max, height - thumb);
        if (fill <= 0) return new Thickness(0, height / 2, 0, height / 2);
        double offset = thumb / 2 + top;
        return new Thickness(0, offset, 0, Math.Max(0, height - offset - fill));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
