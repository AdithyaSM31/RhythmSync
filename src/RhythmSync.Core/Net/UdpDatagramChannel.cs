using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace RhythmSync.Core.Net;

/// <summary>
/// <see cref="IDatagramChannel"/> over a real UDP socket. One dedicated receive thread reads into a 2048-byte buffer and
/// calls the handler serially with a span of exactly the received length. <see cref="Send"/> does not allocate once a
/// destination has been sent to (its socket address is cached, up to 32 destinations) and never throws for socket
/// errors (they are counted in <see cref="SendErrors"/>); socket errors on receive are counted in
/// <see cref="ReceiveErrors"/> and the loop continues. On Windows <c>SIO_UDP_CONNRESET</c> is disabled so an ICMP
/// port-unreachable caused by a send does not surface as an error on the receive path. Exceptions thrown by the
/// handler itself are not caught. Disposing closes the socket and joins the receive thread (skipped when called from the
/// handler itself); disposing twice is fine.
/// </summary>
public sealed class UdpDatagramChannel : IDatagramChannel
{
    private const int ReceiveBufferBytes = 2048;
    private const int SioUdpConnReset = -1744830452;   // 0x9800000C
    private const int JoinTimeoutMs = 2000;
    private const int ErrorBackoffMs = 1;               // keeps a persistently failing socket from spinning a core

    /// <summary>Destinations whose <see cref="SocketAddress"/> is cached; further ones are sent uncached (allocating).</summary>
    private const int MaxCachedDestinations = 32;

    private readonly Socket _socket;
    private readonly ConcurrentDictionary<IPEndPoint, SocketAddress> _destinations = new();
    private readonly Thread _thread;
    private readonly object _startGate = new();
    private DatagramHandler? _handler;
    private bool _started;
    private int _disposed;
    private long _sendErrors;
    private long _receiveErrors;

    public UdpDatagramChannel(IPEndPoint bind, bool broadcast = false)
    {
        ArgumentNullException.ThrowIfNull(bind);

        _socket = new Socket(bind.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            if (broadcast) _socket.EnableBroadcast = true;
            if (OperatingSystem.IsWindows())
                _socket.IOControl(SioUdpConnReset, new byte[] { 0, 0, 0, 0 }, null);
            _socket.Bind(bind);
            LocalEndPoint = (IPEndPoint)_socket.LocalEndPoint!;
        }
        catch
        {
            _socket.Dispose();
            throw;
        }

        _thread = new Thread(ReceiveLoop) { IsBackground = true, Name = $"udp-rx {LocalEndPoint}" };
    }

    public IPEndPoint LocalEndPoint { get; }

    /// <summary>Datagrams whose send failed with a socket error (or was attempted after dispose).</summary>
    public long SendErrors => Interlocked.Read(ref _sendErrors);

    /// <summary>Socket errors seen on the receive path while the channel was open.</summary>
    public long ReceiveErrors => Interlocked.Read(ref _receiveErrors);

    public void Send(ReadOnlySpan<byte> datagram, IPEndPoint to)
    {
        ArgumentNullException.ThrowIfNull(to);
        if (Volatile.Read(ref _disposed) != 0)
        {
            Interlocked.Increment(ref _sendErrors);
            return;
        }

        try
        {
            _socket.SendTo(datagram, SocketFlags.None, AddressOf(to));
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            Interlocked.Increment(ref _sendErrors);
        }
    }

    /// <summary>
    /// The serialized address for <paramref name="to"/>, built on the first send to each destination so that later sends
    /// (the audio path: one per client per frame) do not allocate. A <see cref="SocketAddress"/> is read-only while sending
    /// and safe to share between threads. The key is a private copy because <see cref="IPEndPoint"/> is mutable.
    /// </summary>
    private SocketAddress AddressOf(IPEndPoint to)
    {
        if (_destinations.TryGetValue(to, out var cached)) return cached;
        var address = to.Serialize();
        if (_destinations.Count < MaxCachedDestinations)
            _destinations.TryAdd(new IPEndPoint(to.Address, to.Port), address);
        return address;
    }

    public void Start(DatagramHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_startGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_started) throw new InvalidOperationException("Channel already started.");
            _started = true;
            _handler = handler;
            _thread.Start();
        }
    }

    private void ReceiveLoop()
    {
        var handler = _handler!;
        var buffer = new byte[ReceiveBufferBytes];
        var address = new SocketAddress(LocalEndPoint.AddressFamily);
        var template = LocalEndPoint.AddressFamily == AddressFamily.InterNetwork
            ? new IPEndPoint(IPAddress.Any, 0)
            : new IPEndPoint(IPAddress.IPv6Any, 0);

        // Senders rarely change; reuse the last endpoint instance so the steady state allocates nothing.
        SocketAddress? lastAddress = null;
        IPEndPoint? lastEndPoint = null;

        while (Volatile.Read(ref _disposed) == 0)
        {
            int length;
            try
            {
                length = _socket.ReceiveFrom(buffer, SocketFlags.None, address);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                Interlocked.Increment(ref _receiveErrors);
                Thread.Sleep(ErrorBackoffMs);
                continue;
            }

            if (lastAddress is null || !address.Buffer.Span.SequenceEqual(lastAddress.Buffer.Span))
            {
                lastAddress = new SocketAddress(address.Family, address.Size);
                address.Buffer.CopyTo(lastAddress.Buffer);
                lastEndPoint = (IPEndPoint)template.Create(lastAddress);
            }
            handler(buffer.AsSpan(0, length), lastEndPoint!);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _socket.Dispose();   // unblocks a receive in progress
        bool started;
        lock (_startGate) started = _started;
        if (started && Thread.CurrentThread != _thread)
            _thread.Join(JoinTimeoutMs);
    }
}

public sealed class UdpDatagramChannelFactory : IDatagramChannelFactory
{
    public IDatagramChannel Create(IPEndPoint bind, bool broadcast = false) => new UdpDatagramChannel(bind, broadcast);
}
