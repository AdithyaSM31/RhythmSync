namespace RhythmSync.Core.Client;

/// <summary>
/// Loss and lateness over the trailing 10 s, from cumulative counters sampled with <see cref="Add"/> (about once a
/// second). The baseline is the newest sample at or before <c>now − 10 s</c>, so a burst stops counting once it is more
/// than 10 s old. Percentages are 0 when their denominator is 0. Not thread-safe.
/// </summary>
public sealed class StatsWindow
{
    public const long WindowTicks = 10 * AudioFormat.TicksPerSecond;

    private readonly record struct Sample(long Ticks, long Received, long Late, long Concealed);

    private readonly List<Sample> _samples = new();

    /// <summary>Adds the cumulative counters read at <paramref name="nowTicks"/> (non-decreasing times).</summary>
    public void Add(long nowTicks, long received, long late, long concealed)
    {
        _samples.Add(new Sample(nowTicks, received, late, concealed));
        int drop = 0;
        while (drop + 1 < _samples.Count && _samples[drop + 1].Ticks <= nowTicks - WindowTicks) drop++;
        if (drop > 0) _samples.RemoveRange(0, drop);
    }

    /// <summary>Late frames as a percentage of received frames in the window.</summary>
    public double LatePct
    {
        get
        {
            if (_samples.Count < 2) return 0;
            long received = _samples[^1].Received - _samples[0].Received;
            long late = _samples[^1].Late - _samples[0].Late;
            return received > 0 ? 100.0 * late / received : 0;
        }
    }

    /// <summary>Concealed frames as a percentage of frames played (received + concealed) in the window.</summary>
    public double LossPct
    {
        get
        {
            if (_samples.Count < 2) return 0;
            long received = _samples[^1].Received - _samples[0].Received;
            long concealed = _samples[^1].Concealed - _samples[0].Concealed;
            long total = received + concealed;
            return total > 0 ? 100.0 * concealed / total : 0;
        }
    }
}
