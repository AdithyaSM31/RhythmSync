using RhythmSync.Core;
using RhythmSync.Core.Host;

namespace RhythmSync.Tests.Host;

public class FrameAssemblerTests
{
    private sealed class RecordingSink : IFrameSink
    {
        public readonly List<(uint Seq, long PlayAt)> Frames = new();
        public readonly List<short[]> Pcm = new();
        public bool KeepPcm;

        public void OnFrame(uint seq, long playAt, ReadOnlySpan<short> pcm)
        {
            Assert.Equal(AudioFormat.FrameFloats, pcm.Length);
            Frames.Add((seq, playAt));
            if (KeepPcm) Pcm.Add(pcm.ToArray());
        }
    }

    private static CaptureTimestamper Stamper(int rate, long sessionAtZero = 0)
    {
        var ts = new CaptureTimestamper(rate);
        ts.AddObservation(0, sessionAtZero);
        ts.AddObservation(rate, sessionAtZero + AudioFormat.TicksPerSecond);   // exactly nominal
        return ts;
    }

    private static float[] Sine(int frames, int rate)
    {
        var data = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            float v = 0.4f * MathF.Sin(2 * MathF.PI * 440f * i / rate);
            data[2 * i] = v;
            data[2 * i + 1] = -v;
        }
        return data;
    }

    private static float[] Constant(int frames, float value)
    {
        var data = new float[frames * 2];
        Array.Fill(data, value);
        return data;
    }

    [Fact]
    public void Emits_240_frame_packets_with_consecutive_seq()
    {
        var sink = new RecordingSink();
        var ts = Stamper(48000);
        var asm = new FrameAssembler(48000, ts, sink, targetLatencyMs: 200);

        const int total = 240_000;
        var audio = Sine(total, 48000);
        int[] sizes = { 137, 441, 1000 };
        int pos = 0, n = 0;
        while (pos < total)
        {
            int len = Math.Min(sizes[n++ % sizes.Length], total - pos);
            asm.Push(audio.AsSpan(pos * 2, len * 2), pos);
            pos += len;
        }

        // 1000 frames' worth of audio; the cubic taps hold back the newest ~2 device frames, so the 1000th
        // frame completes on the next push.
        Assert.InRange(sink.Frames.Count, 999, 1000);
        for (int i = 0; i < sink.Frames.Count; i++)
        {
            Assert.Equal((uint)i, sink.Frames[i].Seq);
            if (i > 0) Assert.InRange(sink.Frames[i].PlayAt - sink.Frames[i - 1].PlayAt, 50_000 - 10, 50_000 + 10);
        }
        Assert.Equal(ts.SessionTimeOf(0) + 2_000_000, sink.Frames[0].PlayAt);
    }

    [Fact]
    public void Resamples_44100_input()
    {
        var sink = new RecordingSink();
        var asm = new FrameAssembler(44100, Stamper(44100), sink, targetLatencyMs: 200);

        var audio = Sine(88_200, 44100);   // 2 s
        for (int pos = 0; pos < 88_200; pos += 441)
            asm.Push(audio.AsSpan(pos * 2, 441 * 2), pos);

        Assert.InRange(sink.Frames.Count, 398, 402);
    }

    [Fact]
    public void Resampled_44100_playAt_follows_device_position()
    {
        var sink = new RecordingSink();
        var ts = Stamper(44100);
        var asm = new FrameAssembler(44100, ts, sink, targetLatencyMs: 0);

        asm.Push(Sine(44_100, 44100), 0);

        // Frame k starts at device position k * 240 * 44100 / 48000 = k * 220.5.
        Assert.Equal(ts.SessionTimeOf(10 * 220.5), sink.Frames[10].PlayAt);
    }

    [Fact]
    public void Preserves_signal_at_48k()
    {
        var sink = new RecordingSink { KeepPcm = true };
        var asm = new FrameAssembler(48000, Stamper(48000), sink, targetLatencyMs: 200, ditherSeed: 1);

        var audio = Sine(2400, 48000);
        asm.Push(audio, 0);

        for (int f = 0; f < sink.Pcm.Count; f++)
            for (int i = 0; i < AudioFormat.FrameFloats; i++)
                Assert.InRange(sink.Pcm[f][i], audio[f * AudioFormat.FrameFloats + i] * 32767 - 2, audio[f * AudioFormat.FrameFloats + i] * 32767 + 2);
    }

    [Fact]
    public void Scales_dithers_and_clamps()
    {
        var half = new RecordingSink { KeepPcm = true };
        new FrameAssembler(48000, Stamper(48000), half, 200, ditherSeed: 7).Push(Constant(2400, 0.5f), 0);
        Assert.NotEmpty(half.Pcm);
        Assert.All(half.Pcm.SelectMany(p => p), s => Assert.InRange(s, 16384 - 2, 16384 + 2));

        var loud = new RecordingSink { KeepPcm = true };
        new FrameAssembler(48000, Stamper(48000), loud, 200).Push(Constant(2400, 1.5f), 0);
        Assert.NotEmpty(loud.Pcm);
        Assert.All(loud.Pcm.SelectMany(p => p), s => Assert.Equal(32767, s));

        var neg = new RecordingSink { KeepPcm = true };
        new FrameAssembler(48000, Stamper(48000), neg, 200).Push(Constant(2400, -1.5f), 0);
        Assert.All(neg.Pcm.SelectMany(p => p), s => Assert.Equal(-32767, s));
    }

    [Fact]
    public void Latency_change_shifts_playAt()
    {
        var sink = new RecordingSink();
        var asm = new FrameAssembler(48000, Stamper(48000), sink, targetLatencyMs: 200);

        asm.Push(Sine(4802, 48000), 0);
        int before = sink.Frames.Count;
        Assert.True(before > 0);

        asm.TargetLatencyMs = 300;
        asm.Push(Sine(2400, 48000), 4802);

        Assert.True(sink.Frames.Count > before);
        long jump = sink.Frames[before].PlayAt - sink.Frames[before - 1].PlayAt - AudioFormat.FrameTicks;
        Assert.InRange(jump, 1_000_000 - 10, 1_000_000 + 10);
    }

    [Fact]
    public void Position_jump_reanchors_and_playAt_follows_timestamper()
    {
        var sink = new RecordingSink();
        var asm = new FrameAssembler(48000, Stamper(48000), sink, targetLatencyMs: 200);

        // 240 * 10 + 2 frames emit exactly 10 frames, leaving only the 2 tap-lookahead frames pending.
        asm.Push(Sine(2402, 48000), 0);
        Assert.Equal(10, sink.Frames.Count);

        // The device skips ahead by 4800 frames (100 ms).
        asm.Push(Sine(480, 48000), 2402 + 4800);

        Assert.Equal(11, sink.Frames.Count);
        Assert.Equal(10u, sink.Frames[10].Seq);
        long jump = sink.Frames[10].PlayAt - sink.Frames[9].PlayAt - AudioFormat.FrameTicks;
        Assert.InRange(jump, 1_000_000 - 1000, 1_000_000 + 1000);
    }
}
