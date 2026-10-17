using RhythmSync.Core;
using RhythmSync.Core.Host;

namespace RhythmSync.Tests.Host;

public class CaptureTimestamperTests
{
    [Fact]
    public void Tracks_drifting_capture_clock()
    {
        // Device runs 150 ppm fast against the session clock; session time == true time.
        const double deviceRate = 48000 * (1 + 150e-6);   // 48007.2 frames per true second
        var ts = new CaptureTimestamper(48000);
        var rng = new Random(12345);

        long TrueTicks(long pos) => (long)Math.Round(pos / deviceRate * AudioFormat.TicksPerSecond);

        long lastPos = 0;
        for (int i = 0; i <= 6000; i++)   // an observation every 10 ms for 60 s
        {
            long pos = (long)Math.Round(i * 0.010 * deviceRate);
            long jitter = (long)Math.Round((rng.NextDouble() * 2 - 1) * 20_000);   // +/-2 ms
            ts.AddObservation(pos, TrueTicks(pos) + jitter);
            lastPos = pos;
        }

        Assert.True(Math.Abs(ts.SessionTimeOf(lastPos) - TrueTicks(lastPos)) < 1000);
        Assert.True(Math.Abs(ts.MeasuredRate - deviceRate) < 1);
    }

    [Fact]
    public void Throws_without_observations()
    {
        var ts = new CaptureTimestamper(48000);
        Assert.Throws<InvalidOperationException>(() => ts.SessionTimeOf(0));
    }

    [Fact]
    public void Single_observation_uses_nominal_rate()
    {
        var ts = new CaptureTimestamper(48000);
        ts.AddObservation(1000, 5_000_000);

        Assert.Equal(5_000_000, ts.SessionTimeOf(1000));
        Assert.Equal(5_000_000 + 10_000_000, ts.SessionTimeOf(49_000));
        Assert.Equal(48000, ts.MeasuredRate, 6);
    }

    [Fact]
    public void Huge_session_epoch_does_not_lose_precision()
    {
        const long epoch = 600_000_000_000_000L;   // ~69 days of 100 ns ticks
        var ts = new CaptureTimestamper(48000);
        for (int i = 0; i < 1000; i++)
            ts.AddObservation(i * 480L, epoch + i * 100_000L);   // exactly 48 kHz

        Assert.InRange(ts.SessionTimeOf(999 * 480 + 240), epoch + 999 * 100_000L + 50_000 - 1, epoch + 999 * 100_000L + 50_000 + 1);
        Assert.Equal(48000, ts.MeasuredRate, 3);
    }

    [Fact]
    public void Old_observations_leave_the_window()
    {
        // 1 s window: the first half of the run ran at 48 kHz, the second half 10% fast.
        var ts = new CaptureTimestamper(48000, windowTicks: 10_000_000);
        long pos = 0;
        for (int i = 0; i <= 200; i++)   // 10 ms steps, 2 s
        {
            ts.AddObservation(pos, i * 100_000L);
            pos += i < 100 ? 480 : 528;
        }

        Assert.Equal(52800, ts.MeasuredRate, 1);
    }
}
