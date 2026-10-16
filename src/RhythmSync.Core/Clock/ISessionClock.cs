namespace RhythmSync.Core.Clock;

/// <summary>Monotonic local clock in 100 ns ticks (QPC-based; same epoch/unit as WASAPI <c>qpcPosition</c>).</summary>
public interface ISessionClock
{
    long NowTicks { get; }
}
