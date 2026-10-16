using System.Diagnostics;

namespace RhythmSync.Core.Clock;

/// <summary>Session clock backed by the QueryPerformanceCounter, converted to 100 ns ticks.</summary>
public sealed class StopwatchClock : ISessionClock
{
    public long NowTicks => ToTicks(Stopwatch.GetTimestamp(), Stopwatch.Frequency);

    /// <summary>Scales a raw counter value to 100 ns ticks without overflowing (split into whole seconds and remainder).</summary>
    internal static long ToTicks(long timestamp, long frequency)
    {
        long wholeSeconds = Math.DivRem(timestamp, frequency, out long remainder);
        return wholeSeconds * AudioFormat.TicksPerSecond + remainder * AudioFormat.TicksPerSecond / frequency;
    }
}
