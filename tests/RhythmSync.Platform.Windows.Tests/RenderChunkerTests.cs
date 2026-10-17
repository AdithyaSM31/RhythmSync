using RhythmSync.Core.Audio;
using RhythmSync.Platform.Windows;

namespace RhythmSync.Platform.Windows.Tests;

public class RenderChunkerTests
{
    private sealed class Capture : IRenderCallback
    {
        public readonly List<(int Length, RenderTiming Timing)> Calls = new();
        public void Render(Span<float> stereo, in RenderTiming timing) => Calls.Add((stereo.Length, timing));
    }

    [Fact]
    public void Buffers_over_one_second_are_split_into_even_chunks_of_at_most_one_second()
    {
        var cb = new Capture();
        var buffer = new float[2 * (48_000 * 2 + 100)];     // 2 s + 100 frames

        RenderChunker.Render(cb, buffer, 48_000, devicePosition: 7, devicePositionClientTicks: 99, writePosition: 1000);

        Assert.Equal(new[] { 96_000, 96_000, 200 }, cb.Calls.Select(c => c.Length));
        Assert.All(cb.Calls, c => Assert.True(c.Length % 2 == 0 && c.Length / 2 <= 48_000));
        Assert.Equal(new long[] { 1000, 1000 + 48_000, 1000 + 96_000 }, cb.Calls.Select(c => c.Timing.WritePositionFrames));
        Assert.All(cb.Calls, c => { Assert.Equal(7, c.Timing.DevicePositionFrames); Assert.Equal(99, c.Timing.DevicePositionClientTicks); });
    }

    [Fact]
    public void Normal_period_is_a_single_call()
    {
        var cb = new Capture();
        RenderChunker.Render(cb, new float[960], 48_000, 0, 0, 0);
        Assert.Single(cb.Calls);
        Assert.Equal(960, cb.Calls[0].Length);
    }

    [Fact]
    public void Exactly_one_second_is_one_call_and_empty_is_none()
    {
        var cb = new Capture();
        RenderChunker.Render(cb, new float[96_000], 48_000, 0, 0, 0);
        Assert.Single(cb.Calls);
        RenderChunker.Render(cb, Span<float>.Empty, 48_000, 0, 0, 0);
        Assert.Single(cb.Calls);
    }

    [Fact]
    public void Odd_length_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => RenderChunker.Render(new Capture(), new float[3], 48_000, 0, 0, 0));
    }
}
