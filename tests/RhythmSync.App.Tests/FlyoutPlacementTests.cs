using System.Windows;
using RhythmSync.App.Flyout;

namespace RhythmSync.App.Tests;

/// <summary>Where the tray flyout goes: the working-area corner on the taskbar's edge, always inside the working area.</summary>
public class FlyoutPlacementTests
{
    private static readonly Rect Screen = new(0, 0, 1920, 1080);
    private static readonly Size Flyout = new(320, 400);

    private static void AssertInside(Rect work, Point topLeft, double width, double height)
    {
        Assert.True(work.Contains(new Rect(topLeft, new Size(width, height))),
            $"{topLeft} {width}x{height} is not inside {work}");
    }

    [Fact]
    public void Bottom_taskbar_puts_flyout_bottom_right()
    {
        var work = new Rect(0, 0, 1920, 1032);

        var (topLeft, height) = FlyoutPlacement.Compute(work, Screen, Flyout);

        Assert.Equal(new Point(1920 - 12 - 320, 1032 - 12 - 400), topLeft);
        Assert.Equal(400, height);
    }

    [Fact]
    public void Top_taskbar_puts_flyout_top_right()
    {
        var work = new Rect(0, 48, 1920, 1032);

        var (topLeft, _) = FlyoutPlacement.Compute(work, Screen, Flyout);

        Assert.Equal(new Point(1588, 60), topLeft);
    }

    [Fact]
    public void Left_taskbar_puts_flyout_bottom_left()
    {
        var work = new Rect(48, 0, 1872, 1080);

        var (topLeft, _) = FlyoutPlacement.Compute(work, Screen, Flyout);

        Assert.Equal(new Point(60, 1080 - 12 - 400), topLeft);
    }

    [Fact]
    public void Right_taskbar_puts_flyout_bottom_right_of_work_area()
    {
        var work = new Rect(0, 0, 1872, 1080);

        var (topLeft, _) = FlyoutPlacement.Compute(work, Screen, Flyout);

        Assert.Equal(new Point(1872 - 12 - 320, 668), topLeft);
    }

    [Fact]
    public void No_taskbar_inset_falls_back_to_bottom_right()
    {
        var (topLeft, _) = FlyoutPlacement.Compute(Screen, Screen, Flyout);

        Assert.Equal(new Point(1920 - 12 - 320, 1080 - 12 - 400), topLeft);
    }

    [Fact]
    public void Tall_flyout_is_capped_to_work_area()
    {
        var work = new Rect(0, 0, 1920, 1032);

        var (topLeft, height) = FlyoutPlacement.Compute(work, Screen, new Size(320, 2000));

        Assert.Equal(1008, height);
        Assert.Equal(12, topLeft.Y);
        AssertInside(work, topLeft, 320, height);
    }

    [Fact]
    public void Second_monitor_with_negative_origin()
    {
        var screen = new Rect(-1920, 0, 1920, 1080);
        var work = new Rect(-1920, 0, 1920, 1040);

        var (topLeft, height) = FlyoutPlacement.Compute(work, screen, Flyout);

        Assert.Equal(new Point(-12 - 320, 1040 - 12 - 400), topLeft);
        AssertInside(work, topLeft, 320, height);
    }

    [Fact]
    public void At_150_percent_the_visible_panel_keeps_its_gap_inside_the_work_area()
    {
        // A 2560x1600 screen at 150% with a 48 px bottom taskbar, in DIPs (48 / 1.5 = 32).
        var screen = new Rect(0, 0, 2560 / 1.5, 1600 / 1.5);
        var work = new Rect(0, 0, 2560 / 1.5, (1600 - 48) / 1.5);
        var window = new Size(336, 400);   // the 320 px panel plus the shadow margin on each side

        // The same arithmetic as TrayFlyoutWindow.Place: the window keeps the 12 px gap less the shadow margin.
        double shadow = TrayFlyoutWindow.ShadowMargin;
        var (topLeft, height) = FlyoutPlacement.Compute(work, screen, window, 12 - shadow);

        AssertInside(work, topLeft, window.Width, height);
        var panel = new Rect(topLeft.X + shadow, topLeft.Y + shadow, window.Width - 2 * shadow, height - 2 * shadow);
        Assert.Equal(320, panel.Width);
        Assert.Equal(12, work.Right - panel.Right, 6);
        Assert.Equal(12, work.Bottom - panel.Bottom, 6);
    }

    [Fact]
    public void Flyout_wider_than_a_tiny_work_area_still_starts_inside_it()
    {
        var work = new Rect(100, 50, 300, 200);

        var (topLeft, height) = FlyoutPlacement.Compute(work, new Rect(100, 50, 300, 240), Flyout);

        Assert.Equal(200 - 24, height);
        Assert.True(work.Contains(topLeft), $"{topLeft} is outside {work}");
        Assert.True(topLeft.Y + height <= work.Bottom);
    }
}
