using System.Net;
using RhythmSync.Core.Clock;
using RhythmSync.Core.Protocol;

namespace RhythmSync.Core.Net;

/// <summary>A host heard on the LAN; <see cref="ControlEndPoint"/> is the beacon sender's address plus the beacon's control port.</summary>
public sealed record DiscoveredHost(uint SessionId, string Name, IPEndPoint ControlEndPoint, long LastSeenTicks);

/// <summary>
/// Client side of LAN discovery. Hosts are keyed by (sender IP, session id) and stay listed while their last beacon is
/// at most <see cref="ExpiryTicks"/> old. <see cref="Hosts"/> never returns an expired host. <see cref="Changed"/>
/// is raised (outside the lock, on the channel's receive thread or the caller of <see cref="Tick"/>) when a host
/// appears, disappears, or its name or control endpoint changes, but not when a beacon merely refreshes a host.
/// Disappearance is detected when a datagram arrives or when <see cref="Tick"/> is called, so the owner should call
/// <see cref="Tick"/> periodically (e.g. once a second) to get <see cref="Changed"/> for hosts that went silent.
/// Thread-safe. Owns the channel and disposes it.
/// </summary>
public sealed class DiscoveryListener : IDisposable
{
    public const long ExpiryTicks = 3 * AudioFormat.TicksPerSecond;

    private readonly record struct Key(IPAddress Address, uint SessionId);

    private readonly IDatagramChannel _channel;
    private readonly ISessionClock _clock;
    private readonly object _gate = new();
    private readonly Dictionary<Key, DiscoveredHost> _hosts = new();

    public DiscoveryListener(IDatagramChannel ch, ISessionClock clock)
    {
        _channel = ch ?? throw new ArgumentNullException(nameof(ch));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Raised when the set of hosts or a host's name or control endpoint changes.</summary>
    public event Action? Changed;

    /// <summary>Hosts heard within the last 3 s, ordered by name then session id.</summary>
    public IReadOnlyList<DiscoveredHost> Hosts
    {
        get
        {
            long now = _clock.NowTicks;
            lock (_gate)
            {
                return _hosts.Values
                    .Where(h => !IsExpired(h, now))
                    .OrderBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(h => h.SessionId)
                    .ToArray();
            }
        }
    }

    public void Start() => _channel.Start(OnDatagram);

    /// <summary>Drops hosts that have gone silent and raises <see cref="Changed"/> if any were dropped.</summary>
    public void Tick()
    {
        if (RemoveExpired(_clock.NowTicks)) Changed?.Invoke();
    }

    private void OnDatagram(ReadOnlySpan<byte> data, IPEndPoint from)
    {
        if (!PacketSerializer.TryReadBeacon(data, out var beacon) || beacon is null) return;

        long now = _clock.NowTicks;
        var host = new DiscoveredHost(beacon.SessionId, beacon.Name, new IPEndPoint(from.Address, beacon.ControlPort), now);
        bool changed = RemoveExpired(now);
        lock (_gate)
        {
            var key = new Key(from.Address, beacon.SessionId);
            changed |= !_hosts.TryGetValue(key, out var known)
                       || known.Name != host.Name
                       || !known.ControlEndPoint.Equals(host.ControlEndPoint);
            _hosts[key] = host;
        }
        if (changed) Changed?.Invoke();
    }

    private bool RemoveExpired(long now)
    {
        lock (_gate)
        {
            bool removed = false;
            foreach (var (key, host) in _hosts.ToArray())
            {
                if (!IsExpired(host, now)) continue;
                _hosts.Remove(key);
                removed = true;
            }
            return removed;
        }
    }

    private static bool IsExpired(DiscoveredHost host, long now) => now - host.LastSeenTicks > ExpiryTicks;

    public void Dispose() => _channel.Dispose();
}
