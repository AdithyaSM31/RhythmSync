using RhythmSync.Core.Audio;
using RhythmSync.Core.Clock;
using RhythmSync.Platform.Windows;
using Xunit.Abstractions;

namespace RhythmSync.Platform.Windows.Tests;

public class WasapiLoopbackSourceTests(ITestOutputHelper log)
{
    private sealed class Capture
    {
        public readonly List<long> Positions = new();
        public readonly List<long> QpcDeltas = new();
        public int Calls;
        public long Frames;
        public float Peak;
        public readonly StopwatchClock Clock = new();

        public void Handle(ReadOnlySpan<float> stereo, long position, long qpc)
        {
            Calls++;
            Frames += stereo.Length / 2;
            Positions.Add(position);
            QpcDeltas.Add(Clock.NowTicks - qpc);
            foreach (float s in stereo) Peak = Math.Max(Peak, Math.Abs(s));
        }
    }

    [Fact, Trait("Category", "Hardware")]
    public void Loopback_delivers_continuously_with_silence_keeper()
    {
        using var keeper = new SilenceKeeper(null);
        using var source = new WasapiLoopbackSource(null);
        var cap = new Capture();
        keeper.Start();
        source.Start(cap.Handle);

        Thread.Sleep(2000);
        source.Stop();

        int calls = cap.Calls;
        var positions = cap.Positions.ToArray();
        var deltas = cap.QpcDeltas.ToArray();
        log.WriteLine($"rate={source.SampleRate} calls={calls} frames={cap.Frames} peak={cap.Peak} " +
                      $"qpcDelta ticks min={deltas.Min()} max={deltas.Max()} faults={source.HandlerFaults}");

        Assert.True(calls >= 180, $"only {calls} callbacks in 2 s");
        for (int i = 1; i < positions.Length; i++)
            Assert.True(positions[i] > positions[i - 1], $"position not increasing at {i}: {positions[i - 1]} -> {positions[i]}");
        Assert.All(deltas, d => Assert.True(Math.Abs(d) < 500_000, $"|qpc - now| = {d} ticks"));
        Assert.Equal(0, source.HandlerFaults);
    }

    private sealed class QuietTone : IRenderCallback
    {
        private double _phase;
        public void Render(Span<float> stereo, in RenderTiming timing)
        {
            for (int i = 0; i + 1 < stereo.Length; i += 2)
            {
                float v = 0.01f * (float)Math.Sin(_phase);     // -40 dBFS, barely audible
                _phase += 2 * Math.PI * 440 / 48000;
                stereo[i] = v;
                stereo[i + 1] = v;
            }
        }
    }

    [Fact, Trait("Category", "Hardware")]
    public void Loopback_captures_real_audio_played_to_the_device()
    {
        using var tone = new WasapiRenderOutput(null);
        using var source = new WasapiLoopbackSource(null);
        var cap = new Capture();
        tone.Start(new QuietTone());
        source.Start(cap.Handle);

        Thread.Sleep(600);
        source.Stop();

        log.WriteLine($"calls={cap.Calls} peak={cap.Peak}");
        Assert.True(cap.Peak > 0.005f, $"loopback delivered only silence (peak {cap.Peak})");   // other apps may add to the mix
    }

    [Fact, Trait("Category", "Hardware")]
    public void Throwing_handler_is_counted_and_capture_thread_survives()
    {
        using var keeper = new SilenceKeeper(null);
        using var source = new WasapiLoopbackSource(null);
        int calls = 0;
        keeper.Start();
        source.Start((ReadOnlySpan<float> s, long p, long q) =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("boom");
        });

        Thread.Sleep(500);
        int afterHalf = Volatile.Read(ref calls);
        Thread.Sleep(500);

