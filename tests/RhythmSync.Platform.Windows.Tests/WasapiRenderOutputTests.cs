using RhythmSync.Core.Audio;
using RhythmSync.Core.Clock;
using RhythmSync.Platform.Windows;

namespace RhythmSync.Platform.Windows.Tests;

public class WasapiRenderOutputTests(Xunit.Abstractions.ITestOutputHelper log)
{
    private sealed class Recorder : IRenderCallback
    {
        public readonly List<(RenderTiming Timing, int Frames, long NowTicks)> Calls = new();
        private readonly StopwatchClock _clock = new();

        public void Render(Span<float> stereo, in RenderTiming timing)
        {
            stereo.Clear();
            lock (Calls) Calls.Add((timing, stereo.Length / 2, _clock.NowTicks));
        }
    }

    [Fact, Trait("Category", "Hardware")]
    public void Renders_and_reports_sane_timing()
    {
        var rec = new Recorder();
        bool invalidated = false;
        using var output = new WasapiRenderOutput(null);
        output.DeviceInvalidated += () => invalidated = true;
        Assert.True(output.SampleRate >= 8000);

        output.Start(rec);
        Thread.Sleep(2000);
        output.Stop();

        Assert.False(invalidated);
        Assert.Equal(0L, output.CallbackFaults);

        (RenderTiming Timing, int Frames, long NowTicks)[] calls;
        lock (rec.Calls) calls = rec.Calls.ToArray();
        Assert.True(calls.Length > 100, $"expected ~200 periods in 2 s, got {calls.Length}");

        long prevDevice = long.MinValue, prevWrite = long.MinValue;
        var leads = new List<long>();
        int index = 0;
        foreach (var c in calls)
        {
            Assert.True(c.Frames > 0);
            Assert.True(c.Timing.DevicePositionFrames >= prevDevice, "device position went backwards");
            Assert.True(c.Timing.WritePositionFrames >= prevWrite, "write position went backwards");
            Assert.True(Math.Abs(c.Timing.DevicePositionClientTicks - c.NowTicks) < 500_000,
                $"device clock tick {c.Timing.DevicePositionClientTicks} too far from now {c.NowTicks}");
            long lead = c.Timing.WritePositionFrames - c.Timing.DevicePositionFrames;
            if (index++ >= 5)       // let the first few periods settle
            {
                Assert.InRange(lead, 1, output.SampleRate / 10 - 1);    // 0 < lead < 100 ms
                leads.Add(lead);
            }
            prevDevice = c.Timing.DevicePositionFrames;
            prevWrite = c.Timing.WritePositionFrames;
        }
        // Lead = endpoint latency, constant in steady state. Individual periods wobble with OS wake-up jitter (the
        // position and QPC are sampled together, so that is not a timing error), hence: nearly all periods within
        // 1 ms of the median, every period within 5 ms. A re-base bug (lead collapsing to 0) would break both.
        var sorted = leads.OrderBy(l => l).ToArray();
        long median = sorted[sorted.Length / 2];
        double ms(long frames) => frames * 1000.0 / output.SampleRate;
        int outside1ms = leads.Count(l => ms(Math.Abs(l - median)) >= 1.0);
        log.WriteLine($"lead frames median={median} min={sorted[0]} max={sorted[^1]} ({ms(median):F1} ms); {outside1ms}/{leads.Count} periods >= 1 ms from median");
        Assert.True(outside1ms <= leads.Count / 20, $"lead unstable: {outside1ms}/{leads.Count} periods >= 1 ms from median {median}");
        Assert.True(ms(sorted[^1] - median) < 5.0 && ms(median - sorted[0]) < 5.0, $"lead range {sorted[0]}..{sorted[^1]} frames");
    }
}
