using RhythmSync.App.Services;

namespace RhythmSync.App.Tests;

public class SessionTickerTests
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(5);

    /// <summary>Counts Begin/End so tests can check they stay strictly paired; never calls winmm.</summary>
    private sealed class FakeResolution : IHighResolutionTimer
    {
        private int _depth, _begins, _ends, _maxDepth;
        public void Begin() { lock (this) { _begins++; _depth++; _maxDepth = Math.Max(_maxDepth, _depth); } }
        public void End() { lock (this) { _ends++; _depth--; } }
        public int Begins { get { lock (this) return _begins; } }
        public int Ends { get { lock (this) return _ends; } }
        public int Depth { get { lock (this) return _depth; } }
        public int MaxDepth { get { lock (this) return _maxDepth; } }
    }

    private static void WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(sw.ElapsedMilliseconds < timeoutMs, "timed out waiting for ticker");
            Thread.Sleep(2);
        }
    }

    private static SessionTicker Make(Action tick, FakeResolution res, Action? slowTick = null, Func<bool>? isActive = null,
        int slowEveryActive = 100, int slowEveryIdle = 10)
        => new(tick, slowTick, isActive, res, activeInterval: Fast, idleInterval: Slow, slowEveryActive: slowEveryActive, slowEveryIdle: slowEveryIdle);

    // ---- basics ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Ticks_repeatedly_on_a_background_thread()
    {
        int count = 0;
        int? tickThread = null;
        using var ticker = Make(() => { Interlocked.Increment(ref count); tickThread ??= Environment.CurrentManagedThreadId; }, new FakeResolution());

        WaitFor(() => Volatile.Read(ref count) >= 5);

        Assert.NotEqual(Environment.CurrentManagedThreadId, tickThread);
    }

    [Fact]
    public void Default_intervals_are_10_ms_active_and_100_ms_idle()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(10), SessionTicker.ActiveInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(100), SessionTicker.IdleInterval);
    }

    [Fact]
    public void A_throwing_tick_is_survived()
    {
        int count = 0;
        using var ticker = Make(() =>
        {
            if (Interlocked.Increment(ref count) <= 3) throw new InvalidOperationException("tick failed");
        }, new FakeResolution());

        WaitFor(() => Volatile.Read(ref count) >= 10);
    }

    [Fact]
    public void A_throwing_state_poll_is_survived()
    {
        int polls = 0, ticks = 0;
        using var ticker = Make(() => Interlocked.Increment(ref ticks), new FakeResolution(),
            isActive: () => Interlocked.Increment(ref polls) == 1 ? false : throw new InvalidOperationException("poll failed"));

        WaitFor(() => Volatile.Read(ref ticks) >= 5);
    }

    // ---- slow tick ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Slow_tick_runs_every_nth_active_tick_and_after_the_tick()
    {
        int count = 0;
        var slowSeen = new List<int>();
        using var ticker = Make(() => Interlocked.Increment(ref count), new FakeResolution(),
            slowTick: () => { lock (slowSeen) slowSeen.Add(Volatile.Read(ref count)); }, slowEveryActive: 5);

        WaitFor(() => { lock (slowSeen) return slowSeen.Count >= 3; });
        ticker.Dispose();

        lock (slowSeen) Assert.Equal(new[] { 5, 10, 15 }, slowSeen.Take(3));
    }

    [Fact]
    public void Slow_tick_uses_the_idle_count_while_idle()
    {
        int count = 0;
        var slowSeen = new List<int>();
        using var ticker = Make(() => Interlocked.Increment(ref count), new FakeResolution(),
            slowTick: () => { lock (slowSeen) slowSeen.Add(Volatile.Read(ref count)); },
            isActive: () => false, slowEveryActive: 1000, slowEveryIdle: 3);

        WaitFor(() => { lock (slowSeen) return slowSeen.Count >= 3; });
        ticker.Dispose();

        lock (slowSeen) Assert.Equal(new[] { 3, 6, 9 }, slowSeen.Take(3));
    }

    [Fact]
    public void A_throwing_slow_tick_does_not_stop_either_tick()
    {
        int ticks = 0, slow = 0;
        using var ticker = Make(() => Interlocked.Increment(ref ticks), new FakeResolution(), slowTick: () =>
        {
            Interlocked.Increment(ref slow);
            throw new InvalidOperationException("slow failed");
        }, slowEveryActive: 2);

        WaitFor(() => Volatile.Read(ref slow) >= 3);
        int before = Volatile.Read(ref ticks);
        WaitFor(() => Volatile.Read(ref ticks) > before + 3);
    }

    // ---- cadence and timer resolution --------------------------------------------------------------------------------

    [Fact]
    public void Idle_session_holds_no_timer_resolution_and_ticks_at_the_idle_interval()
    {
        var res = new FakeResolution();
        using var ticker = Make(() => { }, res, isActive: () => false);

        Assert.Equal(Slow, ticker.CurrentInterval);
        Assert.Equal(0, res.Begins);
    }

    [Fact]
    public void Active_session_holds_resolution_and_ticks_at_the_active_interval()
    {
        var res = new FakeResolution();
        using var ticker = Make(() => { }, res, isActive: () => true);

        Assert.Equal(Fast, ticker.CurrentInterval);
        Assert.Equal(1, res.Depth);
    }

    [Fact]
    public void Follows_the_isActive_poll_through_connect_and_disconnect()
    {
        var res = new FakeResolution();
        bool active = false;
        using var ticker = Make(() => { }, res, isActive: () => Volatile.Read(ref active));

        Assert.Equal(Slow, ticker.CurrentInterval);

        Volatile.Write(ref active, true);   // join: Connecting, Syncing, ...
        WaitFor(() => ticker.CurrentInterval == Fast);
        Assert.Equal(1, res.Depth);

        Volatile.Write(ref active, false);  // leave / host gone for good
        WaitFor(() => ticker.CurrentInterval == Slow);
        Assert.Equal(0, res.Depth);
        Assert.Equal(1, res.Begins);
        Assert.Equal(1, res.Ends);
    }

    [Fact]
    public void Toggling_the_same_state_twice_is_a_no_op()
    {
        var res = new FakeResolution();
        using var ticker = Make(() => { }, res, isActive: () => false);

        ticker.SetActive(false);
        ticker.SetActive(false);
        Assert.Equal(0, res.Begins);

        ticker.SetActive(true);
        ticker.SetActive(true);
        Assert.Equal(1, res.Begins);
        Assert.Equal(Fast, ticker.CurrentInterval);
    }

    [Fact]
    public void Begin_and_End_stay_paired_across_many_toggles_from_many_threads()
    {
        var res = new FakeResolution();
        var ticker = Make(() => { }, res);   // no poll: SetActive is the only driver; starts active

        Parallel.For(0, 8, t =>
        {
            for (int i = 0; i < 500; i++) ticker.SetActive((i + t) % 3 != 0);
        });
        ticker.SetActive(false);

        Assert.Equal(res.Begins, res.Ends);
        Assert.Equal(0, res.Depth);
        Assert.True(res.MaxDepth <= 1, "resolution requests must never nest");
        ticker.Dispose();
        Assert.Equal(res.Begins, res.Ends);
    }

    [Fact]
    public void Dispose_while_active_releases_the_resolution_once()
    {
        var res = new FakeResolution();
        var ticker = Make(() => { }, res);
        ticker.SetActive(false);
        ticker.SetActive(true);
        Assert.Equal(1, res.Depth);

        ticker.Dispose();
        ticker.Dispose();

        Assert.Equal(0, res.Depth);
        Assert.Equal(res.Begins, res.Ends);
    }

    [Fact]
    public void Dispose_while_idle_releases_nothing_extra()
    {
        var res = new FakeResolution();
        var ticker = Make(() => { }, res, isActive: () => false);
        ticker.Dispose();
        Assert.Equal(0, res.Begins);
        Assert.Equal(0, res.Ends);
    }

    [Fact]
    public void SetActive_after_Dispose_is_ignored()
    {
        var res = new FakeResolution();
        var ticker = Make(() => { }, res, isActive: () => false);
        ticker.Dispose();

        ticker.SetActive(true);

        Assert.Equal(0, res.Begins);
    }

    [Fact]
    public void Dispose_stops_ticking_and_is_idempotent()
    {
        int count = 0;
        var ticker = Make(() => Interlocked.Increment(ref count), new FakeResolution());
        WaitFor(() => Volatile.Read(ref count) >= 3);

        ticker.Dispose();
        int atDispose = Volatile.Read(ref count);
        Thread.Sleep(50);

        Assert.Equal(atDispose, Volatile.Read(ref count));
        ticker.Dispose();
    }
}
