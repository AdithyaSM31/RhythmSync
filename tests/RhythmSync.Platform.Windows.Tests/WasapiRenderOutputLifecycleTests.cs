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
}
