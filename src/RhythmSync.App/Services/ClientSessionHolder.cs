using System.Net;
using RhythmSync.App.ViewModels;
using RhythmSync.Core.Audio.Eq;
using RhythmSync.Core.Client;
using RhythmSync.Core.Clock;
using Serilog;

namespace RhythmSync.App.Services;

/// <summary>
/// Owns the app's <see cref="ClientSession"/> and lets host mode take the network ports from it. The session is created
/// on the first join. <see cref="ReleaseAsync"/> leaves, disposes it (freeing the audio port) and blocks joining until
/// <see cref="Allow"/>; the next join then creates a fresh session. Volume and offset are whatever the host sent the
/// current session (the host keeps them per device and sends them on every join).
/// Exposes the session to the client view model. Thread-safe: <see cref="Tick"/> is called from the ticker thread while
/// the UI thread joins and releases.
/// </summary>
public sealed class ClientSessionHolder : IClientSessionControl, IAsyncDisposable
{
    private readonly Func<ClientSession> _factory;
    private readonly object _gate = new();
    private readonly Func<string>? _currentDeviceName;
    private ClientSession? _session;
    private string? _sessionName;   // the device name _session was created with
    private bool _blocked, _disposed;

    /// <param name="factory">Creates a session; it must read the current device name each time it is called.</param>
    /// <param name="currentDeviceName">The name the factory would use now. A session is built with one name for its whole life, so a
    /// disconnected session built with an older name is replaced at the next join (a renamed PC says its new name in the Hello).</param>
    public ClientSessionHolder(Func<ClientSession> factory, Func<string>? currentDeviceName = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _currentDeviceName = currentDeviceName;
    }

    public SyncState State => Volatile.Read(ref _session)?.State ?? SyncState.Disconnected;

    public event Action? StateChanged;

    public float Volume => Volatile.Read(ref _session)?.Volume ?? 1f;

    public double OffsetMs => Volatile.Read(ref _session)?.OffsetMs ?? 0;

    public EqGains Eq => Volatile.Read(ref _session)?.Eq ?? EqGains.Flat;

    public PlaybackStats? Stats => Volatile.Read(ref _session)?.Stats;

    public long OutputFailures => Volatile.Read(ref _session)?.OutputFailures ?? 0;

    /// <summary>Drives the current session, if any. Called from the ticker thread.</summary>
    public void Tick() => Volatile.Read(ref _session)?.Tick();

    public Task LeaveAsync() => Volatile.Read(ref _session)?.LeaveAsync() ?? Task.CompletedTask;

    /// <exception cref="InvalidOperationException">Joining is blocked because this PC is hosting.</exception>
    public async Task<JoinResult> JoinAsync(IPEndPoint control, string code)
    {
        // A session that is not connected but was built with an older device name is replaced first (and its ports freed),
        // so the Hello carries the current name. A connected or connecting session is never replaced.
        ClientSession? stale = null;
        lock (_gate)
        {
            CheckCanJoin();
            if (_session is { State: SyncState.Disconnected } && _currentDeviceName is not null && _sessionName != _currentDeviceName())
            {
                stale = _session;
                _session = null;
            }
        }

        if (stale is not null)
        {
            try { await stale.DisposeAsync().ConfigureAwait(false); }
            catch (Exception e) { Log.Warning(e, "Disposing the outdated client session failed"); }
        }

        ClientSession session;
        lock (_gate)
        {
            CheckCanJoin();   // hosting may have started, or the app quit, while the old session was disposed
            if (_session is null)
            {
                _sessionName = _currentDeviceName?.Invoke();
                _session = _factory();
                _session.StateChanged += OnSessionStateChanged;
            }
            session = _session;
        }
        return await session.JoinAsync(control, code, CancellationToken.None).ConfigureAwait(false);
    }

    private void CheckCanJoin()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_blocked) throw new InvalidOperationException("This PC is hosting; stop hosting before joining another host.");
    }

    /// <summary>Leaves the host, disposes the session so it frees its ports, and blocks joining until <see cref="Allow"/>.</summary>
    public async Task ReleaseAsync()
    {
        ClientSession? session;
        lock (_gate)
        {
            _blocked = true;
            session = _session;
        }
        if (session is null) return;

        try { await session.LeaveAsync().ConfigureAwait(false); }
        catch (Exception e) { Log.Warning(e, "Leaving the host failed"); }

        lock (_gate)
        {
            if (ReferenceEquals(_session, session)) _session = null;
        }
        try { await session.DisposeAsync().ConfigureAwait(false); }
        catch (Exception e) { Log.Warning(e, "Disposing the client session failed"); }
        RaiseStateChanged();   // now Disconnected, whatever the disposed session reported
    }

    /// <summary>Lets the next join create a new session again.</summary>
    public void Allow()
    {
        lock (_gate) _blocked = false;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _blocked = true;
        }
        await ReleaseAsync().ConfigureAwait(false);
    }

    private void OnSessionStateChanged() => RaiseStateChanged();

    private void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(); }
        catch (Exception e) { Log.Warning(e, "A client state subscriber failed"); }
    }
}
