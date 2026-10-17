using RhythmSync.Core.Clock;

namespace RhythmSync.Tests.Sim;

/// <summary>A session clock that only moves when a test sets or advances it.</summary>
public sealed class ManualClock : ISessionClock
{
    public long NowTicks { get; set; }

    public void Advance(TimeSpan d) => NowTicks += d.Ticks;
}
