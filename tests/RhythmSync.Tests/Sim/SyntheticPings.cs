namespace RhythmSync.Tests.Sim;

/// <summary>One NTP-style exchange: t1/t4 on the client clock, t2/t3 on the host clock (100 ns ticks).</summary>
public readonly record struct PingSample(long T1, long T2, long T3, long T4);

/// <summary>
/// Deterministic generator of clock-sync pings against a known true mapping
/// <c>host(c) = offset + c * (1 + driftPpm * 1e-6)</c>. Each direction's delay is
/// 0.5 ms + Exp(mean up 2 ms / down 1 ms), plus with probability 0.1 an extra U(20, 100) ms.
/// Host turnaround is 0.05 ms. An optional single drift change keeps the mapping continuous.
/// </summary>
public sealed class SyntheticPings
{
    private const double TicksPerMs = 10_000.0;

    private readonly Random _rng;
    private readonly double _offsetTicks;
    private readonly double _driftBefore;
    private readonly long _clientStartTicks;
    private readonly long _intervalTicks;
    private long? _changeAtClientTicks;
    private double _driftAfter;
    private long _index;

    public SyntheticPings(long offsetTicks, double driftPpm, long clientStartTicks, long intervalTicks, int seed)
    {
        _offsetTicks = offsetTicks;
        _driftBefore = driftPpm * 1e-6;
        _clientStartTicks = clientStartTicks;
        _intervalTicks = intervalTicks;
        _rng = new Random(seed);
    }

    /// <summary>From <paramref name="clientTicks"/> on, the true drift becomes <paramref name="driftPpm"/> (mapping stays continuous).</summary>
    public SyntheticPings WithDriftChange(long clientTicks, double driftPpm)
    {
        _changeAtClientTicks = clientTicks;
        _driftAfter = driftPpm * 1e-6;
        return this;
    }

    /// <summary>True host time for a client time.</summary>
    public long Host(long clientTicks)
    {
        double c = clientTicks;
        if (_changeAtClientTicks is long cc && clientTicks > cc)
        {
            double hostAtChange = _offsetTicks + cc * (1 + _driftBefore);
            return (long)Math.Round(hostAtChange + (c - cc) * (1 + _driftAfter));
        }
        return (long)Math.Round(_offsetTicks + c * (1 + _driftBefore));
    }

    /// <summary>Generates the next <paramref name="count"/> pings, continuing from where the previous call stopped.</summary>
    public List<PingSample> Generate(int count)
    {
        var list = new List<PingSample>(count);
        for (int i = 0; i < count; i++)
            list.Add(Next());
        return list;
    }

    private PingSample Next()
    {
        long t1 = _clientStartTicks + _index++ * _intervalTicks;
        long up = Delay(meanMs: 2.0);
        long down = Delay(meanMs: 1.0);
        long turnaround = (long)(0.05 * TicksPerMs);

        long arrivalClient = t1 + up;               // true instant (client timeline) the host receives it
        long t2 = Host(arrivalClient);
        long departClient = arrivalClient + turnaround;
        long t3 = Host(departClient);
        long t4 = departClient + down;
        return new PingSample(t1, t2, t3, t4);
    }

    private long Delay(double meanMs)
    {
        double ms = 0.5 - meanMs * Math.Log(1 - _rng.NextDouble());
        if (_rng.NextDouble() < 0.1)
            ms += 20 + _rng.NextDouble() * 80;
        return (long)Math.Round(ms * TicksPerMs);
    }
}
