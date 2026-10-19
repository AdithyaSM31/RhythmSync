using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RhythmSync.Core;
using RhythmSync.Core.Audio;
using RhythmSync.Core.Audio.Eq;
using RhythmSync.Core.Client;
using RhythmSync.Core.Clock;
using RhythmSync.Core.Host;
using RhythmSync.Core.Net;
using RhythmSync.Core.Protocol;
using RhythmSync.Tests.Sim;

namespace RhythmSync.Tests.Client;

/// <summary>Real loopback TCP and UDP against a real <see cref="HostSession"/>; every wait is bounded.</summary>
public class ClientSessionTests
{
    private const string Code = "1234";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);
    private static readonly UdpDatagramChannelFactory Channels = new();

    // ---- fakes and rigs --------------------------------------------------------------------------------------------

    /// <summary>A no-op output device that records how it is driven.</summary>
    private sealed class NoopOutput : IAudioOutput
    {
        private readonly bool _failStart;

        public NoopOutput(int sampleRate, bool failStart = false)
        {
            SampleRate = sampleRate;
            _failStart = failStart;
        }

        public int SampleRate { get; }
        public IRenderCallback? Callback { get; private set; }
        public bool Started { get; private set; }
        public bool Stopped { get; private set; }
        public bool Disposed { get; private set; }

        public event Action? DeviceInvalidated;

        public void Start(IRenderCallback cb)
        {
            if (_failStart) throw new InvalidOperationException("device busy");
            Callback = cb;
            Started = true;
        }

        public void Stop() => Stopped = true;

        public void Dispose() => Disposed = true;

        public void RaiseInvalidated() => DeviceInvalidated?.Invoke();
    }

    private sealed class FakeOutputFactory : IAudioOutputFactory
    {
        private readonly List<NoopOutput> _created = new();

        private int _failCreates, _failStarts, _attempts;

        /// <summary>Rate of the next output created.</summary>
        public int SampleRate { get; set; } = 48000;

        /// <summary>How many of the next Create calls throw.</summary>
        public int FailCreates { set => Volatile.Write(ref _failCreates, value); }

        /// <summary>How many of the next created outputs throw from Start.</summary>
        public int FailStarts { set => Volatile.Write(ref _failStarts, value); }

        /// <summary>Create calls, failed ones included.</summary>
        public int Attempts => Volatile.Read(ref _attempts);

        public int CreateCalls
        {
            get { lock (_created) return _created.Count; }
        }

        public NoopOutput Last
        {
            get { lock (_created) return _created[^1]; }
        }

        public IAudioOutput Create()
        {
            Interlocked.Increment(ref _attempts);
            if (Volatile.Read(ref _failCreates) > 0)
            {
                Interlocked.Decrement(ref _failCreates);
                throw new InvalidOperationException("no device");
            }
            bool failStart = Volatile.Read(ref _failStarts) > 0;
            if (failStart) Interlocked.Decrement(ref _failStarts);
            var o = new NoopOutput(SampleRate, failStart);
            lock (_created) _created.Add(o);
            return o;
        }
    }

    private sealed class HostRig : IAsyncDisposable
    {
        private HostRig(HostSession host, HostPipeline pipeline)
        {
            Host = host;
            Pipeline = pipeline;
        }

        public HostSession Host { get; }
        public HostPipeline Pipeline { get; }
        public IPEndPoint Control => new(IPAddress.Loopback, Host.ControlPort);

        public static async Task<HostRig> StartAsync(int controlPort = 0, string code = Code, int latencyMs = 200, uint sessionId = 0x5E55_0001)
        {
            var clock = new StopwatchClock();
            var audio = Channels.Create(new IPEndPoint(IPAddress.Loopback, 0));
            var clockChannel = Channels.Create(new IPEndPoint(IPAddress.Loopback, 0));
            var pipeline = new HostPipeline(sessionId, audio, clockChannel, clock, AudioFormat.SampleRate, latencyMs);
            var options = new HostSessionOptions("Host", controlPort, audio.LocalEndPoint.Port, clockChannel.LocalEndPoint.Port,
                                                 latencyMs, PairingCode: code);
            var host = new HostSession(options, clock, pipeline);
            await host.StartAsync(CancellationToken.None);
            return new HostRig(host, pipeline);
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            Pipeline.Dispose();
        }
    }

    /// <summary>Stands in for the host's capture device: pushes real-time silence into the host pipeline when pumped.</summary>
    private sealed class LiveCapture(HostPipeline pipeline)
    {
        private const int Chunk = 480;
        private readonly StopwatchClock _clock = new();
        private readonly float[] _silence = new float[Chunk * AudioFormat.Channels];
        private readonly long _t0 = new StopwatchClock().NowTicks;
        private long _position;

        public void Pump()
        {
            long due = (_clock.NowTicks - _t0) * AudioFormat.SampleRate / AudioFormat.TicksPerSecond;
            for (; _position + Chunk <= due; _position += Chunk)
                pipeline.OnCaptured(_silence, _position, _t0 + _position * AudioFormat.TicksPerSecond / AudioFormat.SampleRate);
        }
    }

    /// <summary>Stands in for a running output device: renders the scheduler in real time, keeping 10 ms queued, when pumped.</summary>
    private sealed class LiveRender
    {
        private const int Queued = 480;
        private readonly StopwatchClock _clock = new();
        private readonly float[] _buffer = new float[48000 * AudioFormat.Channels];
        private PlaybackScheduler? _scheduler;
        private long _t0, _written;

        /// <summary>The last rendered buffer was audio (not silence).</summary>
        public bool Playing { get; private set; }

        public void Pump(PlaybackScheduler scheduler)
        {
            long now = _clock.NowTicks;
            if (!ReferenceEquals(scheduler, _scheduler))
            {
                _scheduler = scheduler;
                _t0 = now;
                _written = 0;
            }
            long device = (now - _t0) * AudioFormat.SampleRate / AudioFormat.TicksPerSecond;
            int frames = (int)Math.Min(device + Queued - _written, 48000);
            if (frames <= 0) return;
            scheduler.Render(_buffer.AsSpan(0, frames * AudioFormat.Channels), new RenderTiming(device, now, _written));
            _written += frames;
            Playing = !double.IsNaN(scheduler.LastRenderStreamPosition);
        }
    }

    private static ClientSession NewClient(FakeOutputFactory outputs, ISessionClock? clock = null, Guid? id = null)
        => new(new ClientSessionOptions(id ?? Guid.NewGuid(), "Laptop", AudioPort: 0), clock ?? new StopwatchClock(), Channels, outputs);

    /// <summary>Ticks the client (and hosts) every 10 ms until <paramref name="condition"/> holds; fails after <paramref name="limit"/>.</summary>
    private static async Task TickUntil(Func<bool> condition, ClientSession client, TimeSpan? limit = null, Func<HostRig?>? host = null)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > (limit ?? Limit)) throw new TimeoutException($"Condition not met within {(limit ?? Limit).TotalSeconds} s.");
            client.Tick();
            host?.Invoke()?.Host.Tick();
            await Task.Delay(10);
        }
    }

    private static async Task<T> Within<T>(Task<T> task)
    {
        var finished = await Task.WhenAny(task, Task.Delay(Limit));
        if (finished != task) throw new TimeoutException("Operation did not complete in time.");
        return await task;
    }

    private static PlaybackScheduler SchedulerOf(NoopOutput output) => Assert.IsType<PlaybackScheduler>(output.Callback);

    // ---- tests ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Join_reaches_InSync()
    {
        await using var rig = await HostRig.StartAsync();
        var outputs = new FakeOutputFactory();
        await using var client = NewClient(outputs);

        var result = await Within(client.JoinAsync(rig.Control, Code, CancellationToken.None));
        Assert.Equal(new JoinResult(JoinOutcome.Joined), result);

        await TickUntil(() => client.State == SyncState.InSync, client, TimeSpan.FromSeconds(5), () => rig);
        await TickUntil(() => rig.Host.Clients is [{ Ready: true }], client, TimeSpan.FromSeconds(5), () => rig);
        Assert.True(outputs.Last.Started);
        Assert.NotNull(client.Stats);
    }

    [Fact]
    public async Task An_output_that_fails_to_start_is_not_reported_in_sync()
    {
        await using var rig = await HostRig.StartAsync();
        var outputs = new FakeOutputFactory { FailStarts = 1 };
        await using var client = NewClient(outputs);
        await Within(client.JoinAsync(rig.Control, Code, CancellationToken.None));

        await TickUntil(() => client.OutputFailures == 1, client, TimeSpan.FromSeconds(5), () => rig);
        client.Tick();
        Assert.Equal(SyncState.Syncing, client.State);   // the clock has converged, but nothing is playing

        await TickUntil(() => client.State == SyncState.InSync, client, TimeSpan.FromSeconds(5), () => rig);
        Assert.True(outputs.Last.Started);   // retried about a second later
    }

    [Fact]
    public async Task Audio_resumes_after_an_output_outage_and_the_state_is_in_sync_only_while_an_output_runs()
    {
        await using var rig = await HostRig.StartAsync();
        var outputs = new FakeOutputFactory();
        await using var client = NewClient(outputs);
        var capture = new LiveCapture(rig.Pipeline);
        var render = new LiveRender();
        await Within(client.JoinAsync(rig.Control, Code, CancellationToken.None));
        await TickUntil(() => client.State == SyncState.InSync && rig.Host.Clients is [{ Ready: true }], client, TimeSpan.FromSeconds(5), () => rig);
        await TickUntil(() =>
        {
            capture.Pump();
            render.Pump(SchedulerOf(outputs.Last));
            return render.Playing;
        }, client, TimeSpan.FromSeconds(5), () => rig);

        // The device goes away and the next two attempts to open one fail: an outage of about 2 s, longer than the
        // jitter buffer (1.28 s), while the host keeps streaming.
        var first = outputs.Last;
        outputs.FailCreates = 2;
        first.RaiseInvalidated();
        client.Tick();
        Assert.True(first.Stopped);
        Assert.Equal(SyncState.Syncing, client.State);

        bool inSyncWithoutOutput = false;
        Stopwatch? sinceOutput = null;
        await TickUntil(() =>
        {
            capture.Pump();
            var current = outputs.Last;
            bool running = !ReferenceEquals(current, first) && current.Started && !current.Stopped;
            if (client.State == SyncState.InSync && !running) inSyncWithoutOutput = true;
            if (!running) return false;
            sinceOutput ??= Stopwatch.StartNew();
            render.Pump(SchedulerOf(current));
            return render.Playing;
        }, client, TimeSpan.FromSeconds(8), () => rig);

        Assert.False(inSyncWithoutOutput);
        Assert.Equal(4, outputs.Attempts);   // the first output, two failures, the replacement
        Assert.True(sinceOutput!.Elapsed < TimeSpan.FromSeconds(1), $"audio resumed {sinceOutput.Elapsed.TotalSeconds:F2} s after the output started");
        await TickUntil(() => client.State == SyncState.InSync, client, TimeSpan.FromSeconds(2), () => rig);
    }

    /// <summary>Real UDP channels, except that binding <paramref name="takenPort"/> fails as if another program held it.</summary>
    private sealed class PortTakenChannels(int takenPort) : IDatagramChannelFactory
    {
        public readonly ConcurrentQueue<IPEndPoint> Bound = new();

        public IDatagramChannel Create(IPEndPoint bind, bool broadcast = false)
        {
            if (bind.Port == takenPort) throw new SocketException((int)SocketError.AddressAlreadyInUse);
            var channel = Channels.Create(bind, broadcast);
            Bound.Enqueue(channel.LocalEndPoint);
            return channel;
        }
    }

    [Fact]
    public async Task A_taken_audio_port_falls_back_to_an_ephemeral_one_and_the_hello_carries_it()
    {
        await using var rig = await HostRig.StartAsync();
        const int taken = 47802;   // e.g. a second RhythmSync on this PC
        var channels = new PortTakenChannels(taken);
        await using var client = new ClientSession(new ClientSessionOptions(Guid.NewGuid(), "Laptop", AudioPort: taken),
                                                   new StopwatchClock(), channels, new FakeOutputFactory());

        var result = await Within(client.JoinAsync(rig.Control, Code, CancellationToken.None));

        Assert.Equal(JoinOutcome.Joined, result.Outcome);
        Assert.True(channels.Bound.TryPeek(out var audio));   // the audio channel is bound first
        Assert.NotEqual(taken, audio!.Port);
        Assert.Equal(audio.Port, Assert.Single(rig.Host.Clients).AudioEndPoint.Port);
    }

    /// <summary>Real UDP channels, except that the next Create throws once <see cref="FailNext"/> is set.</summary>
    private sealed class FailingChannels : IDatagramChannelFactory
    {
        private int _failNext, _failures;

        public void FailNext() => Volatile.Write(ref _failNext, 1);

        public int Failures => Volatile.Read(ref _failures);

        public IDatagramChannel Create(IPEndPoint bind, bool broadcast = false)
        {
            if (Interlocked.Exchange(ref _failNext, 0) != 0)
            {
                Interlocked.Increment(ref _failures);
                throw new SocketException((int)SocketError.NoBufferSpaceAvailable);
            }
            return Channels.Create(bind, broadcast);
        }
    }

    [Fact]
    public async Task A_join_whose_session_setup_fails_ends_disconnected_and_releases_the_connection()
    {
        await using var rig = await HostRig.StartAsync();
        var channels = new FailingChannels();
        await using var client = new ClientSession(new ClientSessionOptions(Guid.NewGuid(), "Laptop", AudioPort: 0),
                                                   new StopwatchClock(), channels, new FakeOutputFactory());
        // Bind the audio channel with a first join, then make the clock channel of the next join fail.
        await Within(client.JoinAsync(rig.Control, Code, CancellationToken.None));
        channels.FailNext();

        await Assert.ThrowsAnyAsync<SocketException>(() => Within(client.JoinAsync(rig.Control, Code, CancellationToken.None)));

        Assert.Equal(SyncState.Disconnected, client.State);
        await TickUntil(() => rig.Host.Clients.Count == 0, client, TimeSpan.FromSeconds(5), () => rig);   // TCP closed, not leaked

        Assert.Equal(JoinOutcome.Joined, (await Within(client.JoinAsync(rig.Control, Code, CancellationToken.None))).Outcome);
        Assert.Equal(SyncState.Syncing, client.State);
    }

    [Fact]
    public async Task A_reconnect_whose_session_setup_fails_counts_as_a_failed_attempt_and_is_retried()
    {
        var rig = await HostRig.StartAsync();
        int port = rig.Host.ControlPort;
        var clock = new ManualClock { NowTicks = 1_000_000_000_000 };
        var channels = new FailingChannels();
        await using var client = new ClientSession(new ClientSessionOptions(Guid.NewGuid(), "Laptop", AudioPort: 0),
                                                   clock, channels, new FakeOutputFactory());
        HostRig? current = rig;
        try
        {
            await Within(client.JoinAsync(rig.Control, Code, CancellationToken.None));
            await rig.DisposeAsync();
            current = null;
            current = await HostRig.StartAsync(controlPort: port);

            channels.FailNext();
            clock.Advance(TimeSpan.FromSeconds(3.5));   // silence noticed: the first attempt starts on this tick
            client.Tick();
            await TickUntil(() => channels.Failures == 1 && client.UnexpectedFaults == 1, client, Limit);
            client.Tick();
            Assert.Equal(SyncState.Connecting, client.State);

            clock.Advance(TimeSpan.FromSeconds(2));     // the next attempt
            await TickUntil(() => client.State == SyncState.Syncing, client, Limit);
            Assert.Single(current.Host.Clients);
        }
        finally
        {
            if (current is not null) await current.DisposeAsync();
        }
    }

    [Fact]
    public async Task Audio_is_accepted_only_from_the_hosts_address()
    {
        const uint sessionId = 0x5E55_0001;
        await using var rig = await HostRig.StartAsync(sessionId: sessionId);
        var outputs = new FakeOutputFactory();
        await using var client = NewClient(outputs);
        await Within(client.JoinAsync(rig.Control, Code, CancellationToken.None));
        await TickUntil(() => client.State == SyncState.InSync, client, TimeSpan.FromSeconds(5), () => rig);
        var clientAudio = new IPEndPoint(IPAddress.Loopback, rig.Host.Clients[0].AudioEndPoint.Port);
        var scheduler = SchedulerOf(outputs.Last);
        var clock = new StopwatchClock();
        var buffer = new float[480 * AudioFormat.Channels];

        // One second of frames whose middle is due now, as a host 200 ms into its stream would send them.
        void SendStream(UdpClient from)
        {
            long t0 = clock.NowTicks - 2_000_000;
            var packet = new byte[PacketSerializer.AudioPacketBytes];
            var pcm = new short[AudioFormat.FrameFloats];
            for (uint seq = 0; seq < 200; seq++)
            {
                PacketSerializer.WriteAudio(packet, sessionId, seq, t0 + seq * AudioFormat.FrameTicks, pcm);
                from.Send(packet, packet.Length, clientAudio);
            }
        }

        bool RenderPlays()
        {
            long now = clock.NowTicks;
            scheduler.Render(buffer, new RenderTiming(0, now, 0));
            return scheduler.State == PlaybackState.Playing;
        }

        using (var stranger = new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0)))
            SendStream(stranger);   // another PC on the LAN (or a stale host) sending to our port
        await Task.Delay(300);
        Assert.False(RenderPlays());

        using var host = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        SendStream(host);
        await TickUntil(RenderPlays, client, TimeSpan.FromSeconds(2), () => rig);
    }

    [Fact]
    public async Task Heartbeats_carry_stats_to_the_host()
    {
        await using var rig = await HostRig.StartAsync();
        await using var client = NewClient(new FakeOutputFactory());
        await Within(client.JoinAsync(rig.Control, Code, CancellationToken.None));

        client.Tick();   // first heartbeat goes out at once, before the pipeline exists
        await TickUntil(() => rig.Host.Clients is [{ LastStats: not null }], client, TimeSpan.FromSeconds(2), () => rig);
        var early = rig.Host.Clients[0].LastStats!;
        Assert.Equal(SyncState.Syncing, early.State);
        Assert.True(double.IsNaN(early.LossPct) && double.IsNaN(early.LatePct) && double.IsNaN(early.SyncErrorMs));

        await TickUntil(() => rig.Host.Clients is [{ LastStats.State: SyncState.InSync }], client, TimeSpan.FromSeconds(5), () => rig);
        var stats = rig.Host.Clients[0].LastStats!;
        Assert.Equal(0, stats.LossPct);
        Assert.Equal(0, stats.LatePct);
        Assert.True(double.IsNaN(stats.SyncErrorMs));   // the host sends no audio: the scheduler is not playing
        Assert.True(double.IsFinite(stats.DriftPpm) && double.IsFinite(stats.FitResidualMs));
    }

    [Fact]
    public async Task Output_is_created_only_after_clock_convergence()
    {
        await using var rig = await HostRig.StartAsync();
        var outputs = new FakeOutputFactory();
        await using var client = NewClient(outputs);

        await Within(client.JoinAsync(rig.Control, Code, CancellationToken.None));
        Assert.Equal(SyncState.Syncing, client.State);
        client.Tick();
        Assert.Equal(0, outputs.CreateCalls);
        Assert.Null(client.Stats);
        Assert.DoesNotContain(rig.Host.Clients, c => c.Ready);

        await TickUntil(() => client.State == SyncState.InSync, client, TimeSpan.FromSeconds(5), () => rig);
        Assert.Equal(1, outputs.CreateCalls);
    }

    [Fact]
    public async Task Rejection_is_reported()
    {
        await using var rig = await HostRig.StartAsync();
        await using var client = NewClient(new FakeOutputFactory());

        var result = await Within(client.JoinAsync(rig.Control, "9999", CancellationToken.None));

        Assert.Equal(new JoinResult(JoinOutcome.Rejected, RejectReason.BadCode), result);
        Assert.Equal(SyncState.Disconnected, client.State);
    }

    [Fact]
    public async Task Unreachable_host_is_reported()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int closedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var client = NewClient(new FakeOutputFactory());

        var result = await Within(client.JoinAsync(new IPEndPoint(IPAddress.Loopback, closedPort), Code, CancellationToken.None));

        Assert.Equal(JoinOutcome.Unreachable, result.Outcome);
        Assert.Equal(SyncState.Disconnected, client.State);
    }

    [Fact]
    public async Task Host_settings_apply_locally()
    {
        await using var rig = await HostRig.StartAsync();
        var outputs = new FakeOutputFactory();
        var id = Guid.NewGuid();
        await using var client = NewClient(outputs, id: id);
        await Within(client.JoinAsync(rig.Control, Code, CancellationToken.None));
        await TickUntil(() => client.State == SyncState.InSync, client, TimeSpan.FromSeconds(5), () => rig);

        await rig.Host.SetDeviceSettingsAsync(id, 0.5f, 10, EqGains.Flat);

        await TickUntil(() => client.Volume == 0.5f && client.OffsetMs == 10, client, TimeSpan.FromSeconds(1), () => rig);
        Assert.Equal(10, SchedulerOf(outputs.Last).EffectiveOffsetMs, 6);
    }

    [Fact]
    public async Task Host_latency_change_reclamps_the_requested_offset()
    {
        await using var rig = await HostRig.StartAsync(latencyMs: 100);
        var outputs = new FakeOutputFactory();
        var id = Guid.NewGuid();
        await using var client = NewClient(outputs, id: id);
        await Within(client.JoinAsync(rig.Control, Code, CancellationToken.None));
        await TickUntil(() => client.State == SyncState.InSync, client, TimeSpan.FromSeconds(5), () => rig);

        await rig.Host.SetDeviceSettingsAsync(id, 1f, -80, EqGains.Flat);
        await TickUntil(() => client.OffsetMs == -80, client, TimeSpan.FromSeconds(1), () => rig);
        Assert.Equal(-50, SchedulerOf(outputs.Last).EffectiveOffsetMs, 6);   // ≥ −(100 − 50)

        await rig.Host.SetLatencyAsync(200);

        await TickUntil(() => Math.Abs(SchedulerOf(outputs.Last).EffectiveOffsetMs - -80) < 1e-6, client, TimeSpan.FromSeconds(1), () => rig);
    }

    [Fact]
    public async Task Output_device_change_rebuilds_output()
    {
        await using var rig = await HostRig.StartAsync(latencyMs: 100);
        var outputs = new FakeOutputFactory();
        var id = Guid.NewGuid();
        await using var client = NewClient(outputs, id: id);
        await Within(client.JoinAsync(rig.Control, Code, CancellationToken.None));
        await TickUntil(() => client.State == SyncState.InSync, client, TimeSpan.FromSeconds(5), () => rig);
        await rig.Host.SetDeviceSettingsAsync(id, 1f, -80, EqGains.Flat);   // effective −50 at 100 ms latency
        await TickUntil(() => client.OffsetMs == -80, client, TimeSpan.FromSeconds(1), () => rig);
        var first = outputs.Last;
        Assert.Equal(48000, SchedulerOf(first).DeviceSampleRate);

        outputs.SampleRate = 44100;
        first.RaiseInvalidated();
        Assert.Equal(1, outputs.CreateCalls);   // the rebuild is not done on the raising (audio) thread
        client.Tick();

        Assert.Equal(2, outputs.CreateCalls);
        Assert.True(first.Stopped);
        Assert.True(first.Disposed);
        var second = outputs.Last;
        Assert.True(second.Started);
        Assert.Equal(44100, SchedulerOf(second).DeviceSampleRate);
        Assert.NotSame(first.Callback, second.Callback);
        Assert.Equal(-50, SchedulerOf(second).EffectiveOffsetMs, 6);

        // The requested offset (not the clamped one) survives the rebuild.
        await rig.Host.SetLatencyAsync(200);
        await TickUntil(() => Math.Abs(SchedulerOf(second).EffectiveOffsetMs - -80) < 1e-6, client, TimeSpan.FromSeconds(1), () => rig);

        // A late event from the old device does nothing.
        first.RaiseInvalidated();
        client.Tick();
        Assert.Equal(2, outputs.CreateCalls);
    }

    [Fact]
    public async Task Reconnects_after_host_restart()
    {
        var rig = await HostRig.StartAsync(sessionId: 0x1111);
        int port = rig.Host.ControlPort;
        var outputs = new FakeOutputFactory();
        await using var client = NewClient(outputs);
        var states = new ConcurrentQueue<SyncState>();
        client.StateChanged += () => states.Enqueue(client.State);
        HostRig? current = rig;
        try
        {
            await Within(client.JoinAsync(rig.Control, Code, CancellationToken.None));
            await TickUntil(() => client.State == SyncState.InSync, client, TimeSpan.FromSeconds(5), () => current);
            var firstOutput = outputs.Last;

            await rig.DisposeAsync();
            current = null;
            current = await HostRig.StartAsync(controlPort: port, sessionId: 0x2222);
            states.Clear();

            await TickUntil(() => client.State == SyncState.InSync && current!.Host.Clients is [{ Ready: true }], client, Limit, () => current);

            Assert.Contains(SyncState.Connecting, states);
            Assert.True(firstOutput.Disposed);
            Assert.Equal(2, outputs.CreateCalls);   // the new session built a fresh output and pipeline
            Assert.NotSame(firstOutput.Callback, outputs.Last.Callback);
        }
        finally
        {
            if (current is not null) await current.DisposeAsync();
        }
    }

    [Fact]
    public async Task Reject_during_reconnect_disconnects_at_once()
    {
        var rig = await HostRig.StartAsync();
        int port = rig.Host.ControlPort;
        var clock = new ManualClock { NowTicks = 1_000_000_000_000 };
        await using var client = NewClient(new FakeOutputFactory(), clock);
        var states = new ConcurrentQueue<SyncState>();
        client.StateChanged += () => states.Enqueue(client.State);
        HostRig? current = rig;
        try
        {
            await Within(client.JoinAsync(rig.Control, Code, CancellationToken.None));
            await rig.DisposeAsync();
            current = null;
            current = await HostRig.StartAsync(controlPort: port, code: "5678");   // the host came back with a new code

            clock.Advance(TimeSpan.FromSeconds(3.5));   // silence noticed: reconnect starts on this tick
            client.Tick();

            await TickUntil(() => client.State == SyncState.Disconnected, client, Limit);
            Assert.Contains(SyncState.Connecting, states);
            Assert.True(clock.NowTicks - 1_000_000_000_000 < 60 * AudioFormat.TicksPerSecond);
        }
        finally
        {
            if (current is not null) await current.DisposeAsync();
        }
    }

    [Fact]
    public async Task Gives_up_after_60s()
    {
        var rig = await HostRig.StartAsync();
        var clock = new ManualClock { NowTicks = 1_000_000_000_000 };
        await using var client = NewClient(new FakeOutputFactory(), clock);
        var states = new ConcurrentQueue<SyncState>();
        client.StateChanged += () => states.Enqueue(client.State);

        await Within(client.JoinAsync(rig.Control, Code, CancellationToken.None));
        await rig.DisposeAsync();

        // 3 s of silence before the loss is noticed, then 60 s of failed retries.
        for (int i = 0; i < 3 * 10 + 1; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            client.Tick();
        }
        Assert.Equal(SyncState.Connecting, client.State);
        for (int i = 0; i < 59 * 10; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            client.Tick();
            if (i % 20 == 0) await Task.Delay(1);
        }
        Assert.Equal(SyncState.Connecting, client.State);

        for (int i = 0; i < 2 * 10; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            client.Tick();
        }
        Assert.Equal(SyncState.Disconnected, client.State);
        Assert.Contains(SyncState.Connecting, states);
    }

    [Fact]
    public async Task Invalid_host_settings_are_ignored_and_values_clamped()
    {
        using var listener = new FakeHost();
        await using var client = NewClient(new FakeOutputFactory());

        var join = client.JoinAsync(listener.Control, Code, CancellationToken.None);
        await using var stream = await Within(listener.AcceptAsync());
        Assert.Equal(new JoinResult(JoinOutcome.Joined), await Within(join));

        await ControlCodec.WriteAsync(stream, new SetDeviceSettings(float.NaN, 5), CancellationToken.None);
        await ControlCodec.WriteAsync(stream, new SetDeviceSettings(0.3f, double.PositiveInfinity), CancellationToken.None);
        await ControlCodec.WriteAsync(stream, new SetDeviceSettings(2f, -500), CancellationToken.None);

        await TickUntil(() => client.Volume == 1f && client.OffsetMs == -100 && client.IgnoredSettingsMessages == 2, client,
                        TimeSpan.FromSeconds(2));
        Assert.Equal(2, client.IgnoredSettingsMessages);
    }

    private static EqGains Laptop => EqPresets.All.First(p => p.Name == "Laptop speakers").Gains;

    [Fact]
    public async Task A_valid_eq_reaches_the_pipeline()
    {
        await using var rig = await HostRig.StartAsync();
        var id = Guid.NewGuid();
        await using var client = NewClient(new FakeOutputFactory(), id: id);
        await Within(client.JoinAsync(rig.Control, Code, CancellationToken.None));
        await TickUntil(() => client.State == SyncState.InSync, client, TimeSpan.FromSeconds(5), () => rig);
        Assert.Equal(EqGains.Flat, client.Eq);

        await rig.Host.SetDeviceSettingsAsync(id, 1f, 0, Laptop);

        await TickUntil(() => client.Eq == Laptop, client, TimeSpan.FromSeconds(1), () => rig);
        Assert.Equal(Laptop, client.Pipeline!.Eq.Gains);
    }

    [Fact]
    public async Task A_malformed_eq_is_ignored_and_counted_and_the_connection_stays()
    {
        using var listener = new FakeHost();
        await using var client = NewClient(new FakeOutputFactory());
        var join = client.JoinAsync(listener.Control, Code, CancellationToken.None);
        await using var stream = await Within(listener.AcceptAsync());
        Assert.Equal(new JoinResult(JoinOutcome.Joined), await Within(join));

        await ControlCodec.WriteAsync(stream, new SetDeviceSettings(0.5f, 10, Laptop.ToArray()), CancellationToken.None);
        await TickUntil(() => client.Eq == Laptop && client.Volume == 0.5f && client.OffsetMs == 10, client, TimeSpan.FromSeconds(2));

        var nan = Laptop.ToArray();
        nan[0] = float.NaN;
        await ControlCodec.WriteAsync(stream, new SetDeviceSettings(0.9f, 50, new float[9]), CancellationToken.None);
        await ControlCodec.WriteAsync(stream, new SetDeviceSettings(0.9f, 50, nan), CancellationToken.None);
        await TickUntil(() => client.IgnoredSettingsMessages == 2, client, TimeSpan.FromSeconds(2));

        Assert.Equal(Laptop, client.Eq);
        Assert.Equal(0.5f, client.Volume);
        Assert.Equal(10, client.OffsetMs);

        await ControlCodec.WriteAsync(stream, new SetDeviceSettings(0.25f, -20, null), CancellationToken.None);   // null means flat
        await TickUntil(() => client.Eq == EqGains.Flat && client.Volume == 0.25f && client.OffsetMs == -20, client, TimeSpan.FromSeconds(2));
        Assert.Equal(2, client.IgnoredSettingsMessages);
    }

    [Fact]
    public async Task An_eq_received_before_playback_applies_when_the_pipeline_is_built()
    {
        await using var rig = await HostRig.StartAsync();
        var id = Guid.NewGuid();
        await using var client = NewClient(new FakeOutputFactory(), id: id);
        await Within(client.JoinAsync(rig.Control, Code, CancellationToken.None));
        Assert.Null(client.Pipeline);   // nothing ticks the client, so no output or pipeline exists yet

        await rig.Host.SetDeviceSettingsAsync(id, 1f, 0, Laptop);
        var sw = Stopwatch.StartNew();
        while (client.Eq != Laptop)
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(2)) throw new TimeoutException("The EQ did not arrive.");
            await Task.Delay(10);
        }
        Assert.Null(client.Pipeline);

        await TickUntil(() => client.Pipeline is not null, client, TimeSpan.FromSeconds(5), () => rig);

        Assert.Equal(Laptop, client.Pipeline!.Eq.Gains);
    }

    [Fact]
    public async Task The_eq_survives_a_reconnect()
    {
        var rig = await HostRig.StartAsync(sessionId: 0x1111);
        int port = rig.Host.ControlPort;
        var id = Guid.NewGuid();
        await using var client = NewClient(new FakeOutputFactory(), id: id);
        HostRig? current = rig;
        try
        {
            await Within(client.JoinAsync(rig.Control, Code, CancellationToken.None));
            await TickUntil(() => client.State == SyncState.InSync, client, TimeSpan.FromSeconds(5), () => current);
            await rig.Host.SetDeviceSettingsAsync(id, 1f, 0, Laptop);
            await TickUntil(() => client.Eq == Laptop, client, TimeSpan.FromSeconds(2), () => current);
            var firstPipeline = client.Pipeline;

            await rig.DisposeAsync();
            current = null;
            current = await HostRig.StartAsync(controlPort: port, sessionId: 0x2222);   // a fresh host that never sends the EQ again

            await TickUntil(() => client.State == SyncState.InSync && current!.Host.Clients is [{ Ready: true }] && client.Pipeline is not null
                                  && !ReferenceEquals(client.Pipeline, firstPipeline), client, Limit, () => current);

            Assert.Equal(Laptop, client.Eq);
            Assert.Equal(Laptop, client.Pipeline!.Eq.Gains);
        }
        finally
        {
            if (current is not null) await current.DisposeAsync();
        }
    }

    [Fact]
    public async Task Leave_sends_bye_and_host_drops_the_client()
    {
        await using var rig = await HostRig.StartAsync();
        await using var client = NewClient(new FakeOutputFactory());
        await Within(client.JoinAsync(rig.Control, Code, CancellationToken.None));
        Assert.Single(rig.Host.Clients);

        await client.LeaveAsync();

        Assert.Equal(SyncState.Disconnected, client.State);
        await TickUntil(() => rig.Host.Clients.Count == 0, client, TimeSpan.FromSeconds(2));
    }

    /// <summary>A bare control-plane host: accepts one client, answers its Hello with a Welcome, then is driven by the test.</summary>
    private sealed class FakeHost : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<IDisposable> _owned = new();

        public FakeHost() => _listener.Start();

        public IPEndPoint Control => new(IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port);

        public async Task<NetworkStream> AcceptAsync()
        {
            var tcp = await _listener.AcceptTcpClientAsync();
            _owned.Add(tcp);
            var stream = tcp.GetStream();
            Assert.IsType<Hello>(await ControlCodec.ReadAsync(stream, CancellationToken.None));
            await ControlCodec.WriteAsync(stream, new Welcome(0x42, AudioFormat.SampleRate, AudioFormat.Channels, AudioFormat.FrameSamples,
                                                              200, 47802, 9), CancellationToken.None);
            return stream;
        }

        public void Dispose()
        {
            _listener.Stop();
            foreach (var o in _owned) o.Dispose();
        }
    }
}
