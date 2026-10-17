using System.Net;

namespace RhythmSync.Core.Net;

/// <summary>
/// Receives one datagram. <paramref name="data"/> is exactly the received datagram (never a larger buffer) and is only
/// valid for the duration of the call.
/// </summary>
public delegate void DatagramHandler(ReadOnlySpan<byte> data, IPEndPoint from);

/// <summary>A bound UDP-like endpoint. Disposing it stops delivery to the handler.</summary>
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
