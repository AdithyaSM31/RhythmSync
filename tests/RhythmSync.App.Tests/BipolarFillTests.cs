using System.Globalization;
using System.Windows;
using RhythmSync.App.Controls;

namespace RhythmSync.App.Tests;

/// <summary>The bipolar fill of the EQ band slider: it spans from the middle of the track to the thumb, top-down.</summary>
public class BipolarFillTests
{
    [Fact]
    public void Zero_gain_has_no_fill()
    {
        var (_, height) = BipolarFill.Compute(0, -12, 12, 120);
        Assert.Equal(0, height);
    }

    [Fact]
    public void A_full_boost_fills_the_upper_half()
    {
        Assert.Equal((0.0, 60.0), BipolarFill.Compute(12, -12, 12, 120));
    }

    [Fact]
    public void A_full_cut_fills_the_lower_half()
    {
        Assert.Equal((60.0, 60.0), BipolarFill.Compute(-12, -12, 12, 120));
    }

    [Fact]
    public void A_partial_boost_fills_from_the_thumb_down_to_the_middle()
    {
        Assert.Equal((30.0, 30.0), BipolarFill.Compute(6, -12, 12, 120));
        Assert.Equal((60.0, 30.0), BipolarFill.Compute(-6, -12, 12, 120));
    }

    [Fact]
    public void A_zero_length_track_has_no_fill()
    {
        Assert.Equal((0.0, 0.0), BipolarFill.Compute(12, -12, 12, 0));
        Assert.Equal((0.0, 0.0), BipolarFill.Compute(12, -12, 12, -5));
        Assert.Equal((0.0, 0.0), BipolarFill.Compute(12, -12, 12, double.NaN));
    }

    [Fact]
    public void A_value_outside_the_range_is_clamped_and_a_degenerate_range_has_no_fill()
    {
        Assert.Equal((0.0, 60.0), BipolarFill.Compute(99, -12, 12, 120));
        Assert.Equal((60.0, 60.0), BipolarFill.Compute(-99, -12, 12, 120));
        Assert.Equal((0.0, 0.0), BipolarFill.Compute(1, 5, 5, 120));
        Assert.Equal((0.0, 0.0), BipolarFill.Compute(double.NaN, -12, 12, 120));
    }

    [Fact]
    public void The_converter_returns_a_margin_that_leaves_the_thumb_radius_at_each_end()
    {
        // Value, Minimum, Maximum, ActualHeight; parameter = the thumb size. The usable track is 136 - 16 = 120, starting 8 px down.
        var converter = new BipolarFillConverter();
        var culture = CultureInfo.InvariantCulture;

        var boost = (Thickness)converter.Convert([6.0, -12.0, 12.0, 136.0], typeof(Thickness), "16", culture);
        Assert.Equal(new Thickness(0, 8 + 30, 0, 136 - (8 + 30 + 30)), boost);

        var flat = (Thickness)converter.Convert([0.0, -12.0, 12.0, 136.0], typeof(Thickness), "16", culture);
        Assert.Equal(68, flat.Top);
        Assert.Equal(68, flat.Bottom);

        // Values that are not there yet (design time, early layout) give no fill rather than an exception.
        var missing = (Thickness)converter.Convert([System.Windows.DependencyProperty.UnsetValue, -12.0, 12.0, 136.0], typeof(Thickness), "16", culture);
        Assert.Equal(new Thickness(0), missing);
        Assert.Throws<NotSupportedException>(() => { converter.ConvertBack(new Thickness(0), [typeof(double)], "16", culture); });
    }
}
