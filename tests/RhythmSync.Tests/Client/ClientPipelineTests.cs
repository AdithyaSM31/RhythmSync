using RhythmSync.Core;
using RhythmSync.Core.Client;
using RhythmSync.Core.Clock;
using RhythmSync.Core.Protocol;
using RhythmSync.Tests.Sim;

namespace RhythmSync.Tests.Client;

public class ClientPipelineTests
{
    private const uint SessionId = 0x5EED;
    private const long Second = AudioFormat.TicksPerSecond;
    private const short Level = 8000;

    private static long PlayAt(uint seq) => 2_000_000 + seq * AudioFormat.FrameTicks;   // first frame due at 200 ms

    private static byte[] AudioPacket(uint sessionId, uint seq, long playAt, short level = Level)
    {
        var pcm = new short[AudioFormat.FrameFloats];
        Array.Fill(pcm, level);
        var packet = new byte[PacketSerializer.AudioPacketBytes];
        PacketSerializer.WriteAudio(packet, sessionId, seq, playAt, pcm);
        return packet;
    }

    [Fact]
    public void Client_pipeline_drops_wrong_session_and_garbage()
    {
        var pipeline = new ClientPipeline(SessionId, new IdentityClockMapping(), 48000);

        // A stale packet from another session with a huge seq arrives first: it must not anchor the buffer.
        pipeline.HandleAudioDatagram(AudioPacket(SessionId + 1, 4_000_000_000, PlayAt(0)));

        var valid = AudioPacket(SessionId, 0, PlayAt(0));
        var ping = new byte[PacketSerializer.PingBytes];
        PacketSerializer.WritePing(ping, new PingPacket(SessionId, 1, 0));
        var badMagic = (byte[])valid.Clone();
        badMagic[0] ^= 0xFF;
        var padded = new byte[valid.Length + 1];
        valid.CopyTo(padded, 0);
        var wrongSampleCount = (byte[])valid.Clone();
        wrongSampleCount[20] = 239;

        pipeline.HandleAudioDatagram(ReadOnlySpan<byte>.Empty);
        pipeline.HandleAudioDatagram(new byte[] { 1, 2, 3 });
        pipeline.HandleAudioDatagram(new byte[PacketSerializer.AudioPacketBytes]);
        pipeline.HandleAudioDatagram(valid.AsSpan(0, valid.Length - 1));
        pipeline.HandleAudioDatagram(padded);
        pipeline.HandleAudioDatagram(badMagic);
        pipeline.HandleAudioDatagram(wrongSampleCount);
        pipeline.HandleAudioDatagram(ping);

        Assert.Equal(0, pipeline.Buffer.Received);
        Assert.Equal(0, pipeline.Buffer.Rejected);

        // Correct frames that follow are accepted and actually play.
        var sim = new SimScheduler();
        uint next = 0;
        var output = new FakeOutput(sim, new DriftingClock(sim, 0, 0), 48000, 0, 480)
        {
            // Each frame arrives 100 ms before it is due.
            BeforeEachCallback = () =>
            {
                for (; PlayAt(next) <= sim.Now + 1_000_000; next++)
                    pipeline.HandleAudioDatagram(AudioPacket(SessionId, next, PlayAt(next)));
            },
        };
        output.Start(pipeline.Scheduler);
        sim.RunUntil(Second / 2);

        Assert.Equal(PlaybackState.Playing, pipeline.Scheduler.State);
        Assert.Equal(next, pipeline.Buffer.Received);
        Assert.Equal(0, pipeline.Buffer.Rejected);
        float expected = Level / 32768f;
        Assert.Contains(output.Samples, v => Math.Abs(v - expected) < 1e-4);
    }

    [Fact]
    public void Gain_is_applied_through_the_dsp_chain()
    {
        var pipeline = new ClientPipeline(SessionId, new IdentityClockMapping(), 48000);
        pipeline.Gain.Gain = 0.5f;
        var sim = new SimScheduler();
        var output = new FakeOutput(sim, new DriftingClock(sim, 0, 0), 48000, 0, 480);
        for (uint s = 0; s < 100; s++) pipeline.HandleAudioDatagram(AudioPacket(SessionId, s, PlayAt(s)));
        output.Start(pipeline.Scheduler);
        sim.RunUntil(Second / 2);

        // GainProcessor ramps to the new gain at the start of playback; well into it every sample is at half level.
        float half = 0.5f * Level / 32768f;
        Assert.All(output.Samples.TakeLast(4800), v => Assert.Equal(half, v, 1e-4));
    }

    [Fact]
    public void OnFrame_inserts_into_the_buffer()
    {
        var pipeline = new ClientPipeline(SessionId, new IdentityClockMapping(), 48000);
        var pcm = new short[AudioFormat.FrameFloats];
        pipeline.OnFrame(0, PlayAt(0), pcm);
        pipeline.OnFrame(1, PlayAt(1), pcm);
        Assert.Equal(2, pipeline.Buffer.Received);
        Assert.True(pipeline.Buffer.Contains(1));
    }

    [Fact]
    public void ReplaceOutputRate_keeps_buffer_dsp_gain_and_offset()
    {
        var pipeline = new ClientPipeline(SessionId, new IdentityClockMapping(), 48000);
        var buffer = pipeline.Buffer;
        var dsp = pipeline.Dsp;
        var gain = pipeline.Gain;
        var old = pipeline.Scheduler;
        old.SetOffset(-37.5, 200);
        pipeline.HandleAudioDatagram(AudioPacket(SessionId, 0, PlayAt(0)));

        pipeline.ReplaceOutputRate(44100);

        Assert.NotSame(old, pipeline.Scheduler);
        Assert.Same(buffer, pipeline.Buffer);
        Assert.Same(dsp, pipeline.Dsp);
        Assert.Same(gain, pipeline.Gain);
        Assert.Equal(-37.5, pipeline.Scheduler.EffectiveOffsetMs);
        Assert.True(pipeline.Buffer.Contains(0));

        // The new scheduler renders at 44.1 kHz from the same buffer.
        var sim = new SimScheduler();
        var output = new FakeOutput(sim, new DriftingClock(sim, 0, 0), 44100, 0, 441);
        for (uint s = 1; s < 100; s++) pipeline.HandleAudioDatagram(AudioPacket(SessionId, s, PlayAt(s)));
        output.Start(pipeline.Scheduler);
        sim.RunUntil(Second / 2);
        Assert.Equal(PlaybackState.Playing, pipeline.Scheduler.State);
    }
}
