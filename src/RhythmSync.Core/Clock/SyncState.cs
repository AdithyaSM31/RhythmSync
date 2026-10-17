namespace RhythmSync.Core.Clock;

public enum SyncState { Connecting, Syncing, InSync, Degraded, Disconnected }

/// <summary>Derives the clock-sync part of a client's <see cref="SyncState"/> from its clock mapping.</summary>
public static class SyncStateEvaluator
{
    /// <summary>Above this standard error (ms) the mapping is too uncertain for tight sync.</summary>
    public const double MaxStdErrorMs = 1.0;

    /// <summary>With no ping exchange for longer than this, the mapping is stale.</summary>
    public const long MaxSampleAgeTicks = 10 * AudioFormat.TicksPerSecond;

    /// <summary>
    /// Never converged → <see cref="SyncState.Syncing"/>; standard error above 1 ms, or last sample more than 10 s
    /// before <paramref name="nowClientTicks"/> → <see cref="SyncState.Degraded"/>; otherwise <see cref="SyncState.InSync"/>.
    /// </summary>
    public static SyncState Evaluate(IClockMapping m, long nowClientTicks)
    {
        ArgumentNullException.ThrowIfNull(m);
        if (!m.EverConverged) return SyncState.Syncing;
        if (!(m.StdErrorMs <= MaxStdErrorMs)) return SyncState.Degraded;   // NaN counts as degraded
        if (m.LastSampleClientTicks is long last && nowClientTicks - last > MaxSampleAgeTicks) return SyncState.Degraded;
        return SyncState.InSync;
    }
}
