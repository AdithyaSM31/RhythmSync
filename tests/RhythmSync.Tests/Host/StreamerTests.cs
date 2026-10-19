using System.Net;
using RhythmSync.Core;
using RhythmSync.Core.Host;
using RhythmSync.Core.Net;
using RhythmSync.Core.Protocol;
using RhythmSync.Tests.Sim;

namespace RhythmSync.Tests.Host;

public class StreamerTests
{
    private const uint SessionId = 77;
    private static readonly IPEndPoint HostAudio = new(IPAddress.Parse("10.0.0.1"), 47802);
    private static readonly IPEndPoint ClientA = new(IPAddress.Parse("10.0.0.2"), 47802);
    private static readonly IPEndPoint ClientB = new(IPAddress.Parse("10.0.0.3"), 47802);

    private sealed record Frame(uint Seq, long PlayAt, short[] Pcm);

    private sealed class RecordingSink : IFrameSink
    {
        public List<Frame> Frames { get; } = new();
        public void OnFrame(uint seq, long playAt, ReadOnlySpan<short> pcm) => Frames.Add(new Frame(seq, playAt, pcm.ToArray()));
    }

    /// <summary>Counts sends without allocating.</summary>
    private sealed class CountingChannel : IDatagramChannel
    {
        public int Sends;
        public int LastLength;
        public IPEndPoint LocalEndPoint => HostAudio;
        public void Send(ReadOnlySpan<byte> datagram, IPEndPoint to) { Sends++; LastLength = datagram.Length; }
        public void Start(DatagramHandler handler) { }
        public void Dispose() { }
    }

    private static short[] Pcm(uint seq)
    {
        var pcm = new short[AudioFormat.FrameFloats];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)(seq * 1000 + i - 240);
        return pcm;
    }

    private static List<Frame> Listen(SimNetwork net, IPEndPoint at)
    {
        var frames = new List<Frame>();
        var ch = net.Create(at);
        ch.Start((data, from) =>
        {
            Assert.Equal(HostAudio, from);
            Assert.True(PacketSerializer.TryReadHeader(data, out var type, out var session));
            Assert.Equal(PacketType.Audio, type);
            Assert.Equal(SessionId, session);
            var pcm = new short[AudioFormat.FrameFloats];
            Assert.True(PacketSerializer.TryReadAudio(data, out uint seq, out long playAt, pcm));
            frames.Add(new Frame(seq, playAt, pcm));
        });
        return frames;
    }

    private static void AssertSame(IReadOnlyList<Frame> expected, IReadOnlyList<Frame> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Seq, actual[i].Seq);
            Assert.Equal(expected[i].PlayAt, actual[i].PlayAt);
            Assert.Equal(expected[i].Pcm, actual[i].Pcm);
        }
    }

    [Fact]
    public void Streamer_fans_out_identical_frames()
    {
        var sim = new SimScheduler();
        var net = new SimNetwork(sim, 3, new SimLinkProfile(BaseDelayMs: 1, JitterMeanMs: 0, TailProbability: 0, LossProbability: 0));
        var a = Listen(net, ClientA);
        var b = Listen(net, ClientB);
        var local = new RecordingSink();
        var streamer = new Streamer(net.Create(HostAudio), SessionId) { LocalSink = local };
        var idA = Guid.NewGuid();
        streamer.AddClient(idA, ClientA);
        streamer.AddClient(Guid.NewGuid(), ClientB);

        var sent = new List<Frame>();
        void Emit(uint seq)
        {
            var f = new Frame(seq, 5_000_000 + seq * AudioFormat.FrameTicks, Pcm(seq));
            sent.Add(f);
            streamer.OnFrame(f.Seq, f.PlayAt, f.Pcm);
        }

        for (uint s = 0; s < 10; s++) Emit(s);
        sim.RunUntil(10_000_000);

        AssertSame(sent, local.Frames);
        AssertSame(sent, a);
        AssertSame(sent, b);

        streamer.RemoveClient(idA);
        for (uint s = 10; s < 20; s++) Emit(s);
        sim.RunUntil(20_000_000);

        Assert.Equal(10, a.Count);
        AssertSame(sent, b);
        AssertSame(sent, local.Frames);
    }

    [Fact]
    public void Adding_a_known_device_replaces_its_endpoint()
    {
        var ch = new CountingChannel();
        var streamer = new Streamer(ch, SessionId);
        var id = Guid.NewGuid();
        streamer.AddClient(id, ClientA);
        streamer.AddClient(id, ClientB);
        streamer.OnFrame(0, 0, Pcm(0));
        Assert.Equal(1, ch.Sends);

        streamer.RemoveClient(Guid.NewGuid());   // unknown: no-op
        streamer.RemoveClient(id);
        streamer.OnFrame(1, 0, Pcm(1));
        Assert.Equal(1, ch.Sends);
    }

    [Fact]
    public void OnFrame_does_not_allocate()
    {
        var ch = new CountingChannel();
        var streamer = new Streamer(ch, SessionId) { LocalSink = new NullSink() };
        for (int i = 0; i < 8; i++) streamer.AddClient(Guid.NewGuid(), new IPEndPoint(IPAddress.Loopback, 50000 + i));
        var pcm = Pcm(1);
        streamer.OnFrame(0, 0, pcm);   // warm up

        int windows = AllocationAssert.None(() =>
        {
            for (uint s = 1; s <= 1000; s++) streamer.OnFrame(s, s * AudioFormat.FrameTicks, pcm);
        });

        Assert.Equal(8 * (1 + 1000 * windows), ch.Sends);
        Assert.Equal(PacketSerializer.AudioPacketBytes, ch.LastLength);
    }

    [Fact]
    public async Task Concurrent_membership_changes_are_safe()
    {
        var ch = new CountingChannel();
        var streamer = new Streamer(ch, SessionId);
        var pcm = Pcm(0);
        using var stop = new CancellationTokenSource();
        var mutator = Task.Run(() =>
        {
            var ids = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
            while (!stop.IsCancellationRequested)
                foreach (var id in ids)
                {
                    streamer.AddClient(id, ClientA);
                    streamer.RemoveClient(id);
                }
        });
        for (uint s = 0; s < 20_000; s++) streamer.OnFrame(s, 0, pcm);
        stop.Cancel();
        await mutator;
    }

    private sealed class NullSink : IFrameSink
    {
        public void OnFrame(uint seq, long playAt, ReadOnlySpan<short> pcm) { }
    }
}
