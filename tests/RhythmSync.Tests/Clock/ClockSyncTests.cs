using System.Net;
using RhythmSync.Core.Clock;
using RhythmSync.Core.Net;
using RhythmSync.Core.Protocol;
using RhythmSync.Tests.Sim;

namespace RhythmSync.Tests.Clock;

public class ClockSyncTests
{
    private const long Second = 10_000_000;
    private const long TickInterval = 100_000;   // 10 ms
    private const uint SessionId = 0xC0FFEE;

    private static readonly IPEndPoint HostClock = new(IPAddress.Parse("10.0.0.1"), 47803);
    private static readonly IPEndPoint ClientClock = new(IPAddress.Parse("10.0.0.2"), 50000);

    private sealed class Rig
    {
        public readonly SimScheduler Sim = new();
        public readonly SimNetwork Net;
        public readonly DriftingClock Host;
        public readonly DriftingClock Client;
        public readonly ClockEstimator Estimator = new();
        public readonly ClockSyncClient SyncClient;

        public Rig(uint serverSession = SessionId, uint clientSession = SessionId, int seed = 1, SimLinkProfile? profile = null,
                   bool startServer = true)
        {
            Net = new SimNetwork(Sim, seed, profile ?? new SimLinkProfile());
            Host = new DriftingClock(Sim, 0, 0);
            Client = new DriftingClock(Sim, 12 * Second, +60);
            if (startServer)
                new ClockSyncServer(Net.Create(HostClock), Host, serverSession).Start();
            SyncClient = new ClockSyncClient(Net.Create(ClientClock), Client, HostClock, clientSession, Estimator);
        }

        /// <summary>Starts the client now and calls Tick every 10 ms of sim time until <paramref name="until"/>.</summary>
        public void StartTicking()
        {
            SyncClient.Start();
            Schedule(Sim.Now);
        }

        private void Schedule(long at) => Sim.Schedule(at, () =>
        {
            SyncClient.Tick();
            Schedule(at + TickInterval);
        });
    }

    [Fact]
    public void Clock_sync_converges_over_sim_network()
    {
        var rig = new Rig();
        rig.StartTicking();

        rig.Sim.RunUntil(3 * Second);
        Assert.True(rig.Estimator.IsConverged, $"not converged after 3 s (std error {rig.Estimator.StdErrorMs} ms)");

        rig.Sim.RunUntil(60 * Second);
        long error = rig.Estimator.ClientToHost(rig.Client.NowTicks) - rig.Host.NowTicks;
        Assert.True(Math.Abs(error) < 2000, $"mapping error {error} ticks");
    }

    [Fact]
    public void Ping_rate_is_fast_then_slow()
    {
        var rig = new Rig();
        rig.StartTicking();

        rig.Sim.RunUntil(2 * Second);
        long atTwo = rig.SyncClient.PingsSent;
        Assert.InRange(atTwo, 39, 41);

        rig.Sim.RunUntil(12 * Second);
        Assert.InRange(rig.SyncClient.PingsSent - atTwo, 39, 41);
    }

    [Fact]
    public void Pings_and_pongs_with_wrong_session_are_ignored()
    {
        var lossless = new SimLinkProfile(TailProbability: 0, LossProbability: 0);

        // Server ignores a client from another session: no pong ever comes back.
        var wrongServer = new Rig(serverSession: SessionId + 1, profile: lossless);
        wrongServer.StartTicking();
        wrongServer.Sim.RunUntil(2 * Second);
        Assert.True(wrongServer.SyncClient.PingsSent > 30);
        Assert.Equal(0, wrongServer.Estimator.SampleCount);

        // Client ignores pongs carrying another session (even with a matching ping id and t1),
        // but accepts the same pongs with its own session.
        Assert.Equal(0, RunAgainstEchoServer(SessionId + 1, lossless));
        Assert.True(RunAgainstEchoServer(SessionId, lossless) > 30);
    }

    [Fact]
    public void Pongs_for_unknown_ping_ids_are_ignored()
    {
        var rig = new Rig(profile: new SimLinkProfile(TailProbability: 0, LossProbability: 0), startServer: false);
        var forger = rig.Net.Create(HostClock);
        forger.Start((data, from) =>
        {
            Assert.True(PacketSerializer.TryReadPing(data, out var ping));
            var pong = new byte[PacketSerializer.PongBytes];
            PacketSerializer.WritePong(pong, new PongPacket(SessionId, ping.PingId + 1000, ping.T1, 0, 0));
            forger.Send(pong, from);
        });
        rig.StartTicking();
        rig.Sim.RunUntil(Second);

        Assert.True(rig.SyncClient.PingsSent > 10);
        Assert.Equal(0, rig.Estimator.SampleCount);
    }

    [Fact]
    public void Server_stamps_receive_and_send_times_and_answers_sender()
    {
        var sim = new SimScheduler();
        var net = new SimNetwork(sim, 7, new SimLinkProfile(BaseDelayMs: 1, JitterMeanMs: 0, TailProbability: 0, LossProbability: 0));
        var host = new DriftingClock(sim, 5 * Second, 0);
        using var server = new ClockSyncServer(net.Create(HostClock), host, SessionId);
        server.Start();

        var probe = net.Create(ClientClock);
        PongPacket? received = null;
        probe.Start((data, _) => { Assert.True(PacketSerializer.TryReadPong(data, out var p)); received = p; });

        var ping = new byte[PacketSerializer.PingBytes];
        PacketSerializer.WritePing(ping, new PingPacket(SessionId, 42, 123_456));
        probe.Send(ping, HostClock);
        sim.RunUntil(Second);

        Assert.NotNull(received);
        Assert.Equal(new PongPacket(SessionId, 42, 123_456, 5 * Second + 10_000, 5 * Second + 10_000), received.Value);
    }

    /// <summary>Runs a client for 2 s against a fake server that echoes every ping as a pong with <paramref name="pongSession"/>.</summary>
    private static int RunAgainstEchoServer(uint pongSession, SimLinkProfile profile)
    {
        var rig = new Rig(profile: profile, startServer: false);
        IDatagramChannel echo = rig.Net.Create(HostClock);
        var pong = new byte[PacketSerializer.PongBytes];
        echo.Start((data, from) =>
        {
            Assert.True(PacketSerializer.TryReadPing(data, out var ping));
            long now = rig.Host.NowTicks;
            PacketSerializer.WritePong(pong, new PongPacket(pongSession, ping.PingId, ping.T1, now, now));
            echo.Send(pong, from);
        });
        rig.StartTicking();
        rig.Sim.RunUntil(2 * Second);
        return rig.Estimator.SampleCount;
    }
}
