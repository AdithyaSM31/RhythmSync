using RhythmSync.Core.Audio;
using RhythmSync.Platform.Windows;
using Xunit.Abstractions;

namespace RhythmSync.Platform.Windows.Tests;

public class WasapiRenderOutputLifecycleTests(ITestOutputHelper log)
{
    private sealed class ThrowingCallback : IRenderCallback
    {
        public int Calls;
        public int MaxFrames;
        public void Render(Span<float> stereo, in RenderTiming timing)
        {
            Interlocked.Increment(ref Calls);
            MaxFrames = Math.Max(MaxFrames, stereo.Length / 2);
            if (stereo.Length % 2 != 0) throw new InvalidOperationException("odd span");
            stereo.Fill(0.5f);      // proves the output replaces this with silence when we then throw
            throw new InvalidOperationException("boom");
        }
    }

    [Fact, Trait("Category", "Hardware")]
    public void Throwing_callback_is_counted_and_render_thread_survives()
    {
        var cb = new ThrowingCallback();
        using var output = new WasapiRenderOutput(null);
        output.Start(cb);
        Thread.Sleep(500);
        int callsAfterHalfSecond = cb.Calls;
        Thread.Sleep(500);

        Assert.True(cb.Calls > callsAfterHalfSecond, "render thread stopped after the callback threw");
        Assert.Equal(cb.Calls, output.CallbackFaults);
        output.Stop();
        log.WriteLine($"rate={output.SampleRate} periods/s~{cb.Calls} maxFrames={cb.MaxFrames}");
    }

    [Fact, Trait("Category", "Hardware")]
    public void Stop_is_idempotent_and_safe_from_many_threads()
    {
        var cb = new ThrowingCallback();
        var output = new WasapiRenderOutput(null);
        output.Start(cb);
        Thread.Sleep(100);

        Parallel.For(0, 8, _ => output.Stop());
        output.Stop();
        output.Dispose();
        Assert.Throws<ObjectDisposedException>(() => output.Start(cb));
    }

    [Fact, Trait("Category", "Hardware")]
    public void Dispose_without_start_and_double_start_are_handled()
    {
        new WasapiRenderOutput(null).Dispose();

        using var output = new WasapiRenderOutput(null);
        output.Start(new ThrowingCallback());
        Assert.Throws<InvalidOperationException>(() => output.Start(new ThrowingCallback()));
    }

    [Fact, Trait("Category", "Hardware")]
    public void Catalog_lists_default_device_and_explicit_id_works()
    {
        string? id = DeviceCatalog.DefaultRenderId();
        Assert.NotNull(id);
        var devices = DeviceCatalog.RenderDevices();
        Assert.Contains(devices, d => d.Id == id);
        foreach (var d in devices) log.WriteLine($"{d.Name} | {d.Id}");

        var factory = new WasapiOutputFactory(() => id);
        using var output = factory.Create();
        Assert.True(output.SampleRate > 0);
    }

    private sealed class SilentCallback : IRenderCallback
    {
        public void Render(Span<float> stereo, in RenderTiming timing) => stereo.Clear();
    }

    /// <summary>The app creates outputs on the WPF STA thread; Stop comes from elsewhere and must not stall on apartment marshalling.</summary>
    [Theory, Trait("Category", "Hardware")]
    [InlineData(false)]     // creating STA thread has exited (worst case for non-agile objects)
    [InlineData(true)]      // creating STA thread is alive and waiting (pumping wait)
    public void Output_created_and_started_on_sta_thread_stops_quickly_from_another_thread(bool keepStaAlive)
    {
        WasapiRenderOutput? output = null;
        Exception? failure = null;
        using var created = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var sta = new Thread(() =>
        {
            try
            {
                output = new WasapiRenderOutput(null);
                output.Start(new SilentCallback());
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
        output!.Stop();
        sw.Stop();
        release.Set();

        log.WriteLine($"Stop took {sw.ElapsedMilliseconds} ms (keepStaAlive={keepStaAlive})");
        Assert.True(sw.ElapsedMilliseconds < 1000, $"Stop took {sw.ElapsedMilliseconds} ms");
        Assert.True(output.IsReleased, "COM objects were not released");

        using var second = new WasapiRenderOutput(null);
        second.Start(new SilentCallback());
        Thread.Sleep(100);
        second.Stop();
    }

    [Fact, Trait("Category", "Hardware")]
    public void Default_device_change_is_reported_off_the_notification_thread_exactly_once_and_allows_stop()
    {
        using var output = new WasapiRenderOutput(null);
        output.Start(new SilentCallback());
        int raised = 0;
        int handlerThread = -1;
        using var done = new ManualResetEventSlim();
        output.DeviceInvalidated += () =>
        {
            Interlocked.Increment(ref raised);
            handlerThread = Environment.CurrentManagedThreadId;
            output.Stop();          // a subscriber stopping synchronously must be safe
            done.Set();
        };

        int callerThread = -1;
        var caller = new Thread(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            output.OnDefaultRenderChanged();    // stands in for the Core Audio notification thread
            output.OnDefaultRenderChanged();
        });
        caller.Start();
        caller.Join();

        Assert.True(done.Wait(2000), "DeviceInvalidated was not raised");
        Thread.Sleep(200);
        Assert.Equal(1, raised);
        Assert.NotEqual(callerThread, handlerThread);
        Assert.True(output.IsReleased);
    }
}
