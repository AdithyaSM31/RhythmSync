namespace RhythmSync.Core.Clock;

/// <summary>
/// NTP-style client-to-host clock estimator. Keeps a sliding window of ping exchanges, splits it into
/// time buckets, keeps the min-RTT sample of each (least queueing noise) and fits <c>host = a + b * client</c> by
/// RTT-weighted least squares.
/// The fit is done on values centered on the first selected sample so absolute tick counts
/// (about 1e13 after weeks of uptime) are never squared.
/// <para>
/// <see cref="AddSample"/> may be called from one thread (calls are serialized) while the
/// <see cref="IClockMapping"/> members are read from others; readers see a consistent snapshot.
/// </para>
/// </summary>
public sealed class ClockEstimator : IClockMapping
{
    /// <summary>Weight floor e0 (0.5 ms): weight = 1 / (rtt - minRtt + e0)^2.</summary>
    private const double RttWeightFloorTicks = 5000.0;

    private readonly long _windowTicks;
    private readonly double _keepFraction;
    private readonly int _minSamples;
    private readonly double _convergedResidualMs;

    private readonly object _gate = new();
    private readonly List<Sample> _samples = new();
    private Sample[] _scratch = Array.Empty<Sample>();
    private Sample[] _selected = Array.Empty<Sample>();
    private double[] _weights = Array.Empty<double>();

    private volatile Fit _fit = Fit.Identity;

