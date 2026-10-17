using System.Net;
using RhythmSync.Core.Net;

namespace RhythmSync.Tests.Sim;

/// <summary>
/// One-way link characteristics: each datagram takes <c>BaseDelayMs + Exp(JitterMeanMs)</c>, plus with
/// <c>TailProbability</c> an extra <c>U(TailMinMs, TailMaxMs)</c>, and is lost with <c>LossProbability</c>.
/// </summary>
public sealed record SimLinkProfile(double BaseDelayMs = 0.5, double JitterMeanMs = 1.5, double TailProbability = 0.02,
    double TailMinMs = 20, double TailMaxMs = 100, double LossProbability = 0.005);

/// <summary>
/// Simulated lossy, jittery datagram network on a <see cref="SimScheduler"/> timeline. Every channel is registered at
/// its bind endpoint. <see cref="IDatagramChannel.Send"/> copies the bytes and schedules delivery after a sampled
/// delay (so reordering emerges naturally); datagrams are dropped on random loss, when sent during an outage, or
/// when nothing is registered and started at the destination when they arrive. Deterministic for a given seed.
/// </summary>
public sealed class SimNetwork : IDatagramChannelFactory
{
    private const double TicksPerMs = 10_000.0;
    private const int FirstEphemeralPort = 49152;

    private readonly SimScheduler _sim;
    private readonly SimLinkProfile _profile;
    private readonly Random _rng;
    private readonly Dictionary<IPEndPoint, Channel> _channels = new();
    private readonly List<(long From, long To)> _outages = new();
    private int _nextEphemeralPort = FirstEphemeralPort;

    public SimNetwork(SimScheduler s, int seed, SimLinkProfile profile)
    {
        _sim = s ?? throw new ArgumentNullException(nameof(s));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _rng = new Random(seed);
    }

    /// <summary>Datagrams sent in true time <c>[fromTrueTicks, toTrueTicks)</c> are dropped.</summary>
    public void SetOutage(long fromTrueTicks, long toTrueTicks)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(toTrueTicks, fromTrueTicks);
        _outages.Add((fromTrueTicks, toTrueTicks));
    }

    /// <summary>Datagrams handed to <see cref="IDatagramChannel.Send"/> on any channel of this network.</summary>
    public long Sent { get; private set; }

    /// <summary>Datagrams lost to random loss or an outage (not counting ones with no receiver).</summary>
    public long Dropped { get; private set; }

    /// <summary>Creates a channel bound at <paramref name="bind"/>; port 0 picks a free ephemeral port.</summary>
    public IDatagramChannel Create(IPEndPoint bind, bool broadcast = false)
    {
        ArgumentNullException.ThrowIfNull(bind);
        var endPoint = bind.Port != 0 ? new IPEndPoint(bind.Address, bind.Port) : NextEphemeral(bind.Address);
        if (_channels.ContainsKey(endPoint))
            throw new InvalidOperationException($"{endPoint} is already bound.");
        var channel = new Channel(this, endPoint);
        _channels.Add(endPoint, channel);
        return channel;
    }

    private IPEndPoint NextEphemeral(IPAddress address)
    {
        while (_channels.ContainsKey(new IPEndPoint(address, _nextEphemeralPort))) _nextEphemeralPort++;
        return new IPEndPoint(address, _nextEphemeralPort++);
    }

    private void Send(Channel from, ReadOnlySpan<byte> datagram, IPEndPoint to)
    {
        Sent++;
        long now = _sim.Now;
        if (_rng.NextDouble() < _profile.LossProbability || InOutage(now))
        {
            Dropped++;
            return;
        }

        double ms = _profile.BaseDelayMs - _profile.JitterMeanMs * Math.Log(1 - _rng.NextDouble());
        if (_rng.NextDouble() < _profile.TailProbability)
            ms += _profile.TailMinMs + _rng.NextDouble() * (_profile.TailMaxMs - _profile.TailMinMs);

        byte[] copy = datagram.ToArray();
        var source = from.LocalEndPoint;
        var destination = new IPEndPoint(to.Address, to.Port);
        _sim.Schedule(now + (long)Math.Round(ms * TicksPerMs), () =>
        {
            if (_channels.TryGetValue(destination, out var target))
                target.Deliver(copy, source);
        });
    }

    private bool InOutage(long trueTicks)
    {
        foreach (var (start, end) in _outages)
            if (trueTicks >= start && trueTicks < end) return true;
        return false;
    }

    private void Unregister(Channel channel)
    {
        if (_channels.TryGetValue(channel.LocalEndPoint, out var registered) && ReferenceEquals(registered, channel))
            _channels.Remove(channel.LocalEndPoint);
    }

    private sealed class Channel : IDatagramChannel
    {
        private readonly SimNetwork _network;
        private DatagramHandler? _handler;
        private bool _disposed;

        public Channel(SimNetwork network, IPEndPoint localEndPoint)
        {
            _network = network;
            LocalEndPoint = localEndPoint;
        }

        public IPEndPoint LocalEndPoint { get; }

        public void Send(ReadOnlySpan<byte> datagram, IPEndPoint to)
        {
            ArgumentNullException.ThrowIfNull(to);
            if (_disposed) return;   // IDatagramChannel: Send never throws for network-level failures, including after Dispose
            _network.Send(this, datagram, to);
        }

        public void Start(DatagramHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_handler is not null) throw new InvalidOperationException("Channel already started.");
            _handler = handler;
        }

        public void Deliver(byte[] datagram, IPEndPoint from)
        {
            if (!_disposed) _handler?.Invoke(datagram, from);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _handler = null;
            _network.Unregister(this);
        }
    }
}
