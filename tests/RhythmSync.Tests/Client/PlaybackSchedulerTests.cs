using RhythmSync.Core;
using RhythmSync.Core.Audio;
using RhythmSync.Core.Client;
using RhythmSync.Core.Clock;
using RhythmSync.Tests.Sim;

namespace RhythmSync.Tests.Client;

public class PlaybackSchedulerTests
{
    private const long Second = AudioFormat.TicksPerSecond;
    private const double BoundSamples = 24;   // 0.5 ms at 48 kHz

    /// <summary>
    /// Host = client = true time (identity mapping, client clock offset 0 / drift 0). Frames carry a 440 Hz sine
    /// (amplitude 0.5) and are delivered before each callback while playAt ≤ now + 300 ms.
    /// </summary>
    private sealed class Rig
    {
        public const long T0 = 1_000_000_000_000;   // ~28 h: realistic absolute session-clock values
        private const long LeadTicks = 3_000_000;   // delivered 300 ms before playAt

        public readonly SimScheduler Sim = new(T0);
        public readonly JitterBuffer Buffer = new();
        public readonly PlaybackScheduler Scheduler;
        public readonly FakeOutput Output;
        public bool Feed = true;
        public Func<uint, bool> Skip = _ => false;
        public Func<uint, long> PlayAtShift = _ => 0;

        private readonly short[] _pcm = new short[AudioFormat.FrameFloats];
        private uint _next;

        public Rig(int sampleRate = 48000, int period = 480, double deviceDriftPpm = 0)
        {
            var clientClock = new DriftingClock(Sim, offsetTicks: 0, driftPpm: 0);
            Scheduler = new PlaybackScheduler(Buffer, new IdentityClockMapping(), sampleRate, new DspChain());
            Output = new FakeOutput(Sim, clientClock, sampleRate, deviceDriftPpm, period)
            {
                BeforeEachCallback = Deliver,
                StreamPositionProbe = () => Scheduler.LastRenderStreamPosition,
            };
        }

        public static long PlayAt(uint seq) => T0 + 2_000_000 + seq * AudioFormat.FrameTicks;

        public void Run(double seconds)
        {
            Output.Start(Scheduler);
            Sim.RunUntil(T0 + (long)(seconds * Second));
        }

        /// <summary>Stream position that should be heard at true time <paramref name="heard"/>.</summary>
        public static double Truth(long heard, long offsetTicks = 0, long shiftTicks = 0) =>
            (heard - offsetTicks - shiftTicks - PlayAt(0)) * (double)AudioFormat.SampleRate / AudioFormat.TicksPerSecond;

        private void Deliver()
        {
            if (!Feed) return;
            while (PlayAt(_next) + PlayAtShift(_next) <= Sim.Now + LeadTicks)
            {
                if (!Skip(_next))
                {
                    for (int i = 0; i < AudioFormat.FrameSamples; i++)
                    {
                        long s = (long)_next * AudioFormat.FrameSamples + i;
                        short v = (short)Math.Round(0.5 * 32767 * Math.Sin(2 * Math.PI * 440 * s / AudioFormat.SampleRate));
                        _pcm[2 * i] = v;
                        _pcm[2 * i + 1] = v;
                    }
                    Assert.True(Buffer.Insert(_next, PlayAt(_next) + PlayAtShift(_next), _pcm));
                }
                _next++;
            }
        }
    }

    private static void AssertTracks(Rig rig, double fromSeconds, Func<long, double> truth)
    {
        long from = Rig.T0 + (long)(fromSeconds * Second);
        int checkedEntries = 0;
        double worst = 0;
        foreach (var (heard, pos) in rig.Output.Heard)
        {
            if (heard < from) continue;
            Assert.False(double.IsNaN(pos), $"silent at {(heard - Rig.T0) / (double)Second:F3} s");
            double error = Math.Abs(pos - truth(heard));
            worst = Math.Max(worst, error);
            checkedEntries++;
        }
        Assert.True(checkedEntries > 100, $"only {checkedEntries} entries checked");
        Assert.True(worst < BoundSamples, $"worst |pos − truth| = {worst:F2} samples (bound {BoundSamples})");
    }

    /// <summary>Largest per-channel sample-to-sample step in interleaved stereo.</summary>
    private static double MaxDelta(List<float> samples)
    {
        double max = 0;
        for (int i = 2; i < samples.Count; i++)
            max = Math.Max(max, Math.Abs(samples[i] - samples[i - 2]));
        return max;
    }

    [Fact]
    public void Outputs_silence_while_waiting()
    {
        var rig = new Rig { Feed = false };
        rig.Run(1);
        Assert.Equal(PlaybackState.WaitingForData, rig.Scheduler.State);
        Assert.All(rig.Output.Samples, s => Assert.Equal(0f, s));
        Assert.All(rig.Output.Heard, h => Assert.True(double.IsNaN(h.StreamPos)));
    }