    /// <param name="convergedResidualMs">Convergence threshold in ms, compared against <see cref="StdErrorMs"/> (not <see cref="ResidualMs"/>).</param>
    public ClockEstimator(long windowTicks = 600_000_000, double keepFraction = 0.2, int minSamples = 20, double convergedResidualMs = 0.5)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(windowTicks, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(keepFraction, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(keepFraction, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(minSamples, 2);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(convergedResidualMs, 0);
        _windowTicks = windowTicks;
        _keepFraction = keepFraction;
        _minSamples = minSamples;
        _convergedResidualMs = convergedResidualMs;
    }

    public bool IsConverged => _fit.IsConverged;
    public double DriftPpm => _fit.DriftPpm;
    public double ResidualMs => _fit.ResidualMs;
    public double StdErrorMs => _fit.StdErrorMs;
    public long? LastSampleClientTicks => _fit.LastSampleClientTicks;
    public bool EverConverged => _fit.EverConverged;
    public int SampleCount => _fit.SampleCount;

    public long ClientToHost(long clientTicks)
    {
        var f = _fit;
        return f.H0 + (long)Math.Round(f.Intercept + f.Slope * (clientTicks - f.C0));
    }

    /// <summary>Adds one ping exchange: t1/t4 on the client clock, t2/t3 on the host clock.</summary>
    public void AddSample(long t1, long t2, long t3, long t4)
    {
        long rtt = (t4 - t1) - (t3 - t2);
        long offset = ((t2 - t1) + (t3 - t4)) / 2;
        long c = t1 + (t4 - t1) / 2;
        long h = c + offset;

        lock (_gate)
        {
            _samples.Add(new Sample(c, h, rtt));
            long cutoff = t4 - _windowTicks;
            _samples.RemoveAll(s => s.C < cutoff);
            _fit = Refit(t4);
        }
    }

    private Fit Refit(long lastT4)
    {
        int n = _samples.Count;
        if (n == 0)
            return _fit with { SampleCount = 0, LastSampleClientTicks = lastT4 };

        int k = Math.Max((int)Math.Ceiling(_keepFraction * n), Math.Min(n, _minSamples));
        k = Math.Min(k, n);

        // Sort the window by client time, split into k contiguous buckets of near-equal count and keep
        // the min-RTT sample of each bucket: clean samples are spread across the window (good slope conditioning).
        if (_scratch.Length < n) _scratch = new Sample[Math.Max(n, _scratch.Length * 2)];
        if (_selected.Length < k) _selected = new Sample[Math.Max(k, _selected.Length * 2)];
        _samples.CopyTo(_scratch);
        Array.Sort(_scratch, 0, n, TimeComparer.Instance);
        for (int j = 0; j < k; j++)
        {
            int from = (int)((long)j * n / k), to = (int)((long)(j + 1) * n / k);
            int best = from;
            for (int i = from + 1; i < to; i++)
                if (_scratch[i].Rtt < _scratch[best].Rtt) best = i;
            _selected[j] = _scratch[best];
        }

        // Anchor on the earliest selected sample (bucket 0 holds the earliest times).
        long c0 = _selected[0].C;
        long h0 = _selected[0].H;

        // RTT-weighted least squares: weight w = 1 / (rtt - minRtt + e0)^2, minRtt = smallest RTT in the window.
        // A sample with large excess RTT carries a large, unknown offset error and is down-weighted accordingly.
        long minRtt = long.MaxValue;
        for (int i = 0; i < n; i++)
            if (_scratch[i].Rtt < minRtt) minRtt = _scratch[i].Rtt;

        if (_weights.Length < k) _weights = new double[Math.Max(k, _weights.Length * 2)];
        double sumW = 0, sumX = 0, sumY = 0;
        for (int i = 0; i < k; i++)
        {
            double e = (_selected[i].Rtt - minRtt) + RttWeightFloorTicks;
            double w = 1.0 / (e * e);
            _weights[i] = w;
            sumW += w;
            sumX += w * (_selected[i].C - c0);
            sumY += w * (_selected[i].H - h0);
        }
        double meanX = sumX / sumW, meanY = sumY / sumW;

        double sxx = 0, sxy = 0;
        for (int i = 0; i < k; i++)
        {
            double dx = (_selected[i].C - c0) - meanX;
            double dy = (_selected[i].H - h0) - meanY;
            sxx += _weights[i] * dx * dx;
            sxy += _weights[i] * dx * dy;
        }
        double slope = sxx > 0 ? sxy / sxx : 1.0;
        double intercept = meanY - slope * meanX;

        double sse = 0, weightedSse = 0;
        for (int i = 0; i < k; i++)
        {
            double r = (_selected[i].H - h0) - (intercept + slope * (_selected[i].C - c0));
            sse += r * r;
            weightedSse += _weights[i] * r * r;
        }
        // ResidualMs: plain RMS scatter of the selected samples about the fit.
        double residualMs = Math.Sqrt(sse / k) / 10_000.0;

        // Weighted-fit standard error (ms) of the fitted host time at the latest client time;
        // infinite if the fit is under-determined.
        double seMs = double.PositiveInfinity;
        if (k > 2 && sxx > 0)
        {
            double s2 = weightedSse / (k - 2);
            double dxLast = (lastT4 - c0) - meanX;
            seMs = Math.Sqrt(s2 * (1.0 / sumW + dxLast * dxLast / sxx)) / 10_000.0;
        }

        bool converged = n >= _minSamples && seMs < _convergedResidualMs;
        return new Fit(c0, h0, intercept, slope, residualMs, seMs, converged, _fit.EverConverged || converged, (slope - 1.0) * 1e6, lastT4, n);
    }

    private readonly record struct Sample(long C, long H, long Rtt);

    private sealed class TimeComparer : IComparer<Sample>
    {
        public static readonly TimeComparer Instance = new();
        public int Compare(Sample x, Sample y) => x.C.CompareTo(y.C);
    }

    private sealed record Fit(
        long C0, long H0, double Intercept, double Slope,
        double ResidualMs, double StdErrorMs, bool IsConverged, bool EverConverged, double DriftPpm,
        long? LastSampleClientTicks, int SampleCount)
    {
        public static readonly Fit Identity = new(0, 0, 0, 1.0, 0, double.PositiveInfinity, false, false, 0, null, 0);
    }
}
