using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using RhythmSync.Core.Audio.Eq;
using RhythmSync.Core.Clock;
using RhythmSync.Core.Protocol;

namespace RhythmSync.Core.Host;

/// <param name="PairingCode">The 4-digit join code; null generates a random one.</param>
public sealed record HostSessionOptions(string Name, int ControlPort = 47801, int AudioPort = 47802, int ClockPort = 47803,
    int TargetLatencyMs = 200, int MaxClients = 8, string? PairingCode = null);

public sealed record ConnectedClient(Guid DeviceId, string Name, IPEndPoint AudioEndPoint, bool Ready, HeartbeatStats? LastStats,
    float Volume, double OffsetMs, EqGains Eq);

public interface IHostControl
{
    Task SetLatencyAsync(int ms);

    Task SetDeviceSettingsAsync(Guid deviceId, float volume, double offsetMs, EqGains eq);
}

/// <summary>
/// The host's control plane: a TCP server that admits clients (pairing code, protocol version, capacity, lockouts),
/// adds <c>Ready</c> clients to the audio <see cref="Streamer"/>, exchanges heartbeats, drops silent clients and pushes
/// per-device settings and latency changes. Time comes from the injected <see cref="ISessionClock"/>; <see cref="Tick"/>
/// must be called regularly (about every 250 ms is plenty) by the owner.
/// </summary>
/// <remarks>
/// Every connection runs in its own task which handles all of its own failures: a malformed, truncated, oversize or
/// hostile peer closes only its own connection, never the listener or the session. Messages from a client are never
/// trusted: a <see cref="Hello"/> is validated member by member because the codec does not enforce required members.
/// Guessing the 4-digit code is limited twice: 3 bad codes lock that device id out for 30 s, and more than 10 bad codes
/// from any devices within a sliding 30 s window lock every Hello out until 30 s after the 11th (so rotating device
/// ids does not help). A malformed Hello counts as a bad code.
/// </remarks>
public sealed class HostSession : IHostControl, IAsyncDisposable
{
    public const int ProtocolVersion = 2;

    public const int MinLatencyMs = 100;
    public const int MaxLatencyMs = 500;
    public const double MaxOffsetMs = 100;

    private const int MaxNameLength = 64;
    private const int MaxCodeChars = 256;
    private const int BadCodeLimit = 3;
    private const int GlobalBadCodeLimit = 10;
    private const long GlobalWindowTicks = 30 * AudioFormat.TicksPerSecond;
    private const int MaxTrackedDevices = 1024;
    private const int MaxPendingConnections = 32;
    private const long LockoutTicks = 30 * AudioFormat.TicksPerSecond;
    private const long HeartbeatIntervalTicks = AudioFormat.TicksPerSecond;
    private const long SilenceLimitTicks = 3 * AudioFormat.TicksPerSecond;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(3);
    private static readonly Heartbeat HostHeartbeat = new((HeartbeatStats?)null);

    private sealed class Connection
    {
        public Connection(TcpClient tcp)
        {
            Tcp = tcp;
            Stream = tcp.GetStream();
            var remote = (IPEndPoint?)tcp.Client.RemoteEndPoint ?? throw new IOException("Peer already disconnected.");
            RemoteAddress = remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address;
        }

        public TcpClient Tcp { get; }
        public NetworkStream Stream { get; }
        public IPAddress RemoteAddress { get; }

        /// <summary>Cancelled on close. Never disposed: it has no timer or link registrations to release.</summary>
        public CancellationTokenSource Cts { get; } = new();

        /// <summary>Serializes writes: a frame must never interleave with another.</summary>
        public SemaphoreSlim WriteLock { get; } = new(1, 1);

        /// <summary>Null until the handshake succeeds. Guarded by the session gate.</summary>
        public ConnectedClient? Info { get; set; }

        public long LastSeenTicks;
        public int PendingEnded;
        private int _closed;