    [Fact]
    public void Starts_on_time()
    {
        var rig = new Rig();
        rig.Run(1);
        Assert.Equal(PlaybackState.Playing, rig.Scheduler.State);
        var first = rig.Output.Heard.First(h => !double.IsNaN(h.StreamPos));
        Assert.InRange(first.StreamPos - Rig.Truth(first.TrueTicksHeard), -BoundSamples, BoundSamples);
    }

    [Fact]
    public void Converges_with_100ppm_device_drift()
    {
        var rig = new Rig(deviceDriftPpm: 100);
        rig.Run(30);
        AssertTracks(rig, 10, h => Rig.Truth(h));
        Assert.Equal(0, rig.Scheduler.GetStats().Resyncs);
    }

    [Fact]
    public void Converges_on_44100_device()
    {
        var rig = new Rig(sampleRate: 44100, period: 441, deviceDriftPpm: 100);
        rig.Run(30);
        AssertTracks(rig, 10, h => Rig.Truth(h));
        Assert.Equal(0, rig.Scheduler.GetStats().Resyncs);
    }

    [Fact]
    public void Offset_shifts_playback()
    {
        var rig = new Rig(deviceDriftPpm: 100);
        rig.Scheduler.SetOffset(10, 200);
        rig.Run(30);
        AssertTracks(rig, 10, h => Rig.Truth(h, offsetTicks: 100_000));
        Assert.Equal(0, rig.Scheduler.GetStats().Resyncs);
    }

    [Fact]
    public void Offset_is_clamped()
    {
        var scheduler = new PlaybackScheduler(new JitterBuffer(), new IdentityClockMapping(), 48000, new DspChain());
        Assert.Equal(0, scheduler.EffectiveOffsetMs);
        scheduler.SetOffset(-100, 100);
        Assert.Equal(-50, scheduler.EffectiveOffsetMs);
        scheduler.SetOffset(150, 200);
        Assert.Equal(100, scheduler.EffectiveOffsetMs);
        scheduler.SetOffset(-100, 200);
        Assert.Equal(-100, scheduler.EffectiveOffsetMs);
    }

    [Fact]
    public void Latency_jump_resyncs_once_without_click()
    {
        const uint jumpSeq = 2000;              // playAt ≈ 10.2 s; first delivered at ≈ 10 s
        const long jumpTicks = 1_000_000;       // +100 ms
        var rig = new Rig(deviceDriftPpm: 100) { PlayAtShift = seq => seq >= jumpSeq ? jumpTicks : 0 };
        rig.Run(30);
        Assert.Equal(1, rig.Scheduler.GetStats().Resyncs);
        Assert.True(MaxDelta(rig.Output.Samples) < 0.06, $"click: max delta {MaxDelta(rig.Output.Samples):F4}");
        AssertTracks(rig, 20, h => Rig.Truth(h, shiftTicks: jumpTicks));
    }

    [Fact]
    public void Latency_drop_resyncs_forward_once_without_click()
    {
        const uint jumpSeq = 2000;
        const long jumpTicks = -1_000_000;      // later frames due 100 ms earlier: playback falls behind
        var rig = new Rig(deviceDriftPpm: 100) { PlayAtShift = seq => seq >= jumpSeq ? jumpTicks : 0 };
        rig.Run(30);
        Assert.Equal(1, rig.Scheduler.GetStats().Resyncs);
        Assert.True(MaxDelta(rig.Output.Samples) < 0.06, $"click: max delta {MaxDelta(rig.Output.Samples):F4}");
        AssertTracks(rig, 20, h => Rig.Truth(h, shiftTicks: jumpTicks));
    }

    [Fact]
    public void Missing_frames_are_concealed()
    {
        var rig = new Rig(deviceDriftPpm: 100) { Skip = seq => seq % 50 == 49 };
        rig.Run(30);
        Assert.True(rig.Scheduler.GetStats().ConcealedFrames > 0);
        AssertTracks(rig, 10, h => Rig.Truth(h));
        Assert.Equal(0, rig.Scheduler.GetStats().Resyncs);
    }

    [Fact]
    public void Render_does_not_allocate()
    {
        var rig = new Rig(deviceDriftPpm: 100);
        rig.Output.BeforeEachCallback = null;
        var pcm = new short[AudioFormat.FrameFloats];
        for (uint seq = 0; seq < 200; seq++) rig.Buffer.Insert(seq, Rig.PlayAt(seq), pcm);
        var buf = new float[960];
        long clientTicks = Rig.PlayAt(0) - 100_000;
        void RenderOnce(int k) =>
            rig.Scheduler.Render(buf, new RenderTiming(k * 480L, clientTicks + k * 100_000L, (k + 1) * 480L));

        for (int k = 0; k < 20; k++) RenderOnce(k);                        // warm up: waiting → playing
        Assert.Equal(PlaybackState.Playing, rig.Scheduler.State);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int k = 20; k < 90; k++) RenderOnce(k);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }
}
