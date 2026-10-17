using System.Net;
using RhythmSync.Core;
using RhythmSync.Core.Host;
using RhythmSync.Core.Protocol;
using RhythmSync.Tests.Sim;

namespace RhythmSync.Tests.Host;

public class HostPipelineTests
{
    private const uint SessionId = 0xAB;
    private const long Second = AudioFormat.TicksPerSecond;
    private const long T0 = 1_000_000_000_000;
    private static readonly IPEndPoint HostAudio = new(IPAddress.Parse("10.0.0.1"), 47802);
    private static readonly IPEndPoint HostClock = new(IPAddress.Parse("10.0.0.1"), 47803);
    private static readonly IPEndPoint Client = new(IPAddress.Parse("10.0.0.2"), 47802);

    [Fact]
    public void Captured_audio_is_stamped_and_streamed_and_pings_are_answered()
    {
        var sim = new SimScheduler(T0);
        var net = new SimNetwork(sim, 5, new SimLinkProfile(BaseDelayMs: 1, JitterMeanMs: 0, TailProbability: 0, LossProbability: 0));
        var hostClock = new DriftingClock(sim, 0, 0);
        using var pipeline = new HostPipeline(SessionId, net.Create(HostAudio), net.Create(HostClock), hostClock,
                                              captureSampleRate: 48000, targetLatencyMs: 200);
        pipeline.Streamer.AddClient(Guid.NewGuid(), Client);

        var playAts = new List<(uint Seq, long PlayAt)>();
        var clientCh = net.Create(Client);
        PongPacket? pong = null;
        clientCh.Start((data, _) =>
        {
            if (PacketSerializer.TryReadPong(data, out var p)) { pong = p; return; }
            var pcm = new short[AudioFormat.FrameFloats];
            Assert.True(PacketSerializer.TryReadAudio(data, out uint seq, out long playAt, pcm));
            playAts.Add((seq, playAt));
        });

        // 10 ms chunks; the device position of each chunk's first frame was captured at the chunk's start time.
        var chunk = new float[480 * 2];
        for (int i = 0; i < 100; i++)
        {
            long at = T0 + i * Second / 100;
            long position = i * 480L;
            sim.Schedule(at, () => pipeline.OnCaptured(chunk, position, hostClock.ToClock(at)));
        }
        var ping = new byte[PacketSerializer.PingBytes];
        PacketSerializer.WritePing(ping, new PingPacket(SessionId, 9, 1234));
        sim.Schedule(T0 + Second / 2, () => clientCh.Send(ping, HostClock));
        sim.RunUntil(T0 + 2 * Second);

        Assert.NotNull(pong);
        Assert.Equal(9u, pong.Value.PingId);
        Assert.InRange(playAts.Count, 195, 200);
        for (int i = 0; i < playAts.Count; i++)
        {
            Assert.Equal((uint)i, playAts[i].Seq);
            // Frame i starts at device position 240·i, captured at T0 + 5 ms·i; played 200 ms later.
            long expected = T0 + i * AudioFormat.FrameTicks + 200 * 10_000;
            Assert.InRange(playAts[i].PlayAt, expected - 10, expected + 10);
        }
    }
}
