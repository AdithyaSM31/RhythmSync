using System.Net;
using RhythmSync.Core.Net;
using RhythmSync.Core.Protocol;

namespace RhythmSync.Core.Clock;

/// <summary>
/// Client side of clock sync. <see cref="Tick"/> (called often, e.g. every 10 ms) sends a ping when the interval has
/// elapsed on the client clock: every <see cref="FastIntervalTicks"/> during the first <see cref="FastPhaseTicks"/>
/// after <see cref="Start"/>, then every <see cref="SlowIntervalTicks"/>. A pong is accepted only if it carries our
/// session id and answers one of the last <see cref="TrackedPings"/> pings (same id and echoed <c>t1</c>); each
/// accepted pong feeds <see cref="ClockEstimator.AddSample"/> once with <c>t4</c> read on receipt. Owns the channel
/// and disposes it. <see cref="Tick"/> and the receive handler may run on different threads.
/// </summary>
public sealed class ClockSyncClient : IDisposable
{
    public const long FastIntervalTicks = 500_000;          // 50 ms
    public const long SlowIntervalTicks = 2_500_000;        // 250 ms
    public const long FastPhaseTicks = 20_000_000;          // 2 s

    /// <summary>Outstanding pings remembered for matching pongs (covers RTTs up to 8 · 50 ms in the fast phase).</summary>
    public const int TrackedPings = 8;

    private readonly IDatagramChannel _channel;
    private readonly ISessionClock _clock;
    private readonly IPEndPoint _server;
    private readonly uint _sessionId;
    private readonly ClockEstimator _estimator;
    private readonly byte[] _ping = new byte[PacketSerializer.PingBytes];

    private readonly object _gate = new();
    private readonly uint[] _outstandingIds = new uint[TrackedPings];
    private readonly long[] _outstandingT1 = new long[TrackedPings];
    private readonly bool[] _outstanding = new bool[TrackedPings];

    private bool _started;
    private long _startTicks;
    private long? _lastPingTicks;
    private uint _nextPingId = 1;
    private long _pingsSent;
    private volatile bool _disposed;

    public ClockSyncClient(IDatagramChannel ch, ISessionClock clock, IPEndPoint server, uint sessionId, ClockEstimator estimator)
    {
        _channel = ch ?? throw new ArgumentNullException(nameof(ch));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _estimator = estimator ?? throw new ArgumentNullException(nameof(estimator));
        _sessionId = sessionId;
    }

    public long PingsSent => Interlocked.Read(ref _pingsSent);

    /// <summary>Starts receiving pongs and the fast ping phase; the first ping goes out on the next <see cref="Tick"/>.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_started) throw new InvalidOperationException("Already started.");
            _started = true;
            _startTicks = _clock.NowTicks;
        }
        _channel.Start(OnDatagram);
    }

    /// <summary>Sends a ping if one is due.</summary>
    public void Tick()
    {
        if (_disposed) return;
        lock (_gate)
        {
            if (!_started) return;
            long now = _clock.NowTicks;
            long interval = now - _startTicks < FastPhaseTicks ? FastIntervalTicks : SlowIntervalTicks;
            if (_lastPingTicks is long last && now - last < interval) return;

            uint id = _nextPingId++;
            int slot = (int)(id % TrackedPings);
            _outstandingIds[slot] = id;
            _outstandingT1[slot] = now;
            _outstanding[slot] = true;
            _lastPingTicks = now;

            int length = PacketSerializer.WritePing(_ping, new PingPacket(_sessionId, id, now));
            _channel.Send(_ping.AsSpan(0, length), _server);
            Interlocked.Increment(ref _pingsSent);
        }
    }

    private void OnDatagram(ReadOnlySpan<byte> data, IPEndPoint from)
    {
        long t4 = _clock.NowTicks;
        if (_disposed || !PacketSerializer.TryReadPong(data, out var pong) || pong.SessionId != _sessionId) return;

        lock (_gate)
        {
            int slot = (int)(pong.PingId % TrackedPings);
            if (!_outstanding[slot] || _outstandingIds[slot] != pong.PingId || _outstandingT1[slot] != pong.T1) return;
            _outstanding[slot] = false;   // a duplicated pong is counted once
        }
        _estimator.AddSample(pong.T1, pong.T2, pong.T3, t4);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _channel.Dispose();
    }
}
