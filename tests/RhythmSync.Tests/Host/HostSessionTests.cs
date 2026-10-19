using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using RhythmSync.Core;
using RhythmSync.Core.Audio.Eq;
using RhythmSync.Core.Host;
using RhythmSync.Core.Protocol;
using RhythmSync.Tests.Sim;

namespace RhythmSync.Tests.Host;

public class HostSessionTests
{
    private const long Second = AudioFormat.TicksPerSecond;
    private const long T0 = 1_000_000_000_000;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly IPEndPoint HostAudio = new(IPAddress.Parse("10.0.0.1"), 47802);
    private static readonly IPEndPoint HostClock = new(IPAddress.Parse("10.0.0.1"), 47803);

    private static async Task<T> Within<T>(Task<T> task)
    {
        var finished = await Task.WhenAny(task, Task.Delay(Timeout));
        if (finished != task) throw new TimeoutException("Operation did not complete within 5 s.");
        return await task;
    }

    private static async Task Within(Task task)
    {
        var finished = await Task.WhenAny(task, Task.Delay(Timeout));
        if (finished != task) throw new TimeoutException("Operation did not complete within 5 s.");
        await task;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met within 5 s.");
            await Task.Delay(5);
        }
    }

    /// <summary>A raw control-plane client: a TcpClient speaking ControlCodec.</summary>
    private sealed class TestClient : IDisposable
    {
        private readonly TcpClient _tcp = new();
        private NetworkStream _stream = null!;

        public static async Task<TestClient> ConnectAsync(int port)
        {
            var c = new TestClient();
            await Within(c._tcp.ConnectAsync(IPAddress.Loopback, port));
            c._stream = c._tcp.GetStream();
            return c;
        }

        public Task SendAsync(ControlMessage m) => Within(ControlCodec.WriteAsync(_stream, m, CancellationToken.None));

        public Task SendRawAsync(byte[] bytes) => Within(_stream.WriteAsync(bytes).AsTask());

        /// <returns>The next message, or null when the host closed the connection.</returns>
        public async Task<ControlMessage?> ReadAsync()
        {
            try { return await Within(ControlCodec.ReadAsync(_stream, CancellationToken.None)); }
            catch (IOException) { return null; }   // reset by the host counts as closed
        }

        /// <summary>Asserts that no message (and no close) arrives within <paramref name="window"/>.</summary>
        public async Task ExpectNothingAsync(TimeSpan window)
        {
            using var cts = new CancellationTokenSource(window);
            try
            {
                var m = await ControlCodec.ReadAsync(_stream, cts.Token);
                Assert.Fail($"Expected silence but received {(m is null ? "EOF" : m.ToString())}.");
            }
            catch (OperationCanceledException) { }
        }

        public async Task<T> ReadAsync<T>() where T : ControlMessage
            => Assert.IsType<T>(await ReadAsync());

        public void Dispose() => _tcp.Dispose();
    }

    private sealed class Rig : IAsyncDisposable
    {
        public readonly ManualClock Clock = new() { NowTicks = T0 };
        public readonly SimScheduler Sim = new(T0);
        public readonly SimNetwork Net;
        public readonly HostPipeline Pipeline;
        public readonly HostSession Host;
        private readonly List<TestClient> _clients = new();

        public Rig(HostSessionOptions? options = null)
        {
            Net = new SimNetwork(Sim, 1, new SimLinkProfile(BaseDelayMs: 1, JitterMeanMs: 0, TailProbability: 0, LossProbability: 0));
            Pipeline = new HostPipeline(0x77, Net.Create(HostAudio), Net.Create(HostClock), Clock, 48000, 200);
            Host = new HostSession(options ?? new HostSessionOptions("Host", ControlPort: 0, PairingCode: "1234"), Clock, Pipeline);
        }

        public async Task<Rig> StartAsync()
        {
            await Host.StartAsync(CancellationToken.None);
            return this;
        }

        public async Task<TestClient> ConnectAsync()
        {
            var c = await TestClient.ConnectAsync(Host.ControlPort);
            lock (_clients) _clients.Add(c);
            return c;
        }

        /// <summary>Connects and sends a Hello; returns the client and the host's first reply.</summary>
        public async Task<(TestClient Client, ControlMessage? Reply)> JoinAsync(Guid id, string code = "1234", int version = HostSession.ProtocolVersion,
                                                                                 int audioPort = 5000, string name = "Laptop")
        {
            var c = await ConnectAsync();
            await c.SendAsync(new Hello(id, name, code, version, audioPort));
            return (c, await c.ReadAsync());
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            foreach (var c in _clients) c.Dispose();
            Pipeline.Dispose();
        }
    }

    [Fact]
    public async Task Join_returns_welcome_and_lists_client()
    {
        await using var rig = await new Rig().StartAsync();
        var id = Guid.NewGuid();

        var (_, reply) = await rig.JoinAsync(id, name: "  Kitchen  ");

        var welcome = Assert.IsType<Welcome>(reply);
        Assert.Equal(new Welcome(rig.Host.SessionId, 48000, 2, 240, 200, 47802, 47803), welcome);
        Assert.Equal(rig.Pipeline.SessionId, rig.Host.SessionId);
        var client = Assert.Single(rig.Host.Clients);
        Assert.Equal(id, client.DeviceId);
        Assert.Equal("Kitchen", client.Name);
        Assert.False(client.Ready);
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 5000), client.AudioEndPoint);
        Assert.Equal(1f, client.Volume);
        Assert.Equal(0.0, client.OffsetMs);
    }

    [Fact]
    public async Task Ready_adds_client_to_streamer()
    {
        await using var rig = await new Rig().StartAsync();
        var received = new List<int>();
        var clientAudio = rig.Net.Create(new IPEndPoint(IPAddress.Loopback, 5000));
        clientAudio.Start((data, _) => received.Add(data.Length));
        var pcm = new short[AudioFormat.FrameFloats];

        var (client, _) = await rig.JoinAsync(Guid.NewGuid(), audioPort: 5000);

        // Joined but not Ready: no audio yet.
        rig.Pipeline.Streamer.OnFrame(0, T0, pcm);
        rig.Sim.RunUntil(T0 + Second);
        Assert.Empty(received);

        await client.SendAsync(new Ready());
        await WaitUntil(() => rig.Host.Clients.Count == 1 && rig.Host.Clients[0].Ready);

        rig.Pipeline.Streamer.OnFrame(1, T0 + Second, pcm);
        rig.Sim.RunUntil(T0 + 2 * Second);
        Assert.Equal(new[] { 982 }, received);
    }

    [Fact]
    public async Task Bad_code_rejected()
    {
        await using var rig = await new Rig().StartAsync();

        var (client, reply) = await rig.JoinAsync(Guid.NewGuid(), code: "9999");

        Assert.Equal(new Reject(RejectReason.BadCode), reply);
        Assert.Null(await client.ReadAsync());   // host closes after rejecting
        Assert.Empty(rig.Host.Clients);
    }

    [Fact]
    public async Task Lockout_after_three_bad_codes_expires_after_30s()
    {
        await using var rig = await new Rig().StartAsync();
        var id = Guid.NewGuid();

        for (int i = 0; i < 3; i++)
            Assert.Equal(new Reject(RejectReason.BadCode), (await rig.JoinAsync(id, code: "0000")).Reply);

        Assert.Equal(new Reject(RejectReason.LockedOut), (await rig.JoinAsync(id, code: "1234")).Reply);

        // Another device is unaffected.
        Assert.IsType<Welcome>((await rig.JoinAsync(Guid.NewGuid(), code: "1234")).Reply);

        rig.Clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(new Reject(RejectReason.LockedOut), (await rig.JoinAsync(id, code: "1234")).Reply);

        rig.Clock.Advance(TimeSpan.FromSeconds(2));   // 31 s after the lockout began
        Assert.IsType<Welcome>((await rig.JoinAsync(id, code: "1234")).Reply);
    }

    [Fact]
    public async Task More_than_ten_bad_codes_from_any_devices_lock_everyone_out_for_30s()
    {
        await using var rig = await new Rig().StartAsync();

        // Rotating device ids defeats the per-device limit, but not the global one.
        for (int i = 0; i < 11; i++)
        {
            Assert.Equal(new Reject(RejectReason.BadCode), (await rig.JoinAsync(Guid.NewGuid(), code: "0000")).Reply);
            rig.Clock.Advance(TimeSpan.FromSeconds(2));   // all 11 within the 30 s window
        }
        var honest = Guid.NewGuid();
        Assert.Equal(new Reject(RejectReason.LockedOut), (await rig.JoinAsync(honest, code: "1234")).Reply);

        rig.Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.IsType<Welcome>((await rig.JoinAsync(honest, code: "1234")).Reply);
    }

    [Fact]
    public async Task The_global_limit_counts_invalid_hellos_and_only_failures_inside_a_sliding_30s_window()
    {
        await using var rig = await new Rig().StartAsync();

        for (int i = 0; i < 6; i++)
            Assert.Equal(new Reject(RejectReason.BadCode), (await rig.JoinAsync(Guid.NewGuid(), code: "0000")).Reply);
        rig.Clock.Advance(TimeSpan.FromSeconds(31));   // those six have left the window

        for (int i = 0; i < 4; i++)
        {
            var bad = await rig.ConnectAsync();
            await bad.SendAsync(new Hello(Guid.NewGuid(), "", "1234", HostSession.ProtocolVersion, 5000));   // invalid: counts as a bad code
            Assert.Null(await ReadUntilClosed(bad));
        }
        for (int i = 0; i < 6; i++)
            Assert.Equal(new Reject(RejectReason.BadCode), (await rig.JoinAsync(Guid.NewGuid(), code: "0000")).Reply);

        // Ten failures in the window: still open. The eleventh closes it.
        Assert.IsType<Welcome>((await rig.JoinAsync(Guid.NewGuid())).Reply);
        Assert.Equal(new Reject(RejectReason.BadCode), (await rig.JoinAsync(Guid.NewGuid(), code: "0000")).Reply);
        Assert.Equal(new Reject(RejectReason.LockedOut), (await rig.JoinAsync(Guid.NewGuid())).Reply);
    }

    [Fact]
    public async Task Parallel_bad_hellos_lock_out_after_exactly_three_guesses()
    {
        await using var rig = await new Rig().StartAsync();
        var id = Guid.NewGuid();

        var replies = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => rig.JoinAsync(id, code: "0000")));

        // Check, compare and record are one atomic step: only three guesses get an answer, the rest are locked out.
        Assert.Equal(3, replies.Count(r => r.Reply is Reject { Reason: RejectReason.BadCode }));
        Assert.Equal(7, replies.Count(r => r.Reply is Reject { Reason: RejectReason.LockedOut }));
        Assert.Equal(new Reject(RejectReason.LockedOut), (await rig.JoinAsync(id, code: "1234")).Reply);
        Assert.Empty(rig.Host.Clients);
    }

    [Fact]
    public async Task Failures_below_the_limit_are_cleared_by_a_successful_join()
    {
        await using var rig = await new Rig().StartAsync();
        var id = Guid.NewGuid();

        await rig.JoinAsync(id, code: "0000");
        await rig.JoinAsync(id, code: "0000");
        Assert.IsType<Welcome>((await rig.JoinAsync(id)).Reply);

        // Two more bad codes must not trip the limit: the counter restarted.
        Assert.Equal(new Reject(RejectReason.BadCode), (await rig.JoinAsync(id, code: "0000")).Reply);
        Assert.Equal(new Reject(RejectReason.BadCode), (await rig.JoinAsync(id, code: "0000")).Reply);
    }

    [Fact]
    public async Task A_protocol_1_hello_is_rejected_with_VersionMismatch()
    {
        await using var rig = await new Rig().StartAsync();

        var (_, reply) = await rig.JoinAsync(Guid.NewGuid(), version: 1);

        Assert.Equal(new Reject(RejectReason.VersionMismatch), reply);
        Assert.Empty(rig.Host.Clients);
    }

    [Fact]
    public async Task Session_full_rejected()
    {
        await using var rig = await new Rig(new HostSessionOptions("Host", ControlPort: 0, MaxClients: 1, PairingCode: "1234")).StartAsync();
        Assert.IsType<Welcome>((await rig.JoinAsync(Guid.NewGuid())).Reply);

        var (_, reply) = await rig.JoinAsync(Guid.NewGuid());

        Assert.Equal(new Reject(RejectReason.SessionFull), reply);
        Assert.Single(rig.Host.Clients);
    }

    [Fact]
    public async Task Silent_client_dropped_after_3s()
    {
        await using var rig = await new Rig().StartAsync();
        var received = new List<int>();
        rig.Net.Create(new IPEndPoint(IPAddress.Loopback, 5000)).Start((data, _) => received.Add(data.Length));
        var (client, _) = await rig.JoinAsync(Guid.NewGuid(), audioPort: 5000);
        await client.SendAsync(new Ready());
        await WaitUntil(() => rig.Host.Clients.Count == 1 && rig.Host.Clients[0].Ready);
        int changes = 0;
        rig.Host.ClientsChanged += () => Interlocked.Increment(ref changes);

        rig.Clock.Advance(TimeSpan.FromSeconds(2.9));
        rig.Host.Tick();
        Assert.Single(rig.Host.Clients);

        rig.Clock.Advance(TimeSpan.FromSeconds(0.2));
        rig.Host.Tick();

        Assert.Empty(rig.Host.Clients);
        Assert.Equal(1, Volatile.Read(ref changes));
        Assert.Null(await ReadUntilClosed(client));   // socket closed
        rig.Pipeline.Streamer.OnFrame(0, T0, new short[AudioFormat.FrameFloats]);
        rig.Sim.RunUntil(T0 + Second);
        Assert.Empty(received);                        // removed from the streamer
    }

    [Fact]
    public async Task Any_message_from_the_client_counts_as_liveness()
    {
        await using var rig = await new Rig().StartAsync();
        var (client, _) = await rig.JoinAsync(Guid.NewGuid());

        rig.Clock.Advance(TimeSpan.FromSeconds(2.5));
        await client.SendAsync(new Heartbeat(null));
        await Task.Delay(100);   // let the host's reader observe it before the clock moves again
        rig.Clock.Advance(TimeSpan.FromSeconds(2.5));
        rig.Host.Tick();

        Assert.Single(rig.Host.Clients);
    }

    [Fact]
    public async Task Tick_sends_a_heartbeat_once_per_second()
    {
        await using var rig = await new Rig().StartAsync();
        var (client, _) = await rig.JoinAsync(Guid.NewGuid());

        rig.Host.Tick();
        Assert.Equal(new Heartbeat(null), await client.ReadAsync());
        rig.Host.Tick();                                  // same instant: nothing more
        await client.ExpectNothingAsync(TimeSpan.FromMilliseconds(300));

        rig.Clock.Advance(TimeSpan.FromSeconds(0.5));
        rig.Host.Tick();                                  // too soon
        await client.ExpectNothingAsync(TimeSpan.FromMilliseconds(300));

        await client.SendAsync(new Heartbeat(null));      // keep the client alive for the next ticks
        await Task.Delay(100);
        rig.Clock.Advance(TimeSpan.FromSeconds(0.6));
        rig.Host.Tick();                                  // 1.1 s after the first
        Assert.Equal(new Heartbeat(null), await client.ReadAsync());
        rig.Host.Tick();
        await client.ExpectNothingAsync(TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public async Task Client_heartbeat_updates_last_stats()
    {
        await using var rig = await new Rig().StartAsync();
        var (client, _) = await rig.JoinAsync(Guid.NewGuid());
        var stats = new HeartbeatStats(1.5, 0.5, 12.0, 0.1, 0.3, Core.Clock.SyncState.InSync);

        await client.SendAsync(new Heartbeat(stats));

        await WaitUntil(() => rig.Host.Clients.Count == 1 && rig.Host.Clients[0].LastStats == stats);
    }

    [Fact]
    public async Task Device_settings_and_latency_are_sent()
    {
        await using var rig = await new Rig().StartAsync();
        var id = Guid.NewGuid();
        var (client, _) = await rig.JoinAsync(id);

        await rig.Host.SetDeviceSettingsAsync(id, 0.5f, 10, EqGains.Flat);
        AssertSettings(0.5f, 10, EqGains.Flat, await client.ReadAsync());
        Assert.Equal(0.5f, rig.Host.Clients[0].Volume);
        Assert.Equal(10.0, rig.Host.Clients[0].OffsetMs);

        await rig.Host.SetDeviceSettingsAsync(id, 3f, -500, EqGains.Flat);   // clamped to 0..1 and ±100 ms
        AssertSettings(1f, -100, EqGains.Flat, await client.ReadAsync());

        await rig.Host.SetLatencyAsync(900);
        Assert.Equal(new SetLatency(500), await client.ReadAsync());
        Assert.Equal(500, rig.Host.TargetLatencyMs);
        Assert.Equal(500, rig.Pipeline.Assembler.TargetLatencyMs);

        await rig.Host.SetLatencyAsync(50);
        Assert.Equal(new SetLatency(100), await client.ReadAsync());
        Assert.Equal(100, rig.Host.TargetLatencyMs);
        Assert.Equal(100, rig.Pipeline.Assembler.TargetLatencyMs);
    }

    private static void AssertSettings(float volume, double offsetMs, EqGains eq, ControlMessage? actual)
    {
        var s = Assert.IsType<SetDeviceSettings>(actual);
        Assert.Equal(volume, s.Volume);
        Assert.Equal(offsetMs, s.OffsetMs);
        Assert.Equal(eq.ToArray(), s.EqGainsDb);
    }

    [Fact]
    public async Task SetDeviceSettingsAsync_updates_ConnectedClient_Eq_and_sends_it()
    {
        await using var rig = await new Rig().StartAsync();
        var id = Guid.NewGuid();
        var (client, _) = await rig.JoinAsync(id);
        var eq = EqPresets.All.First(p => p.Name == "Laptop speakers").Gains;

        await rig.Host.SetDeviceSettingsAsync(id, 0.5f, 10, eq);

        AssertSettings(0.5f, 10, eq, await client.ReadAsync());
        Assert.Equal(eq, rig.Host.Clients[0].Eq);

        await Assert.ThrowsAsync<ArgumentNullException>(() => rig.Host.SetDeviceSettingsAsync(id, 0.5f, 10, null!));
    }

    [Fact]
    public async Task A_new_connection_starts_flat()
    {
        await using var rig = await new Rig().StartAsync();
        await rig.JoinAsync(Guid.NewGuid());

        var c = Assert.Single(rig.Host.Clients);
        Assert.Equal(EqGains.Flat, c.Eq);
        Assert.Equal(1f, c.Volume);
        Assert.Equal(0, c.OffsetMs);
    }

    [Fact]
    public async Task Late_joiner_gets_the_current_latency_in_welcome()
    {
        await using var rig = await new Rig().StartAsync();
        await rig.Host.SetLatencyAsync(350);

        var (_, reply) = await rig.JoinAsync(Guid.NewGuid());

        Assert.Equal(350, Assert.IsType<Welcome>(reply).TargetLatencyMs);
    }

    [Theory]
    [InlineData(float.NaN, 0.0)]
    [InlineData(float.PositiveInfinity, 0.0)]
    [InlineData(0.5f, double.NaN)]
    [InlineData(0.5f, double.NegativeInfinity)]
    public async Task Non_finite_device_settings_are_rejected(float volume, double offset)
    {
        await using var rig = await new Rig().StartAsync();
        var id = Guid.NewGuid();
        await rig.JoinAsync(id);

        await Assert.ThrowsAsync<ArgumentException>(() => rig.Host.SetDeviceSettingsAsync(id, volume, offset, EqGains.Flat));
    }

    [Fact]
    public async Task Same_device_reconnect_replaces_connection()
    {
        // MaxClients = 1: the reconnect must not be counted twice.
        await using var rig = await new Rig(new HostSessionOptions("Host", ControlPort: 0, MaxClients: 1, PairingCode: "1234")).StartAsync();
        var id = Guid.NewGuid();
        var (first, _) = await rig.JoinAsync(id, audioPort: 5000);
        await first.SendAsync(new Ready());
        await WaitUntil(() => rig.Host.Clients.Count == 1 && rig.Host.Clients[0].Ready);

        var (second, reply) = await rig.JoinAsync(id, audioPort: 5001);

        Assert.IsType<Welcome>(reply);
        Assert.Null(await ReadUntilClosed(first));        // old connection closed
        var client = Assert.Single(rig.Host.Clients);
        Assert.Equal(5001, client.AudioEndPoint.Port);
        Assert.False(client.Ready);                        // must Ready again on the new connection

        // The old connection's teardown must not remove the new one.
        await Task.Delay(100);
        Assert.Single(rig.Host.Clients);
        await rig.Host.SetLatencyAsync(300);
        Assert.Equal(new SetLatency(300), await second.ReadAsync());
    }

    [Fact]
    public async Task Bye_removes_the_client()
    {
        await using var rig = await new Rig().StartAsync();
        var (client, _) = await rig.JoinAsync(Guid.NewGuid());

        await client.SendAsync(new Bye());

        await WaitUntil(() => rig.Host.Clients.Count == 0);
    }

    [Fact]
    public async Task Pairing_code_is_four_random_digits_when_not_specified()
    {
        var codes = new HashSet<string>();
        for (int i = 0; i < 20; i++)
        {
            await using var rig = new Rig(new HostSessionOptions("Host", ControlPort: 0));
            Assert.Matches(new Regex("^[0-9]{4}$"), rig.Host.PairingCode);
            codes.Add(rig.Host.PairingCode);
        }
        Assert.True(codes.Count > 1);

        await using var joined = await new Rig(new HostSessionOptions("Host", ControlPort: 0)).StartAsync();
        Assert.IsType<Welcome>((await joined.JoinAsync(Guid.NewGuid(), code: joined.Host.PairingCode)).Reply);
    }

    [Theory]
    [InlineData("noid")]
    [InlineData("nocode")]
    [InlineData("noname")]
    [InlineData("blankname")]
    [InlineData("noport")]
    [InlineData("highport")]
    public async Task Invalid_hello_is_closed_and_never_crashes_the_host(string kind)
    {
        await using var rig = await new Rig().StartAsync();
        var id = Guid.NewGuid();
        Hello hello = kind switch
        {
            "noid" => new Hello(Guid.Empty, "Laptop", "1234", HostSession.ProtocolVersion, 5000),
            "nocode" => new Hello(id, "Laptop", null!, HostSession.ProtocolVersion, 5000),
            "noname" => new Hello(id, null!, "1234", HostSession.ProtocolVersion, 5000),
            "blankname" => new Hello(id, "   ", "1234", HostSession.ProtocolVersion, 5000),
            "noport" => new Hello(id, "Laptop", "1234", HostSession.ProtocolVersion, 0),
            _ => new Hello(id, "Laptop", "1234", HostSession.ProtocolVersion, 70000),
        };
        var bad = await rig.ConnectAsync();

        await bad.SendAsync(hello);

        var reply = await bad.ReadAsync();
        if (reply is not null) Assert.Equal(new Reject(RejectReason.BadCode), reply);
        Assert.Null(await ReadUntilClosed(bad));
        Assert.Empty(rig.Host.Clients);
        Assert.IsType<Welcome>((await rig.JoinAsync(Guid.NewGuid())).Reply);   // host still serving
    }

    [Fact]
    public async Task Missing_members_hello_from_raw_json_is_closed()
    {
        await using var rig = await new Rig().StartAsync();
        var bad = await rig.ConnectAsync();
        var body = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"Hello\"}");
        var frame = new byte[4 + body.Length];
        BitConverter.TryWriteBytes(frame, (uint)body.Length);
        body.CopyTo(frame, 4);

        await bad.SendRawAsync(frame);

        Assert.Null(await ReadUntilClosed(bad));
        Assert.Empty(rig.Host.Clients);
        Assert.IsType<Welcome>((await rig.JoinAsync(Guid.NewGuid())).Reply);
    }

    [Fact]
    public async Task Invalid_hello_counts_toward_lockout()
    {
        await using var rig = await new Rig().StartAsync();
        var id = Guid.NewGuid();

        for (int i = 0; i < 3; i++)
        {
            var bad = await rig.ConnectAsync();
            await bad.SendAsync(new Hello(id, "", "1234", HostSession.ProtocolVersion, 5000));
            Assert.Null(await ReadUntilClosed(bad));
        }

        Assert.Equal(new Reject(RejectReason.LockedOut), (await rig.JoinAsync(id)).Reply);
    }

    [Fact]
    public async Task Hostile_frames_close_only_that_connection()
    {
        await using var rig = await new Rig().StartAsync();
        var (good, _) = await rig.JoinAsync(Guid.NewGuid());

        // Oversize length prefix before any Hello.
        var oversize = await rig.ConnectAsync();
        await oversize.SendRawAsync(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
        Assert.Null(await ReadUntilClosed(oversize));

        // Not a Hello first.
        var wrongFirst = await rig.ConnectAsync();
        await wrongFirst.SendAsync(new Ready());
        Assert.Null(await ReadUntilClosed(wrongFirst));

        // Malformed JSON after a successful join.
        var (mid, _) = await rig.JoinAsync(Guid.NewGuid());
        var junk = System.Text.Encoding.UTF8.GetBytes("not json at all");
        var frame = new byte[4 + junk.Length];
        BitConverter.TryWriteBytes(frame, (uint)junk.Length);
        junk.CopyTo(frame, 4);
        await mid.SendRawAsync(frame);
        Assert.Null(await ReadUntilClosed(mid));
        await WaitUntil(() => rig.Host.Clients.Count == 1);

        // The surviving client and the listener are intact.
        await rig.Host.SetLatencyAsync(250);
        Assert.Equal(new SetLatency(250), await good.ReadAsync());
        Assert.IsType<Welcome>((await rig.JoinAsync(Guid.NewGuid())).Reply);
    }

    [Fact]
    public async Task Disposing_the_session_closes_connections()
    {
        await using var rig = await new Rig().StartAsync();
        var (client, _) = await rig.JoinAsync(Guid.NewGuid());

        await rig.Host.DisposeAsync();

        Assert.Null(await ReadUntilClosed(client));
    }

    /// <summary>Reads (discarding any remaining messages) until the host closes the connection.</summary>
    private static async Task<ControlMessage?> ReadUntilClosed(TestClient c)
    {
        while (true)
        {
            ControlMessage? m;
            try { m = await c.ReadAsync(); }
            catch (InvalidDataException) { return null; }
            if (m is null) return null;
        }
    }
}
