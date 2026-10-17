using System.Net;
using RhythmSync.Core.Net;
using RhythmSync.Core.Protocol;

namespace RhythmSync.Core.Clock;

/// <summary>
/// Host side of clock sync: answers every ping of its session with a pong carrying the host receive time
/// (<c>t2</c>, read first thing on receive) and send time (<c>t3</c>, read immediately before sending), both on
/// the host session clock. Pings from other sessions and malformed datagrams are ignored. Owns the channel and
/// disposes it. Datagrams are handled one at a time (the channel's receive thread); the handler does not allocate.
/// </summary>
public sealed class ClockSyncServer : IDisposable
{
    private readonly IDatagramChannel _channel;
    private readonly ISessionClock _clock;
    private readonly uint _sessionId;
    private readonly byte[] _pong = new byte[PacketSerializer.PongBytes];
    private volatile bool _disposed;

    public ClockSyncServer(IDatagramChannel ch, ISessionClock clock, uint sessionId)
    {
        _channel = ch ?? throw new ArgumentNullException(nameof(ch));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _sessionId = sessionId;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _channel.Start(OnDatagram);
    }

    private void OnDatagram(ReadOnlySpan<byte> data, IPEndPoint from)
    {
        long t2 = _clock.NowTicks;
        if (_disposed || !PacketSerializer.TryReadPing(data, out var ping) || ping.SessionId != _sessionId) return;

        long t3 = _clock.NowTicks;
        int length = PacketSerializer.WritePong(_pong, new PongPacket(_sessionId, ping.PingId, ping.T1, t2, t3));
        _channel.Send(_pong.AsSpan(0, length), from);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _channel.Dispose();
    }
}
