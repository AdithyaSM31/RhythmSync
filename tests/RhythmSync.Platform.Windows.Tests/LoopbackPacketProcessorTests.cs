using System.Runtime.InteropServices;
using RhythmSync.Core.Audio;
using RhythmSync.Platform.Windows;

namespace RhythmSync.Platform.Windows.Tests;

public class LoopbackPacketProcessorTests
{
    private sealed record Delivery(float[] Samples, long Position, long Qpc);

    private sealed class Recorder
    {
        public readonly List<Delivery> Calls = new();
        public void Handle(ReadOnlySpan<float> stereo, long position, long qpc) =>
            Calls.Add(new Delivery(stereo.ToArray(), position, qpc));
    }

    private static byte[] Bytes(float[] samples) => MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();

    [Fact]
    public void Stereo_float_is_delivered_unchanged_with_position_and_qpc_passed_through()
    {
        var p = new LoopbackPacketProcessor(SampleKind.Float32, 2, 48000, 480);
        var rec = new Recorder();
        var samples = new[] { 0.1f, -0.2f, 0.3f, -0.4f };

        p.Process(Bytes(samples), 2, silent: false, 123_456, 777_000_000, rec.Handle);

        var d = Assert.Single(rec.Calls);
        Assert.Equal(samples, d.Samples);
        Assert.Equal(123_456, d.Position);
        Assert.Equal(777_000_000, d.Qpc);
    }

    [Fact]
    public void Silent_flag_delivers_zeros_even_if_the_buffer_holds_data()
    {
        var p = new LoopbackPacketProcessor(SampleKind.Float32, 2, 48000, 480);
        var rec = new Recorder();

        p.Process(Bytes(new[] { 0.9f, 0.9f, 0.9f, 0.9f }), 2, silent: true, 10, 20, rec.Handle);

        var d = Assert.Single(rec.Calls);
        Assert.Equal(new float[4], d.Samples);
        Assert.Equal(10, d.Position);
    }

    [Fact]
    public void Surround_float_is_downmixed_to_stereo()
    {
        var p = new LoopbackPacketProcessor(SampleKind.Float32, 6, 48000, 480);
        var rec = new Recorder();

        p.Process(Bytes(new[] { 0.1f, 0.2f, 0.3f, 0.9f, 0.4f, 0.5f }), 1, false, 0, 0, rec.Handle);

        var d = Assert.Single(rec.Calls);
        Assert.Equal(2, d.Samples.Length);
        Assert.Equal(0.1f + 0.707f * 0.3f + 0.707f * 0.4f, d.Samples[0], 1e-6f);
        Assert.Equal(0.2f + 0.707f * 0.3f + 0.707f * 0.5f, d.Samples[1], 1e-6f);
    }

    [Fact]
    public void Int16_mono_is_converted_and_duplicated()
    {
        var p = new LoopbackPacketProcessor(SampleKind.Int16, 1, 48000, 480);
        var rec = new Recorder();
        var raw = new byte[4];
        MemoryMarshal.Cast<byte, short>(raw.AsSpan())[0] = short.MinValue;
        MemoryMarshal.Cast<byte, short>(raw.AsSpan())[1] = 16384;

        p.Process(raw, 2, false, 0, 0, rec.Handle);

        var d = Assert.Single(rec.Calls);
        Assert.Equal(new[] { -1f, -1f, 0.5f, 0.5f }, d.Samples);
    }

    [Fact]
    public void Packets_larger_than_capacity_are_delivered_in_chunks_with_advancing_timestamps()
    {
        var p = new LoopbackPacketProcessor(SampleKind.Float32, 2, 48000, 100);
        var rec = new Recorder();
        var samples = new float[250 * 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = i;

        p.Process(Bytes(samples), 250, false, 1000, 5_000_000, rec.Handle);

        Assert.Equal(new[] { 200, 200, 100 }, rec.Calls.Select(c => c.Samples.Length));
        Assert.Equal(new long[] { 1000, 1100, 1200 }, rec.Calls.Select(c => c.Position));
        // 100 frames at 48 kHz = 20_833.33 ticks of 100 ns
        Assert.Equal(new long[] { 5_000_000, 5_000_000 + 20_833, 5_000_000 + 41_666 }, rec.Calls.Select(c => c.Qpc));
        Assert.Equal(samples, rec.Calls.SelectMany(c => c.Samples));
    }

    [Fact]
    public void Handler_exceptions_are_counted_and_do_not_stop_later_chunks_or_packets()
    {
        var p = new LoopbackPacketProcessor(SampleKind.Float32, 2, 48000, 100);
        int calls = 0;
        void Throwing(ReadOnlySpan<float> s, long pos, long qpc)
        {
            calls++;
            throw new InvalidOperationException("boom");
        }

        p.Process(new byte[250 * 8], 250, false, 0, 0, Throwing);
        p.Process(new byte[10 * 8], 10, false, 250, 0, Throwing);

        Assert.Equal(4, calls);
        Assert.Equal(4, p.HandlerFaults);
    }

    private static int _sink;
    private static void CountingHandler(ReadOnlySpan<float> s, long pos, long qpc) => _sink += s.Length;

    [Theory]
    [InlineData((int)SampleKind.Float32, 2)]
    [InlineData((int)SampleKind.Float32, 6)]
    [InlineData((int)SampleKind.Int16, 2)]
    [InlineData((int)SampleKind.Int24, 2)]
    [InlineData((int)SampleKind.Int32, 8)]
    public void Processing_does_not_allocate(int kindValue, int channels)
    {
        var kind = (SampleKind)kindValue;
        var p = new LoopbackPacketProcessor(kind, channels, 48000, 480);
        var raw = new byte[480 * channels * MixFormat.BytesPerSample(kind)];
        var handler = new CaptureHandler(CountingHandler);
        for (int i = 0; i < 10; i++) p.Process(raw, 480, false, 0, 0, handler);    // warm up

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) p.Process(raw, 480, i % 2 == 0, i * 480L, i, handler);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }
}