        public Task Task { get; set; } = Task.CompletedTask;

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            try { Cts.Cancel(); } catch (Exception) { /* callbacks must not matter */ }
            try { Tcp.Dispose(); } catch (Exception) { }
        }
    }

    private readonly HostSessionOptions _options;
    private readonly ISessionClock _clock;
    private readonly HostPipeline _pipeline;
    private readonly byte[] _codeBytes;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _latencyGate = new(1, 1);

    private readonly object _gate = new();
    private readonly List<Connection> _joined = new();
    private readonly HashSet<Connection> _all = new();
    private readonly Dictionary<Guid, (int Failures, long LockedUntil)> _badCodes = new();
    private readonly Queue<long> _recentBadCodes = new();   // times of bad codes from any device, within the window
    private long _globalLockedUntil;
    private TcpListener? _listener;
    private Task _acceptTask = Task.CompletedTask;
    private bool _disposed;
    private bool _heartbeatSent;
    private long _lastHeartbeatTicks;

    private int _controlPort;
    private int _targetLatencyMs;
    private int _pending;
    private long _faults;

    public HostSession(HostSessionOptions options, ISessionClock clock, HostPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(options.Name);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxClients, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ControlPort, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.ControlPort, 65535);
        if (options.AudioPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(options), "AudioPort must be 1..65535.");
        if (options.ClockPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(options), "ClockPort must be 1..65535.");
        if (options.PairingCode is { Length: 0 }) throw new ArgumentException("PairingCode must not be empty.", nameof(options));

        _options = options;
        _clock = clock;
        _pipeline = pipeline;
        SessionId = pipeline.SessionId;
        PairingCode = options.PairingCode ?? RandomNumberGenerator.GetInt32(10_000).ToString("D4");
        _codeBytes = Encoding.UTF8.GetBytes(PairingCode);
        _controlPort = options.ControlPort;
        _targetLatencyMs = Math.Clamp(options.TargetLatencyMs, MinLatencyMs, MaxLatencyMs);
        pipeline.Assembler.TargetLatencyMs = _targetLatencyMs;
    }

    public uint SessionId { get; }

    public string PairingCode { get; }

    public int TargetLatencyMs => Volatile.Read(ref _targetLatencyMs);

    /// <summary>The bound TCP port (the option may be 0 for an ephemeral port); valid after <see cref="StartAsync"/>.</summary>
    public int ControlPort => Volatile.Read(ref _controlPort);

    /// <summary>Unexpected (non-protocol, non-network) exceptions swallowed so far while serving connections.</summary>
    public long UnexpectedFaults => Interlocked.Read(ref _faults);

    public IReadOnlyList<ConnectedClient> Clients
    {
        get
        {
            lock (_gate) return _joined.Select(c => c.Info!).ToArray();
        }
    }

    /// <summary>Raised (on an arbitrary thread) when a client joins, becomes Ready or leaves. Read <see cref="Clients"/> afresh.</summary>
    public event Action? ClientsChanged;

    /// <summary>Binds the control port and starts accepting clients. Cancelling <paramref name="ct"/> stops accepting new clients.</summary>
    public Task StartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        TcpListener listener;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_listener is not null) throw new InvalidOperationException("Host session already started.");
            listener = new TcpListener(IPAddress.Any, _options.ControlPort);
            listener.Start();
            _listener = listener;
            Volatile.Write(ref _controlPort, ((IPEndPoint)listener.LocalEndpoint).Port);
            var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            _acceptTask = Task.Run(() => AcceptLoopAsync(listener, linked));
        }
        return Task.CompletedTask;
    }

    public Task SetDeviceSettingsAsync(Guid deviceId, float volume, double offsetMs, EqGains eq)
    {
        ArgumentNullException.ThrowIfNull(eq);
        // The control JSON allows NaN/Infinity literals, so the only place to stop them is before they are sent.
        if (!float.IsFinite(volume)) throw new ArgumentException("Volume must be a finite number.", nameof(volume));
        if (!double.IsFinite(offsetMs)) throw new ArgumentException("Offset must be a finite number.", nameof(offsetMs));
        volume = Math.Clamp(volume, 0f, 1f);
        offsetMs = Math.Clamp(offsetMs, -MaxOffsetMs, MaxOffsetMs);

        Connection? target;
        lock (_gate)
        {
            target = _joined.Find(c => c.Info!.DeviceId == deviceId);
            if (target is null) return Task.CompletedTask;   // the device left in the meantime
            target.Info = target.Info! with { Volume = volume, OffsetMs = offsetMs, Eq = eq };
        }
        return TrySendAsync(target, new SetDeviceSettings(volume, offsetMs, eq.ToArray()));
    }

    public async Task SetLatencyAsync(int ms)
    {
        int latency = Math.Clamp(ms, MinLatencyMs, MaxLatencyMs);
        await _latencyGate.WaitAsync().ConfigureAwait(false);   // one change at a time so every client sees the same order
        try
        {
            _pipeline.Assembler.TargetLatencyMs = latency;
            Volatile.Write(ref _targetLatencyMs, latency);
            Connection[] targets;
            lock (_gate) targets = _joined.ToArray();
            await Task.WhenAll(targets.Select(c => TrySendAsync(c, new SetLatency(latency)))).ConfigureAwait(false);
        }
        finally
        {
            _latencyGate.Release();
        }
    }

    /// <summary>Drops clients that have sent nothing for more than 3 s, and sends every client a Heartbeat once per second.</summary>
    public void Tick()
    {
        long now = _clock.NowTicks;
        List<Connection>? stale = null;
        List<Connection>? heartbeatTargets = null;
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var c in _joined)
            {
                if (now - Volatile.Read(ref c.LastSeenTicks) > SilenceLimitTicks) (stale ??= new()).Add(c);
            }

            if (!_heartbeatSent || now - _lastHeartbeatTicks >= HeartbeatIntervalTicks)
            {
                _heartbeatSent = true;
                _lastHeartbeatTicks = now;
                heartbeatTargets = _joined.Where(c => stale is null || !stale.Contains(c)).ToList();
            }
        }

        if (stale is not null)
            foreach (var c in stale) Drop(c);
        if (heartbeatTargets is not null)
            foreach (var c in heartbeatTargets) _ = SendHeartbeatAsync(c);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _cts.Cancel();
        try { _listener?.Stop(); } catch (SocketException) { }
        await _acceptTask.ConfigureAwait(false);   // never faults: the loop handles everything

        Connection[] all;
        lock (_gate) all = _all.ToArray();
        foreach (var c in all) c.Close();
        await Task.WhenAll(all.Select(c => c.Task)).ConfigureAwait(false);   // per-connection tasks never fault
        _cts.Dispose();
    }

    // ---- accepting -----------------------------------------------------------------------------------------------

    private async Task AcceptLoopAsync(TcpListener listener, CancellationTokenSource linked)
    {
        var ct = linked.Token;
        int consecutiveFaults = 0;
        try
        {
            // Only cancellation or disposal ends the loop; any other failure is counted and the next accept is tried.
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    TcpClient tcp;
                    try
                    {
                        tcp = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (ObjectDisposedException) { return; }

                    consecutiveFaults = 0;
                    try
                    {
                        Admit(tcp);
                    }
                    catch (Exception)
                    {
                        Interlocked.Increment(ref _faults);
                        try { tcp.Dispose(); } catch (Exception) { }
                    }
                }
                catch (Exception e)
                {
                    // A connection that died during accept, or a transient resource error: back off briefly and keep serving.
                    if (e is not SocketException) Interlocked.Increment(ref _faults);
                    consecutiveFaults++;
                    try { await Task.Delay(Math.Min(50 * consecutiveFaults, 1000), ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }
        finally
        {
            try { listener.Stop(); } catch (SocketException) { }
            linked.Dispose();
        }
    }

    private void Admit(TcpClient tcp)
    {
        Connection conn;
        try
        {
            tcp.NoDelay = true;
            conn = new Connection(tcp);
        }
        catch (Exception)
        {
            tcp.Dispose();   // reset between accept and setup
            return;
        }

        // Connections that have not completed the handshake are cheap for an attacker to open: cap them.
        if (Interlocked.Increment(ref _pending) > MaxPendingConnections)
        {
            Interlocked.Decrement(ref _pending);
            conn.PendingEnded = 1;
            conn.Close();
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                EndPending(conn);
                conn.Close();
                return;
            }

            _all.Add(conn);
            conn.Task = Task.Run(() => RunConnectionAsync(conn));
        }
    }

    // ---- per-connection ------------------------------------------------------------------------------------------

    private async Task RunConnectionAsync(Connection c)
    {
        try
        {
            if (await HandshakeAsync(c).ConfigureAwait(false))
                await ReceiveLoopAsync(c).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or InvalidDataException or SocketException
                                      or ObjectDisposedException)
        {
            // Network failure, bad frame or our own close: this connection ends, nothing else is affected.
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _faults);
        }
        finally
        {
            EndPending(c);
            Drop(c);
        }
    }

    /// <returns>True when the client joined; false when it was rejected, invalid, or the session is closing.</returns>
    private async Task<bool> HandshakeAsync(Connection c)
    {
        ControlMessage? first;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(c.Cts.Token))
        {
            timeout.CancelAfter(HandshakeTimeout);
            first = await ControlCodec.ReadAsync(c.Stream, timeout.Token).ConfigureAwait(false);
        }

        if (first is not Hello hello) return false;
        long now = _clock.NowTicks;
        Volatile.Write(ref c.LastSeenTicks, now);

        if (hello.DeviceId == Guid.Empty) return false;   // nothing to attribute a bad attempt to
        if (hello.ProtocolVersion != ProtocolVersion) return await RejectAsync(c, RejectReason.VersionMismatch).ConfigureAwait(false);

        string? name = NormalizeName(hello.DeviceName);
        bool wellFormed = name is not null && hello.PairingCode is not null && hello.AudioPort is >= 1 and <= 65535;
        switch (CheckCode(hello.DeviceId, wellFormed ? hello.PairingCode : null, now))
        {
            case CodeResult.LockedOut: return await RejectAsync(c, RejectReason.LockedOut).ConfigureAwait(false);
            case CodeResult.Bad: return await RejectAsync(c, RejectReason.BadCode).ConfigureAwait(false);
        }

        // Register and send Welcome under the write lock, so a concurrent SetLatency to this client is queued behind
        // Welcome, and Welcome carries whichever latency is current once the client is registered.
        Connection? replaced = null;
        bool full = false;
        bool joined = false;
        await c.WriteLock.WaitAsync(c.Cts.Token).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    var existing = _joined.Find(x => x.Info!.DeviceId == hello.DeviceId);
                    if (existing is null && _joined.Count >= _options.MaxClients)
                    {
                        full = true;
                    }
                    else
                    {
                        if (existing is not null)
                        {
                            _joined.Remove(existing);
                            _pipeline.Streamer.RemoveClient(hello.DeviceId);   // the new connection must send Ready again
                            replaced = existing;
                        }

                        c.Info = new ConnectedClient(hello.DeviceId, name!, new IPEndPoint(c.RemoteAddress, hello.AudioPort),
                                                     Ready: false, LastStats: null, Volume: 1f, OffsetMs: 0, Eq: EqGains.Flat);
                        _joined.Add(c);
                        joined = true;
                    }
                }
            }

            // Close right away: if the Welcome write below throws, the replaced socket must not linger untracked.
            replaced?.Close();

            if (joined)
            {
                await WriteAsync(c, new Welcome(SessionId, AudioFormat.SampleRate, AudioFormat.Channels, AudioFormat.FrameSamples,
                                                TargetLatencyMs, _options.AudioPort, _options.ClockPort)).ConfigureAwait(false);
            }
        }
        finally
        {
            c.WriteLock.Release();
        }

        if (full) return await RejectAsync(c, RejectReason.SessionFull).ConfigureAwait(false);
        if (!joined) return false;

        EndPending(c);
        RaiseClientsChanged();
        return true;
    }

    private async Task ReceiveLoopAsync(Connection c)
    {
        while (true)
        {
            var message = await ControlCodec.ReadAsync(c.Stream, c.Cts.Token).ConfigureAwait(false);
            if (message is null) return;   // clean EOF
            Volatile.Write(ref c.LastSeenTicks, _clock.NowTicks);   // any message is proof of life

            switch (message)
            {
                case Heartbeat hb:
                    lock (_gate)
                    {
                        if (c.Info is { } info && _joined.Contains(c))
                            c.Info = info with { LastStats = hb.Stats ?? info.LastStats };
                    }
                    break;
                case Ready:
                    HandleReady(c);
                    break;
                case Bye:
                    return;
                // Anything else is not meaningful from a client; ignored so newer clients stay compatible.
            }
        }
    }

    private void HandleReady(Connection c)
    {
        bool changed = false;
        lock (_gate)
        {
            // A connection that was replaced (or dropped) must not touch the streamer.
            if (c.Info is { } info && _joined.Contains(c))
            {
                _pipeline.Streamer.AddClient(info.DeviceId, info.AudioEndPoint);
                changed = !info.Ready;
                c.Info = info with { Ready = true };
            }
        }
        if (changed) RaiseClientsChanged();
    }

    /// <summary>Idempotent teardown: unregisters, stops streaming to the device, closes the socket, raises ClientsChanged.</summary>
    private void Drop(Connection c)
    {
        bool wasJoined;
        lock (_gate)
        {
            _all.Remove(c);
            wasJoined = _joined.Remove(c);
            if (wasJoined) _pipeline.Streamer.RemoveClient(c.Info!.DeviceId);
        }
        c.Close();
        if (wasJoined) RaiseClientsChanged();
    }

    private void EndPending(Connection c)
    {
        if (Interlocked.Exchange(ref c.PendingEnded, 1) == 0) Interlocked.Decrement(ref _pending);
    }

    private void RaiseClientsChanged()
    {
        try { ClientsChanged?.Invoke(); }
        catch (Exception) { Interlocked.Increment(ref _faults); }   // a faulty subscriber must not break a connection
    }

    // ---- sending -------------------------------------------------------------------------------------------------

    /// <summary>Writes one frame. The caller holds <see cref="Connection.WriteLock"/>. A stalled peer times out.</summary>
    private static async Task WriteAsync(Connection c, ControlMessage message)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(c.Cts.Token);
        cts.CancelAfter(WriteTimeout);
        await ControlCodec.WriteAsync(c.Stream, message, cts.Token).ConfigureAwait(false);
    }

    /// <summary>Sends to one client; a failed or timed-out write drops that client (a half-written frame cannot be recovered).</summary>
    private async Task TrySendAsync(Connection c, ControlMessage message)
    {
        try
        {
            await c.WriteLock.WaitAsync(c.Cts.Token).ConfigureAwait(false);
            try { await WriteAsync(c, message).ConfigureAwait(false); }
            finally { c.WriteLock.Release(); }
        }
        catch (Exception)
        {
            Drop(c);
        }
    }

    private async Task SendHeartbeatAsync(Connection c)
    {
        // If the previous write is still in flight the peer is slow: skip this heartbeat rather than queue them up.
        if (!c.WriteLock.Wait(0)) return;
        try
        {
            await WriteAsync(c, HostHeartbeat).ConfigureAwait(false);
        }
        catch (Exception)
        {
            Drop(c);
        }
        finally
        {
            c.WriteLock.Release();
        }
    }

    private async Task<bool> RejectAsync(Connection c, RejectReason reason)
    {
        await TrySendAsync(c, new Reject(reason)).ConfigureAwait(false);
        return false;   // the caller ends the connection
    }

    // ---- admission rules -----------------------------------------------------------------------------------------

    private static string? NormalizeName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > MaxNameLength ? trimmed[..MaxNameLength].TrimEnd() : trimmed;
    }

    private enum CodeResult { Ok, Bad, LockedOut }

    /// <summary>
    /// The lockout check, the code comparison and the failure bookkeeping for one Hello, as one atomic step under the
    /// gate: concurrent attempts for a device are serialized, so none can slip past a lockout, add a guess beyond the
    /// limit, or erase a lockout another attempt just set. <paramref name="suppliedCode"/> null means the Hello was
    /// malformed, which counts as a bad code.
    /// </summary>
    private CodeResult CheckCode(Guid deviceId, string? suppliedCode, long now)
    {
        lock (_gate)
        {
            if (now < _globalLockedUntil) return CodeResult.LockedOut;

            if (_badCodes.TryGetValue(deviceId, out var entry) && entry.LockedUntil != 0)
            {
                if (entry.LockedUntil > now) return CodeResult.LockedOut;
                _badCodes.Remove(deviceId);   // lockout served
            }

            if (suppliedCode is not null && suppliedCode.Length <= MaxCodeChars
                && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(suppliedCode), _codeBytes))
            {
                _badCodes.Remove(deviceId);
                return CodeResult.Ok;
            }

            if (!_badCodes.TryGetValue(deviceId, out entry))
            {
                if (_badCodes.Count >= MaxTrackedDevices)
                {
                    // Bound memory against peers inventing device ids: forget served lockouts, and if still full, stop tracking new ids.
                    foreach (var key in _badCodes.Where(kv => kv.Value.LockedUntil != 0 && kv.Value.LockedUntil <= now)
                                                 .Select(kv => kv.Key).ToList())
                        _badCodes.Remove(key);
                    if (_badCodes.Count >= MaxTrackedDevices)
                    {
                        RecordGlobalBadCodeLocked(now);
                        return CodeResult.Bad;
                    }
                }
                entry = (0, 0);
            }

            int failures = entry.Failures + 1;
            _badCodes[deviceId] = failures >= BadCodeLimit ? (0, now + LockoutTicks) : (failures, 0);
            RecordGlobalBadCodeLocked(now);
            return CodeResult.Bad;
        }
    }

    /// <summary>Counts a bad code toward the global limit; the 11th within the window locks every Hello out for 30 s.</summary>
    private void RecordGlobalBadCodeLocked(long now)
    {
        while (_recentBadCodes.TryPeek(out long oldest) && oldest <= now - GlobalWindowTicks) _recentBadCodes.Dequeue();
        _recentBadCodes.Enqueue(now);
        if (_recentBadCodes.Count <= GlobalBadCodeLimit) return;
        _globalLockedUntil = now + LockoutTicks;
        _recentBadCodes.Clear();   // all older than the lockout's end by the time it ends
    }
}
