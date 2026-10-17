using System.Net;
using RhythmSync.Core.Net;
using RhythmSync.Core.Protocol;

namespace RhythmSync.Core.Host;

/// <summary>
/// Fans every finished frame out to the connected clients' audio endpoints and then to an optional local sink (the
/// host's own speaker pipeline). Each frame is serialized once into a preallocated 982-byte packet; <see cref="OnFrame"/>
/// does not allocate. Client membership may change from any thread: the client list is an immutable array swapped
/// under a lock, and <see cref="OnFrame"/> reads the current array without locking.
/// </summary>
public sealed class Streamer : IFrameSink
{
    private readonly record struct Client(Guid DeviceId, IPEndPoint AudioEndPoint);

    private readonly IDatagramChannel _audio;
    private readonly uint _sessionId;
    private readonly byte[] _packet = new byte[PacketSerializer.AudioPacketBytes];
    private readonly object _gate = new();
    private volatile Client[] _clients = Array.Empty<Client>();
    private volatile IFrameSink? _localSink;

    public Streamer(IDatagramChannel audio, uint sessionId)
    {
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _sessionId = sessionId;
    }

    /// <summary>Receives every frame after it has been sent to the clients (e.g. the host's local playback).</summary>
    public IFrameSink? LocalSink
    {
        get => _localSink;
        set => _localSink = value;
    }

    /// <summary>Starts sending to <paramref name="audioEndPoint"/>; a device that is already present gets its endpoint replaced.</summary>
    public void AddClient(Guid deviceId, IPEndPoint audioEndPoint)
    {
        ArgumentNullException.ThrowIfNull(audioEndPoint);
        lock (_gate)
        {
            var current = _clients;
            int index = IndexOf(current, deviceId);
            Client[] next;
            if (index >= 0)
            {
                next = (Client[])current.Clone();
            }
            else
            {
                next = new Client[current.Length + 1];
                current.CopyTo(next, 0);
                index = current.Length;
            }
            next[index] = new Client(deviceId, audioEndPoint);
            _clients = next;
        }
    }

    /// <summary>Stops sending to <paramref name="deviceId"/>; unknown ids are ignored.</summary>
    public void RemoveClient(Guid deviceId)
    {
        lock (_gate)
        {
            var current = _clients;
            int index = IndexOf(current, deviceId);
            if (index < 0) return;
            var next = new Client[current.Length - 1];
            Array.Copy(current, 0, next, 0, index);
            Array.Copy(current, index + 1, next, index, current.Length - index - 1);
            _clients = next;
        }
    }

    /// <summary>Called by <see cref="FrameAssembler"/> on the capture thread (one caller at a time).</summary>
    public void OnFrame(uint seq, long playAt, ReadOnlySpan<short> pcm)
    {
        int length = PacketSerializer.WriteAudio(_packet, _sessionId, seq, playAt, pcm);
        ReadOnlySpan<byte> datagram = _packet.AsSpan(0, length);
        var clients = _clients;
        for (int i = 0; i < clients.Length; i++)
            _audio.Send(datagram, clients[i].AudioEndPoint);
        _localSink?.OnFrame(seq, playAt, pcm);
    }

    private static int IndexOf(Client[] clients, Guid deviceId)
    {
        for (int i = 0; i < clients.Length; i++)
            if (clients[i].DeviceId == deviceId) return i;
        return -1;
    }
}
