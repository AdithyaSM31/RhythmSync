using System.Net;

namespace RhythmSync.Core.Net;

/// <summary>
/// Receives one datagram. <paramref name="data"/> is exactly the received datagram (never a larger buffer) and is only
/// valid for the duration of the call.
/// </summary>
public delegate void DatagramHandler(ReadOnlySpan<byte> data, IPEndPoint from);

/// <summary>
/// A bound UDP-like endpoint. Disposing it stops delivery to the handler. Two guarantees callers rely on: the handler
/// is called serially (never concurrently with itself, always from the same receive thread), so handlers may reuse
/// scratch buffers; and <see cref="Send"/> does not throw for network errors (unreachable or down destination,
/// transient socket failure, send after <see cref="IDisposable.Dispose"/>): the datagram is simply lost, as on any UDP path.
/// </summary>
public interface IDatagramChannel : IDisposable
{
    IPEndPoint LocalEndPoint { get; }

    void Send(ReadOnlySpan<byte> datagram, IPEndPoint to);

    /// <summary>Starts delivering received datagrams to <paramref name="handler"/> (at most one handler per channel).</summary>
    void Start(DatagramHandler handler);
}

public interface IDatagramChannelFactory
{
    IDatagramChannel Create(IPEndPoint bind, bool broadcast = false);
}
