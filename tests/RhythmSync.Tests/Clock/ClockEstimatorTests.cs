using RhythmSync.Core.Clock;
using RhythmSync.Tests.Sim;

namespace RhythmSync.Tests.Clock;

public class ClockEstimatorTests
{
    private const long Sec = 10_000_000;
    private const long Ms = 10_000;

    private static ClockEstimator Feed(IEnumerable<PingSample> pings)
    {
        var est = new ClockEstimator();
        foreach (var p in pings) est.AddSample(p.T1, p.T2, p.T3, p.T4);
        return est;
    }

    private const int SeedCount = 50;

    [Fact]
    public void Recovers_offset_and_drift()
    {
        for (int seed = 1; seed <= SeedCount; seed++)
        {
            var truth = new SyntheticPings(offsetTicks: 37_000_000, driftPpm: 80, clientStartTicks: 5 * Sec, intervalTicks: 250 * Ms, seed: seed);
            var pings = truth.Generate(480); // 120 s at 250 ms
            var est = Feed(pings);
            long lastT4 = pings[^1].T4;

            long posErr = est.ClientToHost(lastT4) - truth.Host(lastT4);
            Assert.True(posErr is >= -2000 and <= 2000, $"seed {seed}: position error {posErr} ticks");
            Assert.True(est.DriftPpm is >= 78 and <= 82, $"seed {seed}: drift {est.DriftPpm:F2} ppm (expected 78..82)");
            Assert.Equal(lastT4, est.LastSampleClientTicks);
        }
    }

    [Fact]
    public void Converges_after_2s_of_fast_pings()
    {
        for (int seed = 1; seed <= SeedCount; seed++)
        {
            var truth = new SyntheticPings(37_000_000, 0, 5 * Sec, 50 * Ms, seed);
            var est = Feed(truth.Generate(40));

            Assert.True(est.IsConverged, $"seed {seed}: not converged (residual {est.ResidualMs:F3} ms)");
            Assert.True(est.ResidualMs < 0.5, $"seed {seed}: residual {est.ResidualMs:F3} ms");
            Assert.True(est.EverConverged, $"seed {seed}: EverConverged false");
        }
    }

    [Fact]
    public void Not_converged_below_20_samples()
    {
        var truth = new SyntheticPings(37_000_000, 0, 5 * Sec, 50 * Ms, seed: 7);
        var est = Feed(truth.Generate(19));

        Assert.False(est.IsConverged);
        Assert.False(est.EverConverged);
        Assert.Equal(19, est.SampleCount);
    }

    [Fact]
    public void Large_absolute_times_keep_precision()
    {
        const long day = 86_400 * Sec;
        for (int seed = 1; seed <= SeedCount; seed++)
        {
            var truth = new SyntheticPings(offsetTicks: -30 * day, driftPpm: 80, clientStartTicks: 60 * day, intervalTicks: 250 * Ms, seed: seed);
            var pings = truth.Generate(480);
            var est = Feed(pings);
            long lastT4 = pings[^1].T4;

            long posErr = est.ClientToHost(lastT4) - truth.Host(lastT4);
            Assert.True(posErr is >= -2000 and <= 2000, $"seed {seed}: position error {posErr} ticks");
            Assert.True(est.DriftPpm is >= 78 and <= 82, $"seed {seed}: drift {est.DriftPpm:F2} ppm (expected 78..82)");
        }
    }

    [Fact]
    public void Window_forgets_old_drift()
    {
        long start = 5 * Sec;
        for (int seed = 1; seed <= SeedCount; seed++)
        {
            var truth = new SyntheticPings(37_000_000, 80, start, 250 * Ms, seed)
                .WithDriftChange(start + 60 * Sec, -40);
            var est = Feed(truth.Generate(520)); // 130 s total

            Assert.True(est.DriftPpm is >= -42 and <= -38, $"seed {seed}: drift {est.DriftPpm:F2} ppm (expected -42..-38)");
        }
    }

    [Fact]
    public void Window_drops_samples_older_than_window()
    {
        var truth = new SyntheticPings(37_000_000, 0, 5 * Sec, 250 * Ms, seed: 3);
        var est = Feed(truth.Generate(520)); // 130 s at 250 ms, window is 60 s

        Assert.InRange(est.SampleCount, 238, 242);
    }

    [Fact]
    public void Identity_maps_one_to_one()
    {
        var id = new IdentityClockMapping();

        Assert.True(id.IsConverged);
        Assert.True(id.EverConverged);
        Assert.Equal(0, id.DriftPpm);
        Assert.Equal(0, id.ResidualMs);
        Assert.Equal(123_456_789_012L, id.ClientToHost(123_456_789_012L));
    }

    [Fact]
    public void Stopwatch_clock_is_monotonic_100ns_ticks()
    {
        var clock = new StopwatchClock();
        long a = clock.NowTicks;
        Thread.Sleep(50);
        long b = clock.NowTicks;

        Assert.InRange(b - a, 40 * Ms, 500 * Ms);
    }

    [Fact]
    public void Estimator_before_any_sample_is_identity_and_not_converged()
    {
        var est = new ClockEstimator();

        Assert.False(est.IsConverged);
        Assert.Null(est.LastSampleClientTicks);
        Assert.Equal(1_000, est.ClientToHost(1_000));
    }
}
