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

    private const int SeedCount = 50;       // per-seed bounds are asserted on seeds 1..50
    private const int P95SeedCount = 500;   // the p95 drift bound is taken over seeds 1..500 (50 draws mostly measure sampling noise)
    private const double MaxDriftErrorPpm = 8;
    private const double P95DriftErrorPpm = 4;

    private static void AssertPositionAndDrift(int seed, ClockEstimator est, long trueHost, long atClientTicks, double expectedDriftPpm, List<double> driftErrors)
    {
        long posErr = est.ClientToHost(atClientTicks) - trueHost;
        double driftErr = Math.Abs(est.DriftPpm - expectedDriftPpm);
        driftErrors.Add(driftErr);
        if (seed > SeedCount) return;
        Assert.True(Math.Abs(posErr) <= 2000, $"seed {seed}: position error {posErr} ticks (max 2000), drift {est.DriftPpm:F2} ppm");
        Assert.True(driftErr <= MaxDriftErrorPpm, $"seed {seed}: drift {est.DriftPpm:F2} ppm, error {driftErr:F2} ppm from {expectedDriftPpm} (max {MaxDriftErrorPpm}), position error {posErr} ticks");
    }

    private static void AssertDriftP95(List<double> driftErrors)
    {
        var sorted = driftErrors.OrderBy(x => x).ToList();
        double p95 = sorted[(int)Math.Ceiling(0.95 * sorted.Count) - 1];
        Assert.True(p95 <= P95DriftErrorPpm, $"p95 drift error {p95:F2} ppm (max {P95DriftErrorPpm}) over {sorted.Count} seeds, worst {sorted[^1]:F2}");
    }

    [Fact]
    public void Recovers_offset_and_drift()
    {
        var driftErrors = new List<double>();
        for (int seed = 1; seed <= P95SeedCount; seed++)
        {
            var truth = new SyntheticPings(offsetTicks: 37_000_000, driftPpm: 80, clientStartTicks: 5 * Sec, intervalTicks: 250 * Ms, seed: seed);
            var pings = truth.Generate(480); // 120 s at 250 ms
            var est = Feed(pings);
            long lastT4 = pings[^1].T4;

            AssertPositionAndDrift(seed, est, truth.Host(lastT4), lastT4, expectedDriftPpm: 80, driftErrors);
            Assert.Equal(lastT4, est.LastSampleClientTicks);
        }
        AssertDriftP95(driftErrors);
    }

    [Fact]
    public void Converges_after_2s_of_fast_pings()
    {
        for (int seed = 1; seed <= SeedCount; seed++)
        {
            var truth = new SyntheticPings(37_000_000, 0, 5 * Sec, 50 * Ms, seed);
            var est = Feed(truth.Generate(40));

            Assert.True(est.IsConverged, $"seed {seed}: not converged (residual {est.ResidualMs:F3} ms)");
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
        var driftErrors = new List<double>();
        for (int seed = 1; seed <= SeedCount; seed++)
        {
            var truth = new SyntheticPings(offsetTicks: -30 * day, driftPpm: 80, clientStartTicks: 60 * day, intervalTicks: 250 * Ms, seed: seed);
            var pings = truth.Generate(480);
            var est = Feed(pings);
            long lastT4 = pings[^1].T4;

            AssertPositionAndDrift(seed, est, truth.Host(lastT4), lastT4, expectedDriftPpm: 80, driftErrors);
        }
        AssertDriftP95(driftErrors);
    }

    [Fact]
    public void Window_forgets_old_drift()
    {
        long start = 5 * Sec;
        var driftErrors = new List<double>();
        for (int seed = 1; seed <= P95SeedCount; seed++)
        {
            var truth = new SyntheticPings(37_000_000, 80, start, 250 * Ms, seed)
                .WithDriftChange(start + 60 * Sec, -40);
            var pings = truth.Generate(520); // 130 s total
            var est = Feed(pings);

            double err = Math.Abs(est.DriftPpm - -40);
            driftErrors.Add(err);
            if (seed > SeedCount) continue;
            Assert.True(err <= MaxDriftErrorPpm, $"seed {seed}: drift {est.DriftPpm:F2} ppm, error {err:F2} ppm from -40 (max {MaxDriftErrorPpm})");
        }
        AssertDriftP95(driftErrors);
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
