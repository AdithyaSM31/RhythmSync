using RhythmSync.Core.Audio;
using RhythmSync.Core.Clock;
using RhythmSync.Platform.Windows;

namespace RhythmSync.Platform.Windows.Tests;

public class WasapiRenderOutputTests
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
        foreach (var c in calls)
        {
            Assert.True(c.Frames > 0);
            Assert.True(c.Timing.DevicePositionFrames >= prevDevice, "device position went backwards");
            Assert.True(c.Timing.WritePositionFrames >= prevWrite, "write position went backwards");
            Assert.True(c.Timing.WritePositionFrames >= c.Timing.DevicePositionFrames, "write position behind device position");
            Assert.True(Math.Abs(c.Timing.DevicePositionClientTicks - c.NowTicks) < 500_000,
                $"device clock tick {c.Timing.DevicePositionClientTicks} too far from now {c.NowTicks}");
            prevDevice = c.Timing.DevicePositionFrames;
            prevWrite = c.Timing.WritePositionFrames;
        }
    }
}
