using RhythmSync.App.Services;

namespace RhythmSync.App.Tests;

public class SessionTickerTests
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(1);

    private static void WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(sw.ElapsedMilliseconds < timeoutMs, "timed out waiting for ticker");
            Thread.Sleep(2);
        }
    }

    [Fact]
    public void Ticks_repeatedly_on_a_background_thread()
    {
        int count = 0;
        int? tickThread = null;
        using var ticker = new SessionTicker(() => { Interlocked.Increment(ref count); tickThread ??= Environment.CurrentManagedThreadId; }, interval: Fast);

        WaitFor(() => Volatile.Read(ref count) >= 5);

        Assert.NotEqual(Environment.CurrentManagedThreadId, tickThread);
    }

    [Fact]
    public void Default_interval_is_10_ms()
        => Assert.Equal(TimeSpan.FromMilliseconds(10), SessionTicker.DefaultInterval);

    [Fact]
    public void A_throwing_tick_is_survived()
    {
        int count = 0;
        using var ticker = new SessionTicker(() =>
        {
            if (Interlocked.Increment(ref count) <= 3) throw new InvalidOperationException("tick failed");
        }, interval: Fast);

        WaitFor(() => Volatile.Read(ref count) >= 10);
    }

    [Fact]
    public void Slow_tick_runs_every_nth_tick_and_after_the_tick()
    {
        int count = 0;
        var slowSeen = new List<int>();
        using var ticker = new SessionTicker(() => Interlocked.Increment(ref count), () =>
        {
            lock (slowSeen) slowSeen.Add(Volatile.Read(ref count));
        }, slowEvery: 5, interval: Fast);

        WaitFor(() => { lock (slowSeen) return slowSeen.Count >= 3; });
        ticker.Dispose();

        lock (slowSeen) Assert.Equal(new[] { 5, 10, 15 }, slowSeen.Take(3));
    }

    [Fact]
    public void A_throwing_slow_tick_does_not_stop_either_tick()
    {
        int ticks = 0, slow = 0;
        using var ticker = new SessionTicker(() => Interlocked.Increment(ref ticks), () =>
        {
            Interlocked.Increment(ref slow);
            throw new InvalidOperationException("slow failed");
        }, slowEvery: 2, interval: Fast);

        WaitFor(() => Volatile.Read(ref slow) >= 3);
        int before = Volatile.Read(ref ticks);
        WaitFor(() => Volatile.Read(ref ticks) > before + 3);
    }

    [Fact]
    public void Dispose_stops_ticking_and_is_idempotent()
    {
        int count = 0;
        var ticker = new SessionTicker(() => Interlocked.Increment(ref count), interval: Fast);
        WaitFor(() => Volatile.Read(ref count) >= 3);

        ticker.Dispose();
        int atDispose = Volatile.Read(ref count);
        Thread.Sleep(50);

        Assert.Equal(atDispose, Volatile.Read(ref count));
        ticker.Dispose();
    }
}
