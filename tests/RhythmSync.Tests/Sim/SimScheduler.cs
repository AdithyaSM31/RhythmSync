namespace RhythmSync.Tests.Sim;

/// <summary>
/// Deterministic discrete-event simulator on a "true" timeline of 100 ns ticks. Actions run in order of
/// (time, scheduling order); <see cref="Now"/> is the time of the running action.
/// </summary>
public sealed class SimScheduler
{
    private readonly PriorityQueue<Action, (long At, long Order)> _queue = new();
    private long _order;

    public SimScheduler(long startTicks = 0) => Now = startTicks;

    public long Now { get; private set; }

    public void Schedule(long atTicks, Action a)
    {
        ArgumentNullException.ThrowIfNull(a);
        if (atTicks < Now)
            throw new ArgumentOutOfRangeException(nameof(atTicks), $"Cannot schedule at {atTicks}, before now ({Now}).");
        _queue.Enqueue(a, (atTicks, _order++));
    }

    /// <summary>Runs every action due at or before <paramref name="ticks"/> (including ones they schedule), then sets Now to it.</summary>
    public void RunUntil(long ticks)
    {
        while (_queue.TryPeek(out _, out var key) && key.At <= ticks)
        {
            Action a = _queue.Dequeue();
            Now = key.At;
            a();
        }
        if (ticks > Now) Now = ticks;
    }
}
