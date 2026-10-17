using System.Net;
using RhythmSync.Core;
using RhythmSync.Core.Net;
using RhythmSync.Core.Protocol;
using RhythmSync.Tests.Sim;

namespace RhythmSync.Tests.Net;

public class DiscoveryTests
{
    private const long Ms = AudioFormat.TicksPerSecond / 1000;
    private static readonly IPEndPoint ListenerAt = new(IPAddress.Parse("10.0.0.9"), 47800);

    private readonly SimScheduler _sim = new();
    private readonly ManualClock _clock = new();
    private readonly SimNetwork _net;

    public DiscoveryTests()
    {
        // Loss-free, jitter-free link: a datagram arrives 0.5 ms after it is sent.
        _net = new SimNetwork(_sim, seed: 1, new SimLinkProfile(BaseDelayMs: 0.5, JitterMeanMs: 0, TailProbability: 0, LossProbability: 0));
    }

    private DiscoveryBroadcaster NewBroadcaster(string address, uint sessionId, string name, ushort controlPort = 47801)
    {
        var channel = _net.Create(new IPEndPoint(IPAddress.Parse(address), 0));
        return new DiscoveryBroadcaster(channel, ListenerAt, _clock, () => new BeaconPacket(sessionId, controlPort, PacketSerializer.Version, name));
    }

    private DiscoveryListener NewListener()
    {
        var listener = new DiscoveryListener(_net.Create(ListenerAt), _clock);
        listener.Start();
        return listener;
    }

    /// <summary>Moves both timelines forward; the session clock is set first so datagrams are stamped with the new time.</summary>
    private void Advance(long ticks)
    {
        _clock.NowTicks += ticks;
        _sim.RunUntil(_sim.Now + ticks);
    }

    [Fact]
    public void Listener_sees_and_expires_hosts()
    {
        using var listener = NewListener();
        var broadcaster = NewBroadcaster("10.0.0.1", 0xABCD, "Living room PC", controlPort: 51000);
        int changed = 0;
        listener.Changed += () => changed++;

        broadcaster.Tick();                      // beacon at t = 0
        Advance(1 * Ms);

        var host = Assert.Single(listener.Hosts);
        Assert.Equal(0xABCDu, host.SessionId);
        Assert.Equal("Living room PC", host.Name);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("10.0.0.1"), 51000), host.ControlEndPoint);   // sender IP + beacon control port
        Assert.Equal(1, changed);

        Advance(3_100 * Ms - 1 * Ms);            // t = 3.1 s, no further beacons
        listener.Tick();

        Assert.Empty(listener.Hosts);
        Assert.Equal(2, changed);
    }

    [Fact]
    public void Hosts_hides_an_expired_host_even_before_Tick()
    {
        using var listener = NewListener();
        var broadcaster = NewBroadcaster("10.0.0.1", 1, "A");
        broadcaster.Tick();
        Advance(1 * Ms);
        Assert.Single(listener.Hosts);

        Advance(2_900 * Ms);                     // 2.9 s since the beacon: still listed
        Assert.Single(listener.Hosts);

        Advance(200 * Ms);                       // 3.1 s: gone from Hosts without anyone calling Tick
        Assert.Empty(listener.Hosts);
    }

    [Fact]
    public void Refreshing_a_host_does_not_raise_Changed_but_a_new_name_or_control_port_does()
    {
        using var listener = NewListener();
        string name = "Kitchen";
        ushort port = 47801;
        var channel = _net.Create(new IPEndPoint(IPAddress.Parse("10.0.0.1"), 0));
        var broadcaster = new DiscoveryBroadcaster(channel, ListenerAt, _clock, () => new BeaconPacket(7, port, PacketSerializer.Version, name));
        int changed = 0;
        listener.Changed += () => changed++;

        for (int i = 0; i < 4; i++)              // four beacons, one second apart
        {
            broadcaster.Tick();
            Advance(1_000 * Ms);
        }
        Assert.Equal(1, changed);
        Assert.Single(listener.Hosts);

        name = "Kitchen 2";
        broadcaster.Tick();
        Advance(1 * Ms);
        Assert.Equal(2, changed);
        Assert.Equal("Kitchen 2", Assert.Single(listener.Hosts).Name);

        Advance(1_000 * Ms);
        port = 47900;
        broadcaster.Tick();
        Advance(1 * Ms);
        Assert.Equal(3, changed);
        Assert.Equal(47900, Assert.Single(listener.Hosts).ControlEndPoint.Port);
    }

    [Fact]
    public void Hosts_are_keyed_by_sender_address_and_session()
    {
        using var listener = NewListener();
        // Same session id from two machines, and two sessions from one machine: four distinct hosts.
        var broadcasters = new[]
        {
            NewBroadcaster("10.0.0.1", 1, "A1"), NewBroadcaster("10.0.0.2", 1, "B1"),
            NewBroadcaster("10.0.0.1", 2, "A2"), NewBroadcaster("10.0.0.2", 2, "B2"),
        };
        foreach (var b in broadcasters) b.Tick();
        Advance(1 * Ms);

        Assert.Equal(4, listener.Hosts.Count);
        Assert.Equal(new[] { "A1", "A2", "B1", "B2" }, listener.Hosts.Select(h => h.Name).OrderBy(n => n));
    }

    [Fact]
    public void Expiry_removes_only_the_silent_host()
    {
        using var listener = NewListener();
        var quiet = NewBroadcaster("10.0.0.1", 1, "Quiet");
        var chatty = NewBroadcaster("10.0.0.2", 2, "Chatty");
        quiet.Tick();
        chatty.Tick();
        Advance(1 * Ms);
        Assert.Equal(2, listener.Hosts.Count);
        int changed = 0;
        listener.Changed += () => changed++;

        for (int i = 0; i < 4; i++)              // Chatty keeps beaconing; Quiet falls silent after t = 0
        {
            Advance(1_000 * Ms);
            chatty.Tick();
        }
        Advance(1 * Ms);

        // The datagram arriving after Quiet's 3 s were up already pruned it and raised Changed once.
        Assert.Equal("Chatty", Assert.Single(listener.Hosts).Name);
        Assert.Equal(1, changed);
        listener.Tick();                         // nothing more to expire
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Listener_ignores_datagrams_that_are_not_beacons()
    {
        using var listener = NewListener();
        var sender = _net.Create(new IPEndPoint(IPAddress.Parse("10.0.0.1"), 0));
        int changed = 0;
        listener.Changed += () => changed++;

        sender.Send(new byte[] { 1, 2, 3 }, ListenerAt);
        var ping = new byte[PacketSerializer.PingBytes];
        PacketSerializer.WritePing(ping, new PingPacket(1, 1, 1));
        sender.Send(ping, ListenerAt);
        Advance(1 * Ms);

        Assert.Empty(listener.Hosts);
        Assert.Equal(0, changed);
    }

    [Fact]
    public void Broadcaster_sends_once_per_second()
    {
        var received = new List<BeaconPacket>();
        var receiver = _net.Create(ListenerAt);
        receiver.Start((data, _) =>
        {
            Assert.True(PacketSerializer.TryReadBeacon(data, out var beacon));
            received.Add(beacon!);
        });
        var broadcaster = NewBroadcaster("10.0.0.1", 99, "Host");

        for (int i = 0; i < 50; i++)             // Tick every 100 ms for 5 s (t = 0 ... 4.9 s)
        {
            broadcaster.Tick();
            Advance(100 * Ms);
        }

        Assert.Equal(5, received.Count);
        Assert.All(received, b => Assert.Equal(99u, b.SessionId));
    }
}
