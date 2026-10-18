using System.Net;
using System.Net.Sockets;
using RhythmSync.Core.Audio;
using RhythmSync.Core.Clock;
using RhythmSync.Core.Host;
using RhythmSync.Core.Net;
using RhythmSync.Core.Protocol;

namespace RhythmSync.Core.Client;

/// <param name="AudioPort">UDP port for the audio stream; 0 binds an ephemeral port (its actual number goes in Hello).</param>
public sealed record ClientSessionOptions(Guid DeviceId, string DeviceName, int AudioPort = 47802);

public enum JoinOutcome { Joined, Rejected, Unreachable }

public sealed record JoinResult(JoinOutcome Outcome, RejectReason? Reason = null);

/// <summary>
/// The client's whole lifecycle with one host: joins over TCP with a pairing code, syncs clocks, starts audio only
/// once the clock has converged, sends heartbeats with stats, applies host settings, rebuilds the output after a device
/// change, and reconnects after losing the host.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Tick"/> (call it about every 10 ms) drives everything: the clock-sync ping, starting playback once the
/// clock estimator has converged (create the output, build a <see cref="ClientPipeline"/> at its rate, start it, send
/// <see cref="Ready"/>), the output rebuild after <see cref="IAudioOutput.DeviceInvalidated"/>, a heartbeat every
/// second, host liveness, reconnect scheduling and the state. There are no internal timers; the TCP reader runs as its
/// own task and every reconnect attempt runs in the background, one at a time.
/// </para>
/// <para>
/// Every Welcome starts a new session: a fresh clock estimator, clock-sync client, pipeline and output (a scheduler or
/// jitter buffer from an old session cannot be reused). The audio UDP channel is bound once, on the first join, and
/// kept for the lifetime of this object; datagrams of other sessions are dropped by the pipeline.
/// </para>
/// <para>
/// Heartbeats always carry stats. Until the pipeline exists, the audio fields (<c>LossPct</c>, <c>LatePct</c>,
/// <c>SyncErrorMs</c>) are NaN; <c>SyncErrorMs</c> is also NaN whenever the scheduler is not playing (its value goes
/// stale while silent). <c>DriftPpm</c>, <c>FitResidualMs</c> and <c>State</c> are always filled.
/// </para>
/// <para>
/// Liveness: any message from the host counts. After more than 3 s without one (on the injected clock) the connection,
/// output, pipeline and clock client are torn down, <see cref="State"/> becomes <see cref="SyncState.Connecting"/>, and
/// a join with the remembered endpoint and code is tried at once and then every 2 s. 60 s after the loss without
/// success, or on a <see cref="Reject"/>, the state becomes <see cref="SyncState.Disconnected"/> and retrying stops.
/// </para>
/// <para>
/// Thread safety: all members may be called from any thread. <see cref="StateChanged"/> may be raised on any thread
/// (Tick's caller, the TCP reader, a reconnect attempt); read <see cref="State"/> afresh in the handler.
/// </para>
/// </remarks>
public sealed class ClientSession : IAsyncDisposable
{
    private const int ProtocolVersion = HostSession.ProtocolVersion;
    private const double MaxOffsetMs = HostSession.MaxOffsetMs;
    private const int MinLatencyMs = HostSession.MinLatencyMs, MaxLatencyMs = HostSession.MaxLatencyMs;
    private const long Second = AudioFormat.TicksPerSecond;
    private const long HeartbeatIntervalTicks = 1 * Second;
    private const long SilenceLimitTicks = 3 * Second;
    private const long RetryIntervalTicks = 2 * Second;
    private const long GiveUpTicks = 60 * Second;
    private const long OutputRetryTicks = 1 * Second;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(3);

    /// <summary>One TCP connection to a host and the session its Welcome started. Mutable members are guarded by the gate.</summary>
    private sealed class Connection
    {
        public Connection(TcpClient tcp, Welcome welcome)
        {
            Tcp = tcp;
            Stream = tcp.GetStream();
            Welcome = welcome;
        }

