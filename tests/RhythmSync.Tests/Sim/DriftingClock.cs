using RhythmSync.Core.Clock;

namespace RhythmSync.Tests.Sim;

/// <summary>A session clock that reads <c>offset + trueTime · (1 + driftPpm·1e-6)</c> on a <see cref="SimScheduler"/> timeline.</summary>
public sealed class DriftingClock : ISessionClock
{
    private readonly SimScheduler _sim;
    private readonly long _offsetTicks;
    private readonly double _drift;

    public DriftingClock(SimScheduler s, long offsetTicks, double driftPpm)
    {
        _sim = s ?? throw new ArgumentNullException(nameof(s));
        _offsetTicks = offsetTicks;
        _drift = driftPpm * 1e-6;
    }

    public long NowTicks => ToClock(_sim.Now);

    /// <summary>Clock reading at true time <paramref name="trueTicks"/>.</summary>
    public long ToClock(long trueTicks) => _offsetTicks + trueTicks + (long)Math.Round(trueTicks * _drift);

    /// <summary>True time at which this clock reads <paramref name="clockTicks"/> (inverse of <see cref="ToClock"/>).</summary>
    public long ToTrue(long clockTicks)
    {
        long elapsed = clockTicks - _offsetTicks;
        return elapsed - (long)Math.Round(elapsed * _drift / (1 + _drift));
    }
}
