using RhythmSync.Core;
using RhythmSync.Core.Client;

namespace RhythmSync.Tests.Client;

public class StatsWindowTests
{
    private const long Second = AudioFormat.TicksPerSecond;

    [Fact]
    public void StatsWindow_uses_trailing_10s()
    {
        var w = new StatsWindow();
        w.Add(0, received: 0, late: 0, concealed: 0);
        w.Add(1 * Second, received: 150, late: 50, concealed: 50);   // the burst, between t = 0 and t = 1 s
        Assert.Equal(100.0 * 50 / 150, w.LatePct, 9);
        Assert.Equal(100.0 * 50 / 200, w.LossPct, 9);

        // Clean from then on: 200 frames per second, nothing late or concealed.
        long received = 150;
        for (int t = 2; t <= 10; t++)
        {
            received += 200;
            w.Add(t * Second, received, late: 50, concealed: 50);
        }
        Assert.True(w.LatePct > 0);   // the window [0 s, 10 s] still holds the burst
        Assert.True(w.LossPct > 0);

        received += 200;
        w.Add(11 * Second, received, late: 50, concealed: 50);   // window [1 s, 11 s]: clean
        Assert.Equal(0, w.LatePct);
        Assert.Equal(0, w.LossPct);
    }

    [Fact]
    public void Empty_or_single_sample_window_reports_zero()
    {
        var w = new StatsWindow();
        Assert.Equal(0, w.LatePct);
        Assert.Equal(0, w.LossPct);
        w.Add(0, 100, 10, 10);
        Assert.Equal(0, w.LatePct);
        Assert.Equal(0, w.LossPct);
    }

    [Fact]
    public void Nothing_received_but_concealed_is_total_loss()
    {
        var w = new StatsWindow();
        w.Add(0, 100, 0, 0);
        w.Add(1 * Second, 100, 0, 200);
        Assert.Equal(100, w.LossPct);
        Assert.Equal(0, w.LatePct);
    }
}
