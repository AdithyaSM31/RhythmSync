namespace RhythmSync.Core.Host;

/// <summary>
/// Maps capture-device stream positions (frames) to session time (100 ns ticks) with a least-squares line
/// <c>session = a + b * position</c> fitted over the observations of the last <c>windowTicks</c> of session time.
/// The fit tracks the capture clock's real rate (including drift against the session clock) and averages out
/// the jitter in individual observations.
/// </summary>
/// <remarks>
/// Not thread-safe: every call (<see cref="AddObservation"/>, <see cref="SessionTimeOf"/>, <see cref="MeasuredRate"/>)
/// must come from the capture thread.
/// </remarks>
public sealed class CaptureTimestamper
{
    private readonly int _nominalRate;
    private readonly long _windowTicks;
    private readonly List<(long Position, long Session)> _observations = new();

    // Fitted line in coordinates centred on the oldest observation (_x0, _y0), so no value squared or
    // summed is ever larger than the window itself, regardless of the absolute clock epoch.
    private long _x0;
    private long _y0;
    private double _meanDx;
    private double _meanDy;
    private double _ticksPerFrame;

    public CaptureTimestamper(int nominalRate, long windowTicks = 300_000_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(nominalRate, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(windowTicks, 0L);
        _nominalRate = nominalRate;
        _windowTicks = windowTicks;
        _ticksPerFrame = (double)AudioFormat.TicksPerSecond / nominalRate;
    }

    /// <summary>Measured capture rate in device frames per session second (nominal until two observations exist).</summary>
    public double MeasuredRate => AudioFormat.TicksPerSecond / _ticksPerFrame;

    /// <summary>Records that device stream position <paramref name="devicePositionFrames"/> was captured at <paramref name="sessionTicks"/>.</summary>
    public void AddObservation(long devicePositionFrames, long sessionTicks)
    {
        _observations.Add((devicePositionFrames, sessionTicks));

        long oldestAllowed = sessionTicks - _windowTicks;
        int drop = 0;
        while (drop < _observations.Count - 1 && _observations[drop].Session < oldestAllowed) drop++;
        if (drop > 0) _observations.RemoveRange(0, drop);

        Refit();
    }

    /// <summary>
    /// Session time (ticks) at which device position <paramref name="devicePositionFrames"/> was, or will be, captured.
    /// With one observation the nominal rate is used; with none this throws.
    /// </summary>
    /// <exception cref="InvalidOperationException">No observation has been added yet.</exception>
    public long SessionTimeOf(double devicePositionFrames)
    {
        if (_observations.Count == 0)
            throw new InvalidOperationException("No capture observation has been added yet.");

        double dx = devicePositionFrames - _x0;
        return _y0 + (long)Math.Round(_meanDy + _ticksPerFrame * (dx - _meanDx));
    }

    private void Refit()
    {
        int n = _observations.Count;
        _x0 = _observations[0].Position;
        _y0 = _observations[0].Session;

        if (n < 2)
        {
            // Pass through the single observation using the nominal rate.
            _meanDx = 0;
            _meanDy = 0;
            _ticksPerFrame = (double)AudioFormat.TicksPerSecond / _nominalRate;
            return;
        }

        double sumX = 0, sumY = 0;
        foreach (var (p, s) in _observations)
        {
            sumX += p - _x0;
            sumY += s - _y0;
        }
        double meanX = sumX / n;
        double meanY = sumY / n;

        double sxx = 0, sxy = 0;
        foreach (var (p, s) in _observations)
        {
            double cx = (p - _x0) - meanX;
            double cy = (s - _y0) - meanY;
            sxx += cx * cx;
            sxy += cx * cy;
        }

        _meanDx = meanX;
        _meanDy = meanY;
        double slope = sxx > 0 ? sxy / sxx : 0;
        _ticksPerFrame = slope > 0 ? slope : (double)AudioFormat.TicksPerSecond / _nominalRate;
    }
}
