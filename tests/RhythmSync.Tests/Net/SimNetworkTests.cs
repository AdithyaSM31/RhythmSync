using System.Net;
using RhythmSync.Tests.Sim;

namespace RhythmSync.Tests.Net;

/// <summary>The simulated channel honours the same <c>IDatagramChannel</c> guarantees as the UDP one.</summary>
public class SimNetworkTests
{
    [Fact]
    public void Send_after_Dispose_does_not_throw_and_delivers_nothing()
    {
        var sim = new SimScheduler();
        var net = new SimNetwork(sim, 1, new SimLinkProfile(LossProbability: 0));
        var a = net.Create(new IPEndPoint(IPAddress.Loopback, 1000));
        var b = net.Create(new IPEndPoint(IPAddress.Loopback, 2000));
        int received = 0;
        b.Start((_, _) => received++);

        a.Dispose();
        a.Send(new byte[] { 1 }, b.LocalEndPoint);
        a.Dispose();                             // twice is fine
        sim.RunUntil(1_000_000_000);

        Assert.Equal(0, received);
        Assert.Equal(0, net.Sent);
    }
}
