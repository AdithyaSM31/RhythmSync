using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RhythmSync.Core.Net;

namespace RhythmSync.Tests.Net;

/// <summary>Real sockets on 127.0.0.1 with ephemeral ports.</summary>
public class UdpDatagramChannelTests
{
    private static readonly IPEndPoint AnyLoopback = new(IPAddress.Loopback, 0);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public void Datagram_arrives_with_exact_length_and_sender_endpoint()
    {
        using var a = new UdpDatagramChannel(AnyLoopback);
        using var b = new UdpDatagramChannel(AnyLoopback);
        Assert.NotEqual(0, a.LocalEndPoint.Port);
        byte[]? data = null;
        IPEndPoint? sender = null;
        var got = new ManualResetEventSlim();
        b.Start((d, from) =>
        {
            data = d.ToArray();
            sender = from;
            got.Set();
        });

        var payload = Enumerable.Range(0, 982).Select(i => (byte)i).ToArray();
        a.Send(payload, b.LocalEndPoint);

        Assert.True(got.Wait(Wait));
        Assert.Equal(payload, data);
        Assert.Equal(a.LocalEndPoint, sender);
    }

    [Fact]
    public void Handler_is_called_serially_from_one_thread()
    {
        using var a = new UdpDatagramChannel(AnyLoopback);
        using var b = new UdpDatagramChannel(AnyLoopback);
        const int Count = 300;
        int active = 0, overlaps = 0, received = 0;
        var threads = new HashSet<int>();
        var done = new ManualResetEventSlim();
        b.Start((_, _) =>
        {
            if (Interlocked.Increment(ref active) > 1) Interlocked.Increment(ref overlaps);
            lock (threads) threads.Add(Environment.CurrentManagedThreadId);
            Thread.SpinWait(2000);
            Interlocked.Decrement(ref active);
            if (Interlocked.Increment(ref received) == Count) done.Set();
        });

        var senders = Enumerable.Range(0, 3).Select(_ => new Thread(() =>
        {
            for (int i = 0; i < Count / 3; i++)
            {
                a.Send(new byte[] { 1, 2, 3, 4 }, b.LocalEndPoint);
                if (i % 20 == 0) Thread.Sleep(1);   // keep the socket buffer from overflowing on slow machines
            }
        })).ToArray();
        foreach (var t in senders) t.Start();
        foreach (var t in senders) t.Join();

        Assert.True(done.Wait(Wait), $"only {received} of {Count} datagrams arrived");
        Assert.Equal(0, overlaps);
        Assert.Single(threads);
        Assert.NotEqual(Environment.CurrentManagedThreadId, threads.Single());
    }

    [Fact]
    public void Send_to_a_closed_port_does_not_throw_and_receiving_continues()
    {
        // Reserve a port, then free it, so datagrams to it provoke ICMP port-unreachable (WSAECONNRESET on the next receive).
        int closedPort;
        using (var probe = new UdpClient(AnyLoopback)) closedPort = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        var closed = new IPEndPoint(IPAddress.Loopback, closedPort);

        using var a = new UdpDatagramChannel(AnyLoopback);
        using var b = new UdpDatagramChannel(AnyLoopback);
        var got = new SemaphoreSlim(0);
        a.Start((_, _) => got.Release());

        for (int i = 0; i < 20; i++)
        {
            a.Send(new byte[] { 9 }, closed);
            Thread.Sleep(5);
        }
        b.Send(new byte[] { 1 }, a.LocalEndPoint);

        Assert.True(got.Wait(Wait), "the receive loop died after ICMP port-unreachable");
        Assert.Equal(0, a.ReceiveErrors);        // SIO_UDP_CONNRESET is off: the ICMP error never reaches the receive path
        Assert.Equal(0, a.SendErrors);
    }

    [Fact]
    public void Send_does_not_allocate_after_the_first_send_to_a_destination()
    {
        using var a = new UdpDatagramChannel(AnyLoopback);
        using var b = new UdpDatagramChannel(AnyLoopback);
        b.Start((_, _) => { });
        var payload = new byte[982];
        var to = b.LocalEndPoint;
        for (int i = 0; i < 100; i++) a.Send(payload, to);   // warm up: caches the address, JITs the path

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            a.Send(payload, to);
            if (i % 50 == 0) Thread.Sleep(1);                 // let the receiver drain
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(0, a.SendErrors);
    }

    [Fact]
    public void Send_after_Dispose_does_not_throw_and_Dispose_twice_is_fine()
    {
        var a = new UdpDatagramChannel(AnyLoopback);
        a.Start((_, _) => { });
        var target = new IPEndPoint(IPAddress.Loopback, 9);

        a.Dispose();
        a.Send(new byte[] { 1 }, target);
        a.Dispose();
    }

    [Fact]
    public void Send_to_an_unusable_destination_does_not_throw()
    {
        using var a = new UdpDatagramChannel(AnyLoopback);
        a.Send(new byte[] { 1 }, new IPEndPoint(IPAddress.Parse("203.0.113.1"), 47802));   // TEST-NET-3, never routed
        a.Send(new byte[] { 1 }, new IPEndPoint(IPAddress.Any, 0));                       // not a valid destination
        a.Send(new byte[] { 1 }, new IPEndPoint(IPAddress.IPv6Loopback, 9));              // wrong address family for the socket
    }

    [Fact]
    public void Dispose_stops_the_receive_thread_promptly_even_while_it_is_blocked_in_receive()
    {
        var a = new UdpDatagramChannel(AnyLoopback);
        a.Start((_, _) => { });
        Thread.Sleep(50);                        // let the thread block in ReceiveFrom

        var sw = Stopwatch.StartNew();
        a.Dispose();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"Dispose took {sw.Elapsed}");
    }

    [Fact]
    public void Dispose_from_inside_the_handler_does_not_deadlock()
    {
        using var a = new UdpDatagramChannel(AnyLoopback);
        var b = new UdpDatagramChannel(AnyLoopback);
        var disposed = new ManualResetEventSlim();
        b.Start((_, _) =>
        {
            b.Dispose();
            disposed.Set();
        });

        a.Send(new byte[] { 1 }, b.LocalEndPoint);

        Assert.True(disposed.Wait(Wait));
    }

    [Fact]
    public void Start_twice_throws()
    {
        using var a = new UdpDatagramChannel(AnyLoopback);
        a.Start((_, _) => { });
        Assert.Throws<InvalidOperationException>(() => a.Start((_, _) => { }));
    }

    [Fact]
    public void Broadcast_channel_binds_and_the_factory_creates_channels()
    {
        using var broadcast = new UdpDatagramChannel(AnyLoopback, broadcast: true);
        Assert.NotEqual(0, broadcast.LocalEndPoint.Port);

        using var viaFactory = new UdpDatagramChannelFactory().Create(AnyLoopback);
        Assert.IsType<UdpDatagramChannel>(viaFactory);
    }
}