        Assert.True(Volatile.Read(ref calls) > afterHalf, "capture thread stopped after the handler threw");
        source.Stop();
        Assert.Equal(calls, source.HandlerFaults);
    }

    [Fact, Trait("Category", "Hardware")]
    public void Stop_is_idempotent_safe_from_many_threads_and_releases_the_device()
    {
        using var keeper = new SilenceKeeper(null);
        var source = new WasapiLoopbackSource(null);
        keeper.Start();
        source.Start((s, p, q) => { });
        Thread.Sleep(100);

        Parallel.For(0, 8, _ => source.Stop());
        source.Stop();
        source.Dispose();

        Assert.True(source.IsReleased, "COM objects were not released");
        Assert.Throws<ObjectDisposedException>(() => source.Start((s, p, q) => { }));
    }

    [Fact, Trait("Category", "Hardware")]
    public void Dispose_without_start_and_double_start_are_handled()
    {
        new WasapiLoopbackSource(null).Dispose();

        using var source = new WasapiLoopbackSource(null);
        source.Start((s, p, q) => { });
        Assert.Throws<InvalidOperationException>(() => source.Start((s, p, q) => { }));
        using var other = new WasapiLoopbackSource(null);
        Assert.Throws<ArgumentNullException>(() => other.Start(null!));
    }

    [Fact, Trait("Category", "Hardware")]
    public void Handler_may_stop_the_source_from_the_capture_thread()
    {
        using var keeper = new SilenceKeeper(null);
        var source = new WasapiLoopbackSource(null);
        using var stopped = new ManualResetEventSlim();
        keeper.Start();
        source.Start((s, p, q) =>
        {
            source.Stop();      // must not deadlock on its own join
            stopped.Set();
        });

        Assert.True(stopped.Wait(3000));
        Thread.Sleep(300);
        Assert.True(source.IsReleased);
    }

    [Fact, Trait("Category", "Hardware")]
    public void Explicit_device_id_works_and_keeper_lifecycle_is_safe()
    {
        string? id = DeviceCatalog.DefaultRenderId();
        Assert.NotNull(id);

        using var keeper = new SilenceKeeper(id);
        using var source = new WasapiLoopbackSource(id);
        Assert.True(source.SampleRate > 0);
        keeper.Start();
        source.Start((s, p, q) => { });
        Thread.Sleep(100);
        keeper.Dispose();
        keeper.Dispose();
        new SilenceKeeper(id).Dispose();        // never started
    }

    /// <summary>The app creates devices on the WPF STA thread; Stop comes from elsewhere and must not stall on apartment marshalling.</summary>
    [Theory, Trait("Category", "Hardware")]
    [InlineData(false)]
    [InlineData(true)]
    public void Source_created_and_started_on_sta_thread_stops_quickly_from_another_thread(bool keepStaAlive)
    {
        using var keeper = new SilenceKeeper(null);
        keeper.Start();
        WasapiLoopbackSource? source = null;
        Exception? failure = null;
        using var created = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var sta = new Thread(() =>
        {
            try
            {
                source = new WasapiLoopbackSource(null);
                source.Start((s, p, q) => { });
            }
            catch (Exception ex) { failure = ex; }
            finally { created.Set(); }
            if (keepStaAlive) release.Wait();
        });
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        Assert.True(created.Wait(5000));
        Assert.Null(failure);
        Thread.Sleep(300);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        source!.Stop();
        sw.Stop();
        release.Set();

        log.WriteLine($"Stop took {sw.ElapsedMilliseconds} ms (keepStaAlive={keepStaAlive})");
        Assert.True(sw.ElapsedMilliseconds < 1000, $"Stop took {sw.ElapsedMilliseconds} ms");
        Assert.True(source.IsReleased, "COM objects were not released");
    }

    [Fact, Trait("Category", "Hardware")]
    public void Default_device_change_is_reported_off_the_notification_thread_exactly_once_and_allows_stop()
    {
        using var source = new WasapiLoopbackSource(null);
        source.Start((s, p, q) => { });
        int raised = 0;
        int handlerThread = -1;
        using var done = new ManualResetEventSlim();
        source.DeviceInvalidated += () =>
        {
            Interlocked.Increment(ref raised);
            handlerThread = Environment.CurrentManagedThreadId;
            source.Stop();
            done.Set();
        };

        int callerThread = -1;
        var caller = new Thread(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            source.OnDefaultRenderChanged();
            source.OnDefaultRenderChanged();
        });
        caller.Start();
        caller.Join();

        Assert.True(done.Wait(2000), "DeviceInvalidated was not raised");
        Thread.Sleep(200);
        Assert.Equal(1, raised);
        Assert.NotEqual(callerThread, handlerThread);
        Assert.True(source.IsReleased);
    }
}
