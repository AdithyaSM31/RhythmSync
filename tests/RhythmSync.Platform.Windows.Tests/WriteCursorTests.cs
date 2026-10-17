using RhythmSync.Platform.Windows;

namespace RhythmSync.Platform.Windows.Tests;

public class WriteCursorTests
{
    [Fact]
    public void Steady_state_write_position_leads_device_by_constant_lead()
    {
        var cursor = new WriteCursor(written: 960, initialLead: 960);
        long device = 0;
        for (int i = 0; i < 20; i++)
        {
            device += 480;
            long write = cursor.Begin(device);
            Assert.Equal(device + 480, write);      // lead is constant
            cursor.Commit(write, 480);
        }
    }

    [Fact]
    public void Underrun_rebases_to_last_good_lead_not_to_zero_and_stays_there()
    {
        var cursor = new WriteCursor(written: 960, initialLead: 960);
        long device = 480;
        long write = cursor.Begin(device);
        cursor.Commit(write, 480);                  // lead observed: 480
        Assert.Equal(480, write - device);

        device += 480 * 5;                          // missed periods: device runs past everything written
        write = cursor.Begin(device);
        Assert.Equal(480, write - device);          // lead preserved, not 0
        cursor.Commit(write, 480);

        for (int i = 0; i < 10; i++)                // and it keeps holding in later periods
        {
            device += 480;
            write = cursor.Begin(device);
            Assert.Equal(480, write - device);
            cursor.Commit(write, 480);
        }
    }

    [Fact]
    public void Repeated_underruns_keep_the_lead_observed_before_them()
    {
        var cursor = new WriteCursor(written: 1000, initialLead: 1000);
        long device = 200;
        cursor.Commit(cursor.Begin(device), 480);   // good period, lead 800
        device += 100_000;
        long w1 = cursor.Begin(device);
        cursor.Commit(w1, 480);
        device += 100_000;
        long w2 = cursor.Begin(device);
        Assert.Equal(800, w1 - (device - 100_000));
        Assert.Equal(800, w2 - device);
    }

    [Fact]
    public void Commit_after_a_failed_write_still_advances_the_cursor()
    {
        // Mirrors RenderPeriod: ReleaseBuffer committed the frames even though writing threw, so Commit runs in finally.
        var cursor = new WriteCursor(written: 960, initialLead: 960);
        long device = 480;
        long write = cursor.Begin(device);
        try
        {
            try { throw new InvalidOperationException("write failed"); }
            finally { cursor.Commit(write, 480); }
        }
        catch (InvalidOperationException) { }

        long next = cursor.Begin(device + 480);
        Assert.Equal(write + 480, next);            // contiguous with the committed frames, no gap/overlap
    }

    [Fact]
    public void Underrun_before_any_period_uses_the_initial_lead()
    {
        var cursor = new WriteCursor(written: 480, initialLead: 480);
        Assert.Equal(10_000 + 480, cursor.Begin(10_000));
    }
}
