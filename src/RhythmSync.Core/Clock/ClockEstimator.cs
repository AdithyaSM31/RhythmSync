namespace RhythmSync.Core.Clock;

/// <summary>
/// NTP-style client-to-host clock estimator. Keeps a sliding window of ping exchanges, selects the
/// lowest-RTT fraction (least queueing noise) and fits <c>host = a + b * client</c> by least squares.
/// The fit is done on values centered on the first selected sample so absolute tick counts
/// (about 1e13 after weeks of uptime) are never squared.
/// <para>
/// <see cref="AddSample"/> may be called from one thread (calls are serialized) while the
/// <see cref="IClockMapping"/> members are read from others; readers see a consistent snapshot.
/// </para>
/// </summary>
public sealed class ClockEstimator : IClockMapping
{
    private readonly long _windowTicks;
    private readonly double _keepFraction;
    private readonly int _minSamples;
    private readonly double _convergedResidualMs;

    private readonly object _gate = new();
    private readonly List<Sample> _samples = new();
    private Sample[] _scratch = Array.Empty<Sample>();

    private volatile Fit _fit = Fit.Identity;

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

        if (_scratch.Length < n) _scratch = new Sample[Math.Max(n, _scratch.Length * 2)];
        _samples.CopyTo(_scratch);
        // k smallest RTTs; ties broken by time so the choice is deterministic.
        Array.Sort(_scratch, 0, n, SampleComparer.Instance);

        // Anchor on the earliest selected sample (k samples are in [0,k); find min C).
        int anchor = 0;
        for (int i = 1; i < k; i++)
            if (_scratch[i].C < _scratch[anchor].C) anchor = i;
        long c0 = _scratch[anchor].C;
        long h0 = _scratch[anchor].H;

        double sumX = 0, sumY = 0;
        for (int i = 0; i < k; i++)
        {
            sumX += _scratch[i].C - c0;
            sumY += _scratch[i].H - h0;
        }
        double meanX = sumX / k, meanY = sumY / k;

        double sxx = 0, sxy = 0;
        for (int i = 0; i < k; i++)
        {
            double dx = (_scratch[i].C - c0) - meanX;
            double dy = (_scratch[i].H - h0) - meanY;
            sxx += dx * dx;
            sxy += dx * dy;
        }
        double slope = sxx > 0 ? sxy / sxx : 1.0;
        double intercept = meanY - slope * meanX;

        double sse = 0;
        for (int i = 0; i < k; i++)
        {
            double r = (_scratch[i].H - h0) - (intercept + slope * (_scratch[i].C - c0));
            sse += r * r;
        }
        double residualMs = Math.Sqrt(sse / k) / 10_000.0;

        bool converged = n >= _minSamples && residualMs < _convergedResidualMs;
        return new Fit(c0, h0, intercept, slope, residualMs, converged, _fit.EverConverged || converged, (slope - 1.0) * 1e6, lastT4, n);
    }

    private readonly record struct Sample(long C, long H, long Rtt);

    private sealed class SampleComparer : IComparer<Sample>
    {
        public static readonly SampleComparer Instance = new();
        public int Compare(Sample x, Sample y)
        {
            int byRtt = x.Rtt.CompareTo(y.Rtt);
            return byRtt != 0 ? byRtt : x.C.CompareTo(y.C);
        }
    }

    private sealed record Fit(
        long C0, long H0, double Intercept, double Slope,
        double ResidualMs, bool IsConverged, bool EverConverged, double DriftPpm,
        long? LastSampleClientTicks, int SampleCount)
    {
        public static readonly Fit Identity = new(0, 0, 0, 1.0, 0, false, false, 0, null, 0);
    }
}