        public TcpClient Tcp { get; }
        public NetworkStream Stream { get; }
        public Welcome Welcome { get; }
        public CancellationTokenSource Cts { get; } = new();   // cancelled on close; never disposed (no timers or links)
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
        public Task Reader { get; set; } = Task.CompletedTask;

        public long LastHeardTicks;

        // Session (built on install, torn down with the connection).
        public ClockEstimator Estimator = null!;
        public ClockSyncClient ClockClient = null!;
        public int LatencyMs;
        public ClientPipeline? Pipeline;
        public IAudioOutput? Output;
        public Action? OutputInvalidatedHandler;
        public IAudioOutput? InvalidatedOutput;   // written from the audio thread
        public long NextOutputAttemptTicks;
        public long ConcealedBase;                // concealed frames of schedulers replaced by output rebuilds
        public StatsWindow Window = new();
        public bool HeartbeatSent;
        public long LastHeartbeatTicks;

        private int _closed;

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            try { Cts.Cancel(); } catch (Exception) { }
            try { Tcp.Dispose(); } catch (Exception) { }
        }
    }

    private readonly ClientSessionOptions _options;
    private readonly ISessionClock _clock;
    private readonly IDatagramChannelFactory _channels;
    private readonly IAudioOutputFactory _outputs;
    private readonly CancellationTokenSource _cts = new();

    private readonly object _gate = new();
    private IDatagramChannel? _audioChannel;
    private ClientPipeline? _audioTarget;   // where audio datagrams go; read on the audio receive thread
    private Connection? _conn;
    private IPEndPoint? _endpoint;
    private string? _code;
    private int _generation;                // bumped by Join/Leave/give-up/dispose: older attempts are discarded
    private bool _reconnecting;
    private long _lostTicks;
    private long? _lastAttemptTicks;
    private bool _attemptRunning;
    private Task _attemptTask = Task.CompletedTask;
    private bool _disposed;

    private float _volume = 1f;
    private double _requestedOffsetMs;
    private int _state = (int)SyncState.Disconnected;
    private long _ignoredSettings;
    private long _outputFailures;
    private long _faults;

    public ClientSession(ClientSessionOptions o, ISessionClock clock, IDatagramChannelFactory channels, IAudioOutputFactory outputs)
    {
        ArgumentNullException.ThrowIfNull(o);
        ArgumentNullException.ThrowIfNull(o.DeviceName);
        ArgumentOutOfRangeException.ThrowIfLessThan(o.AudioPort, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(o.AudioPort, 65535);
        _options = o;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _channels = channels ?? throw new ArgumentNullException(nameof(channels));
        _outputs = outputs ?? throw new ArgumentNullException(nameof(outputs));
    }

    public SyncState State => (SyncState)Volatile.Read(ref _state);

    /// <summary>Raised on any thread when <see cref="State"/> changes.</summary>
    public event Action? StateChanged;

    /// <summary>Linear volume 0..1 (clamped). Non-finite values throw. The host's SetDeviceSettings overwrites it.</summary>
    public float Volume
    {
        get { lock (_gate) return _volume; }
        set
        {
            if (!float.IsFinite(value)) throw new ArgumentException("Volume must be a finite number.", nameof(value));
            lock (_gate) ApplyVolumeLocked(value);
        }
    }

    /// <summary>
    /// Requested per-device offset in ms, ±100 (clamped; positive plays later). Non-finite values throw. The scheduler
    /// further limits it by the current latency; the requested value is kept and re-applied when the latency changes.
    /// </summary>
    public double OffsetMs
    {
        get { lock (_gate) return _requestedOffsetMs; }
        set
        {
            if (!double.IsFinite(value)) throw new ArgumentException("Offset must be a finite number.", nameof(value));
            lock (_gate) ApplyOffsetLocked(value);
        }
    }

    /// <summary>Playback statistics of the current pipeline; null until playback has started.</summary>
    public PlaybackStats? Stats
    {
        get { lock (_gate) return _conn?.Pipeline?.Scheduler.GetStats(); }
    }

    /// <summary>Host SetDeviceSettings messages ignored because they carried a non-finite volume or offset.</summary>
    public long IgnoredSettingsMessages => Interlocked.Read(ref _ignoredSettings);

    /// <summary>Times creating or starting an output device failed (retried about once a second).</summary>
    public long OutputFailures => Interlocked.Read(ref _outputFailures);

    /// <summary>Unexpected exceptions swallowed on background paths (reader, reconnect attempts, event handlers).</summary>
    public long UnexpectedFaults => Interlocked.Read(ref _faults);

    // ---- joining ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Joins the host at <paramref name="control"/>, replacing any current connection, and remembers the endpoint and
    /// code for reconnects. Binds the audio channel first (its port goes in Hello). Joined → <see cref="SyncState.Syncing"/>;
    /// otherwise <see cref="SyncState.Disconnected"/>. Throws <see cref="OperationCanceledException"/> if
    /// <paramref name="ct"/> is cancelled or another join, leave or dispose supersedes this one.
    /// </summary>
    public async Task<JoinResult> JoinAsync(IPEndPoint control, string pairingCode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(pairingCode);

        int generation, audioPort;
        bool changed;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureAudioChannelLocked();
            audioPort = _audioChannel!.LocalEndPoint.Port;
            generation = ++_generation;
            _endpoint = control;
            _code = pairingCode;
            _reconnecting = false;
            CloseConnectionLocked();
            changed = SetStateLocked(SyncState.Connecting);
        }
        if (changed) RaiseStateChanged();

        (JoinResult Result, Connection? Conn) attempt;
        try
        {
            attempt = await ConnectAsync(control, pairingCode, audioPort, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            EndJoinAttempt(generation);
            throw;
        }

        lock (_gate)
        {
            if (generation != _generation || _disposed)
            {
                attempt.Conn?.Close();
                throw new OperationCanceledException("The join was superseded.");
            }
            if (attempt.Conn is not null) InstallLocked(attempt.Conn);
            else changed = SetStateLocked(SyncState.Disconnected);
        }
        if (attempt.Conn is not null || changed) RaiseStateChanged();
        return attempt.Result;
    }

    private void EndJoinAttempt(int generation)
    {
        bool changed = false;
        lock (_gate)
        {
            if (generation == _generation && !_disposed) changed = SetStateLocked(SyncState.Disconnected);
        }
        if (changed) RaiseStateChanged();
    }

    /// <summary>Sends Bye, closes the connection and stops reconnecting; <see cref="State"/> becomes Disconnected.</summary>
    public async Task LeaveAsync()
    {
        Connection? conn;
        bool changed;
        lock (_gate)
        {
            if (_disposed) return;
            _generation++;
            _reconnecting = false;
            conn = _conn;
            _conn = null;
            if (conn is not null) TeardownLocked(conn, closeTcp: false);
            changed = SetStateLocked(SyncState.Disconnected);
        }
        if (changed) RaiseStateChanged();

        if (conn is not null)
        {
            await SendAsync(conn, new Bye()).ConfigureAwait(false);
            conn.Close();
        }
    }

    // ---- ticking ---------------------------------------------------------------------------------------------------

    public void Tick()
    {
        long now = _clock.NowTicks;
        Connection? readyTo = null, heartbeatTo = null;
        HeartbeatStats? stats = null;
        bool changed = false;

        lock (_gate)
        {
            if (_disposed) return;

            var c = _conn;
            if (c is not null)
            {
                if (now - Volatile.Read(ref c.LastHeardTicks) > SilenceLimitTicks)
                {
                    changed |= LoseConnectionLocked(now);
                }
                else
                {
                    c.ClockClient.Tick();

                    if (c.Pipeline is null)
                    {
                        if (c.Estimator.EverConverged && now >= c.NextOutputAttemptTicks && StartPlaybackLocked(c, now))
                            readyTo = c;
                    }
                    else if ((c.Output is null || Volatile.Read(ref c.InvalidatedOutput) == c.Output) && now >= c.NextOutputAttemptTicks)
                    {
                        RebuildOutputLocked(c, now);
                    }

                    // No running output (not started yet, failed to start, or being rebuilt) means still syncing, even if
                    // the estimator converged: nothing is being played in sync.
                    var state = c.Pipeline is null || c.Output is null ? SyncState.Syncing : SyncStateEvaluator.Evaluate(c.Estimator, now);
                    changed |= SetStateLocked(state);

                    if (!c.HeartbeatSent || now - c.LastHeartbeatTicks >= HeartbeatIntervalTicks)
                    {
                        c.HeartbeatSent = true;
                        c.LastHeartbeatTicks = now;
                        stats = BuildStatsLocked(c, now, state);
                        heartbeatTo = c;
                    }
                }
            }

            if (_reconnecting)
            {
                if (now - _lostTicks > GiveUpTicks)
                {
                    _reconnecting = false;
                    _generation++;   // a still-running attempt is discarded
                    changed |= SetStateLocked(SyncState.Disconnected);
                }
                else if (!_attemptRunning && (_lastAttemptTicks is not long last || now - last >= RetryIntervalTicks))
                {
                    _lastAttemptTicks = now;
                    _attemptRunning = true;
                    int generation = _generation;
                    var endpoint = _endpoint!;
                    var code = _code!;
                    int audioPort = _audioChannel!.LocalEndPoint.Port;
                    _attemptTask = Task.Run(() => ReconnectAttemptAsync(generation, endpoint, code, audioPort));
                }
            }
        }

        if (readyTo is not null) _ = SendAsync(readyTo, new Ready());
        if (heartbeatTo is not null) _ = SendHeartbeatAsync(heartbeatTo, new Heartbeat(stats));
        if (changed) RaiseStateChanged();
    }

    /// <summary>Creates the output and the pipeline at its rate, applies the settings and starts playing. False if the output could not be created.</summary>
    private bool StartPlaybackLocked(Connection c, long now)
    {
        IAudioOutput output;
        try
        {
            output = _outputs.Create();
        }
        catch (Exception)
        {
            OutputFailedLocked(c, now);
            return false;
        }

        ClientPipeline pipeline;
        try
        {
            pipeline = new ClientPipeline(c.Welcome.SessionId, c.Estimator, output.SampleRate);
        }
        catch (Exception)
        {
            DisposeQuietly(output);
            OutputFailedLocked(c, now);
            return false;
        }

        pipeline.Gain.Gain = _volume;
        pipeline.Scheduler.SetOffset(_requestedOffsetMs, c.LatencyMs);
        c.Pipeline = pipeline;
        c.Window = new StatsWindow();
        Volatile.Write(ref _audioTarget, pipeline);
        StartOutputLocked(c, output, now);
        return true;   // Ready even if the output failed to start: audio is buffered (the state stays Syncing) and the output is retried
    }

    /// <summary>Replaces an invalidated (or missing) output: the old one is stopped and disposed first.</summary>
    private void RebuildOutputLocked(Connection c, long now)
    {
        StopOutputLocked(c);
        Volatile.Write(ref c.InvalidatedOutput, null);

        IAudioOutput next;
        try
        {
            next = _outputs.Create();
        }
        catch (Exception)
        {
            OutputFailedLocked(c, now);
            return;
        }

        var pipeline = c.Pipeline!;
        try
        {
            long concealed = pipeline.Scheduler.GetStats().ConcealedFrames;
            pipeline.ReplaceOutputRate(next.SampleRate);
            c.ConcealedBase += concealed;
        }
        catch (Exception)
        {
            DisposeQuietly(next);
            OutputFailedLocked(c, now);
            return;
        }
        // ReplaceOutputRate keeps only the clamped offset; re-apply what the user asked for.
        pipeline.Scheduler.SetOffset(_requestedOffsetMs, c.LatencyMs);
        StartOutputLocked(c, next, now);
    }

    private void StartOutputLocked(Connection c, IAudioOutput output, long now)
    {
        // The event may fire on an audio thread: only note it; the rebuild happens on the next Tick.
        Action handler = () => Volatile.Write(ref c.InvalidatedOutput, output);
        output.DeviceInvalidated += handler;
        try
        {
            output.Start(c.Pipeline!.Scheduler);
        }
        catch (Exception)
        {
            output.DeviceInvalidated -= handler;
            DisposeQuietly(output);
            OutputFailedLocked(c, now);
            return;
        }
        c.Output = output;
        c.OutputInvalidatedHandler = handler;
    }

    private static void StopOutputLocked(Connection c)
    {
        if (c.Output is not { } output) return;
        if (c.OutputInvalidatedHandler is { } handler) output.DeviceInvalidated -= handler;
        try { output.Stop(); } catch (Exception) { }
        DisposeQuietly(output);
        c.Output = null;
        c.OutputInvalidatedHandler = null;
    }

    private void OutputFailedLocked(Connection c, long now)
    {
        Interlocked.Increment(ref _outputFailures);
        c.NextOutputAttemptTicks = now + OutputRetryTicks;
    }

    private HeartbeatStats BuildStatsLocked(Connection c, long now, SyncState state)
    {
        double loss = double.NaN, late = double.NaN, syncError = double.NaN;
        if (c.Pipeline is { } p)
        {
            var s = p.Scheduler.GetStats();
            c.Window.Add(now, p.Buffer.Received, p.Buffer.Late, c.ConcealedBase + s.ConcealedFrames);
            loss = c.Window.LossPct;
            late = c.Window.LatePct;
            if (p.Scheduler.State == PlaybackState.Playing) syncError = s.SyncErrorMs;
        }
        return new HeartbeatStats(loss, late, c.Estimator.DriftPpm, c.Estimator.ResidualMs, syncError, state);
    }

    // ---- connection lifecycle --------------------------------------------------------------------------------------

    /// <summary>Host silent too long: tear down and start reconnecting.</summary>
    private bool LoseConnectionLocked(long now)
    {
        CloseConnectionLocked();
        _reconnecting = true;
        _lostTicks = now;
        _lastAttemptTicks = null;
        return SetStateLocked(SyncState.Connecting);
    }

    private async Task ReconnectAttemptAsync(int generation, IPEndPoint endpoint, string code, int audioPort)
    {
        bool changed = false;
        try
        {
            var (result, conn) = await ConnectAsync(endpoint, code, audioPort, _cts.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (generation != _generation || _disposed || !_reconnecting)
                {
                    conn?.Close();
                }
                else if (conn is not null)
                {
                    InstallLocked(conn);
                    changed = true;
                }
                else if (result.Outcome == JoinOutcome.Rejected)
                {
                    _reconnecting = false;
                    _generation++;
                    changed = SetStateLocked(SyncState.Disconnected);
                }
                // Unreachable: the next Tick schedules another attempt.
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            Interlocked.Increment(ref _faults);
        }
        finally
        {
            lock (_gate) _attemptRunning = false;
        }
        if (changed) RaiseStateChanged();
    }

    /// <summary>Makes <paramref name="conn"/> current: a new session (clock sync started; playback waits for convergence).</summary>
    private void InstallLocked(Connection conn)
    {
        CloseConnectionLocked();
        _reconnecting = false;

        long now = _clock.NowTicks;
        conn.LastHeardTicks = now;
        conn.LatencyMs = Math.Clamp(conn.Welcome.TargetLatencyMs, MinLatencyMs, MaxLatencyMs);
        conn.Estimator = new ClockEstimator();
        var clockServer = new IPEndPoint(_endpoint!.Address, conn.Welcome.ClockPort);
        IDatagramChannel channel;
        try
        {
            channel = _channels.Create(new IPEndPoint(IPAddress.Any, 0));
        }
        catch (Exception)
        {
            conn.Close();
            throw;
        }
        conn.ClockClient = new ClockSyncClient(channel, _clock, clockServer, conn.Welcome.SessionId, conn.Estimator);
        conn.ClockClient.Start();
        _conn = conn;
        SetStateLocked(SyncState.Syncing);
        conn.Reader = Task.Run(() => ReadLoopAsync(conn));
    }

    private void CloseConnectionLocked()
    {
        if (_conn is not { } c) return;
        _conn = null;
        TeardownLocked(c, closeTcp: true);
    }

    /// <summary>Stops audio for the session (output, pipeline routing, clock client) and optionally closes the TCP connection.</summary>
    private void TeardownLocked(Connection c, bool closeTcp)
    {
        StopOutputLocked(c);
        if (ReferenceEquals(Volatile.Read(ref _audioTarget), c.Pipeline)) Volatile.Write(ref _audioTarget, null);
        c.Pipeline = null;
        c.ClockClient?.Dispose();
        if (closeTcp) c.Close();
    }

    private void EnsureAudioChannelLocked()
    {
        if (_audioChannel is not null) return;
        var channel = _channels.Create(new IPEndPoint(IPAddress.Any, _options.AudioPort));
        try
        {
            channel.Start(OnAudioDatagram);
        }
        catch
        {
            channel.Dispose();
            throw;
        }
        _audioChannel = channel;
    }

    private void OnAudioDatagram(ReadOnlySpan<byte> data, IPEndPoint from) => Volatile.Read(ref _audioTarget)?.HandleAudioDatagram(data);

    /// <summary>One handshake: connect, Hello, first reply. Never throws except for cancellation of <paramref name="ct"/>.</summary>
    private async Task<(JoinResult Result, Connection? Conn)> ConnectAsync(IPEndPoint control, string code, int audioPort, CancellationToken ct)
    {
        var tcp = new TcpClient(control.AddressFamily) { NoDelay = true };
        bool handedOver = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(HandshakeTimeout);
            await tcp.ConnectAsync(control, timeout.Token).ConfigureAwait(false);
            var stream = tcp.GetStream();
            await ControlCodec.WriteAsync(stream, new Hello(_options.DeviceId, _options.DeviceName, code, ProtocolVersion, audioPort),
                                          timeout.Token).ConfigureAwait(false);
            var reply = await ControlCodec.ReadAsync(stream, timeout.Token).ConfigureAwait(false);
            switch (reply)
            {
                case Welcome w when IsUsable(w):
                    handedOver = true;
                    return (new JoinResult(JoinOutcome.Joined), new Connection(tcp, w));
                case Reject r:
                    return (new JoinResult(JoinOutcome.Rejected, r.Reason), null);
                default:
                    return (new JoinResult(JoinOutcome.Unreachable), null);   // closed, or not a host we can play from
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (new JoinResult(JoinOutcome.Unreachable), null);   // handshake timeout
        }
        catch (Exception e) when (e is SocketException or IOException or InvalidDataException or ObjectDisposedException)
        {
            return (new JoinResult(JoinOutcome.Unreachable), null);
        }
        finally
        {
            if (!handedOver) tcp.Dispose();
        }
    }

    private static bool IsUsable(Welcome w) =>
        w.SampleRate == AudioFormat.SampleRate && w.Channels == AudioFormat.Channels && w.FrameSamples == AudioFormat.FrameSamples
        && w.ClockPort is >= 1 and <= 65535;

    // ---- control messages ------------------------------------------------------------------------------------------

    private async Task ReadLoopAsync(Connection c)
    {
        try
        {
            while (true)
            {
                var message = await ControlCodec.ReadAsync(c.Stream, c.Cts.Token).ConfigureAwait(false);
                if (message is null) return;   // host closed; silence detection takes it from here
                Volatile.Write(ref c.LastHeardTicks, _clock.NowTicks);   // any message is proof of life

                switch (message)
                {
                    case SetLatency l:
                        lock (_gate)
                        {
                            if (_conn != c) break;
                            c.LatencyMs = Math.Clamp(l.TargetLatencyMs, MinLatencyMs, MaxLatencyMs);
                            c.Pipeline?.Scheduler.SetOffset(_requestedOffsetMs, c.LatencyMs);
                        }
                        break;
                    case SetDeviceSettings s:
                        // The control JSON allows NaN/Infinity literals: refuse them rather than trust the host.
                        if (!float.IsFinite(s.Volume) || !double.IsFinite(s.OffsetMs))
                        {
                            Interlocked.Increment(ref _ignoredSettings);
                            break;
                        }
                        lock (_gate)
                        {
                            if (_conn != c) break;
                            ApplyVolumeLocked(s.Volume);
                            ApplyOffsetLocked(s.OffsetMs);
                        }
                        break;
                    // Heartbeat: liveness only. Anything else is ignored so newer hosts stay compatible.
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or InvalidDataException or SocketException
                                      or ObjectDisposedException)
        {
            // Closed by us, network failure or a bad frame: the connection ends; silence detection reconnects.
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _faults);
        }
        finally
        {
            c.Close();
        }
    }

    private void ApplyVolumeLocked(float volume)
    {
        _volume = Math.Clamp(volume, 0f, 1f);
        if (_conn?.Pipeline is { } p) p.Gain.Gain = _volume;
    }

    private void ApplyOffsetLocked(double offsetMs)
    {
        _requestedOffsetMs = Math.Clamp(offsetMs, -MaxOffsetMs, MaxOffsetMs);
        if (_conn is { Pipeline: { } p } c) p.Scheduler.SetOffset(_requestedOffsetMs, c.LatencyMs);
    }

    /// <summary>Sends one message; a failed or timed-out write closes the connection (a half-written frame cannot be recovered).</summary>
    private static async Task SendAsync(Connection c, ControlMessage message)
    {
        try
        {
            await c.WriteLock.WaitAsync(c.Cts.Token).ConfigureAwait(false);
            try { await WriteAsync(c, message).ConfigureAwait(false); }
            finally { c.WriteLock.Release(); }
        }
        catch (Exception)
        {
            c.Close();
        }
    }

    private static async Task SendHeartbeatAsync(Connection c, Heartbeat heartbeat)
    {
        // A write still in flight means a slow link: skip this heartbeat rather than queue them up.
        if (!c.WriteLock.Wait(0)) return;
        try
        {
            await WriteAsync(c, heartbeat).ConfigureAwait(false);
        }
        catch (Exception)
        {
            c.Close();
        }
        finally
        {
            c.WriteLock.Release();
        }
    }

    private static async Task WriteAsync(Connection c, ControlMessage message)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(c.Cts.Token);
        cts.CancelAfter(WriteTimeout);
        await ControlCodec.WriteAsync(c.Stream, message, cts.Token).ConfigureAwait(false);
    }

    // ---- state and disposal ----------------------------------------------------------------------------------------

    private bool SetStateLocked(SyncState state) => Interlocked.Exchange(ref _state, (int)state) != (int)state;

    private void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(); }
        catch (Exception) { Interlocked.Increment(ref _faults); }   // a faulty subscriber must not break the session
    }

    private static void DisposeQuietly(IDisposable d)
    {
        try { d.Dispose(); } catch (Exception) { }
    }

    /// <summary>Closes the connection (without Bye; use <see cref="LeaveAsync"/> first for a polite exit) and releases everything.</summary>
    public async ValueTask DisposeAsync()
    {
        Task attempt, reader;
        bool changed;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            _reconnecting = false;
            reader = _conn?.Reader ?? Task.CompletedTask;
            CloseConnectionLocked();
            _audioChannel?.Dispose();
            _audioChannel = null;
            attempt = _attemptTask;
            changed = SetStateLocked(SyncState.Disconnected);
        }
        _cts.Cancel();
        if (changed) RaiseStateChanged();
        await Task.WhenAll(attempt, reader).ConfigureAwait(false);   // neither faults
        _cts.Dispose();
    }
}
