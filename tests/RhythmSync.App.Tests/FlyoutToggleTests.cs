using RhythmSync.App.Flyout;

namespace RhythmSync.App.Tests;

/// <summary>The tray icon toggles the flyout, and the click that deactivated it does not open it again.</summary>
public class FlyoutToggleTests
{
    private static readonly DateTime T = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Click_opens_when_closed()
    {
        var toggle = new FlyoutToggle();

        Assert.True(toggle.OnTrayClick(T));
        Assert.True(toggle.IsOpen);
    }

    [Fact]
    public void Click_closes_when_open()
    {
        var toggle = new FlyoutToggle();
        toggle.OnTrayClick(T);

        Assert.False(toggle.OnTrayClick(T.AddSeconds(2)));
        Assert.False(toggle.IsOpen);
    }

    [Fact]
    public void Click_right_after_deactivation_does_not_reopen()
    {
        var toggle = new FlyoutToggle();
        toggle.OnTrayClick(T);

        toggle.OnDeactivated(T.AddSeconds(1));
        Assert.False(toggle.IsOpen);

        Assert.False(toggle.OnTrayClick(T.AddSeconds(1).AddMilliseconds(100)));
        Assert.False(toggle.IsOpen);
    }

    [Fact]
    public void Click_after_guard_reopens()
    {
        var toggle = new FlyoutToggle();
        toggle.OnTrayClick(T);
        toggle.OnDeactivated(T);

        Assert.True(toggle.OnTrayClick(T.AddMilliseconds(400)));
        Assert.True(toggle.IsOpen);
    }

    [Fact]
    public void Closing_by_an_action_lets_the_next_click_open_at_once()
    {
        var toggle = new FlyoutToggle();
        toggle.OnTrayClick(T);

        toggle.OnClosedByAction();

        Assert.False(toggle.IsOpen);
        Assert.True(toggle.OnTrayClick(T.AddMilliseconds(10)));
    }

    [Fact]
    public void A_clock_that_went_back_does_not_block_opening()
    {
        var toggle = new FlyoutToggle();
        toggle.OnDeactivated(T);

        Assert.True(toggle.OnTrayClick(T.AddMinutes(-5)));
    }
}
