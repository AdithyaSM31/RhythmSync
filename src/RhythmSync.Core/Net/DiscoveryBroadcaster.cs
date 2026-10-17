using System.Net;
using RhythmSync.Core.Clock;
using RhythmSync.Core.Protocol;

namespace RhythmSync.Core.Net;

/// <summary>
/// Host side of LAN discovery: <see cref="Tick"/> (called often, e.g. every 100 ms) sends one beacon to the target
/// (the broadcast address in the app) when at least <see cref="IntervalTicks"/> have passed since the last one; the
/// first call sends immediately. The beacon is rebuilt from the callback each time, so a renamed host announces its new
/// name. Owns the channel and disposes it. Not thread-safe: call <see cref="Tick"/> from one thread.
/// </summary>
public sealed class DiscoveryBroadcaster : IDisposable
{
    public const long IntervalTicks = AudioFormat.TicksPerSecond;   // 1 s

    private readonly IDatagramChannel _channel;
    private readonly IPEndPoint _target;
    private readonly ISessionClock _clock;
    private readonly Func<BeaconPacket> _beacon;
    private readonly byte[] _buffer = new byte[PacketSerializer.HeaderBytes + 4 + PacketSerializer.MaxBeaconNameBytes];
    private long? _lastSentTicks;

    public DiscoveryBroadcaster(IDatagramChannel ch, IPEndPoint target, ISessionClock clock, Func<BeaconPacket> beacon)
    {
        _channel = ch ?? throw new ArgumentNullException(nameof(ch));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _beacon = beacon ?? throw new ArgumentNullException(nameof(beacon));
    }

    /// <summary>Sends a beacon if one is due.</summary>
    public void Tick()
    {
        long now = _clock.NowTicks;
        if (_lastSentTicks is long last && now - last < IntervalTicks) return;

        _lastSentTicks = now;
        int length = PacketSerializer.WriteBeacon(_buffer, _beacon());
        _channel.Send(_buffer.AsSpan(0, length), _target);
    }

    public void Dispose() => _channel.Dispose();
}
