using System.Net;
using RhythmSync.App.ViewModels;
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
    private ClientSession? _session;
    private bool _blocked, _disposed;

    public ClientSessionHolder(Func<ClientSession> factory) => _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public SyncState State => Volatile.Read(ref _session)?.State ?? SyncState.Disconnected;

    public event Action? StateChanged;

    public float Volume => Volatile.Read(ref _session)?.Volume ?? 1f;

    public double OffsetMs => Volatile.Read(ref _session)?.OffsetMs ?? 0;

    public PlaybackStats? Stats => Volatile.Read(ref _session)?.Stats;

    public long OutputFailures => Volatile.Read(ref _session)?.OutputFailures ?? 0;

    /// <summary>Drives the current session, if any. Called from the ticker thread.</summary>
    public void Tick() => Volatile.Read(ref _session)?.Tick();

    public Task LeaveAsync() => Volatile.Read(ref _session)?.LeaveAsync() ?? Task.CompletedTask;

    /// <exception cref="InvalidOperationException">Joining is blocked because this PC is hosting.</exception>
    public Task<JoinResult> JoinAsync(IPEndPoint control, string code)
    {
        ClientSession session;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_blocked) throw new InvalidOperationException("This PC is hosting; stop hosting before joining another host.");
            if (_session is null)
            {
                _session = _factory();
                _session.StateChanged += OnSessionStateChanged;
            }
            session = _session;
        }
        return session.JoinAsync(control, code, CancellationToken.None);
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
