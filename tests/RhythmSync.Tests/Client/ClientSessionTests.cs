using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RhythmSync.Core;
using RhythmSync.Core.Audio;
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
        public NoopOutput(int sampleRate) => SampleRate = sampleRate;

        public int SampleRate { get; }
        public IRenderCallback? Callback { get; private set; }
        public bool Started { get; private set; }
        public bool Stopped { get; private set; }
        public bool Disposed { get; private set; }

        public event Action? DeviceInvalidated;

        public void Start(IRenderCallback cb)
        {
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

        /// <summary>Rate of the next output created.</summary>
        public int SampleRate { get; set; } = 48000;

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
            var o = new NoopOutput(SampleRate);
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

        await rig.Host.SetDeviceSettingsAsync(id, 0.5f, 10);

        await TickUntil(() => client.Volume == 0.5f && client.OffsetMs == 10, client, TimeSpan.FromSeconds(1), () => rig);
        Assert.Equal(10, SchedulerOf(outputs.Last).EffectiveOffsetMs, 6);
    }

    [Fact]
    public async Task Host_latency_change_reclamps_the_requested_offset()
    {
        await using var rig = await HostRig.StartAsync(latencyMs: 100);
        var outputs = new FakeOutputFactory();
        await using var client = NewClient(outputs);
        await Within(client.JoinAsync(rig.Control, Code, CancellationToken.None));
        await TickUntil(() => client.State == SyncState.InSync, client, TimeSpan.FromSeconds(5), () => rig);

        client.OffsetMs = -80;
        Assert.Equal(-80, client.OffsetMs);
        Assert.Equal(-50, SchedulerOf(outputs.Last).EffectiveOffsetMs, 6);   // ≥ −(100 − 50)

        await rig.Host.SetLatencyAsync(200);

        await TickUntil(() => Math.Abs(SchedulerOf(outputs.Last).EffectiveOffsetMs - -80) < 1e-6, client, TimeSpan.FromSeconds(1), () => rig);
    }

    [Fact]
    public async Task Output_device_change_rebuilds_output()
    {
        await using var rig = await HostRig.StartAsync(latencyMs: 100);
        var outputs = new FakeOutputFactory();
        await using var client = NewClient(outputs);
        await Within(client.JoinAsync(rig.Control, Code, CancellationToken.None));
        await TickUntil(() => client.State == SyncState.InSync, client, TimeSpan.FromSeconds(5), () => rig);
        client.OffsetMs = -80;   // effective −50 at 100 ms latency
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

    [Fact]
    public async Task Local_settings_are_validated_and_clamped()
    {
        await using var client = NewClient(new FakeOutputFactory());

        Assert.Throws<ArgumentException>(() => client.Volume = float.NaN);
        Assert.Throws<ArgumentException>(() => client.OffsetMs = double.NegativeInfinity);
        client.Volume = -1;
        client.OffsetMs = 250;

        Assert.Equal(0f, client.Volume);
        Assert.Equal(100, client.OffsetMs);
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
