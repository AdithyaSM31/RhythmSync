using Serilog;

namespace RhythmSync.App.Services;

/// <summary>The machine-wide part of single-instance detection (a named mutex and event in the app; a fake in tests).</summary>
public interface ISingleInstanceGate : IDisposable
{
    /// <summary>True if this process is the first instance; it then holds the instance name until disposed.</summary>
    bool TryClaim();

    /// <summary>Asks the first instance to show its window (a later instance calls this, then exits).</summary>
    void SignalFirstInstance();

    /// <summary>Calls <paramref name="onSignal"/> (on any thread) each time a later instance signals, including one that signalled before this call.</summary>
    void ListenForSignals(Action onSignal);
}

/// <summary>
/// Keeps RhythmSync to one instance per user session: the first launch runs and shows its window again whenever another
/// launch is attempted; a later launch only signals the first one and must exit at once (before creating a tray icon,
/// sockets or settings).
/// </summary>
public sealed class SingleInstanceCoordinator(ISingleInstanceGate gate) : IDisposable
{
    private readonly ISingleInstanceGate _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    private bool _claimed;

    /// <param name="activateWindow">Shows and activates this instance's window; called on any thread (post it to the UI thread).</param>
    /// <returns>True when this is the first instance and should run; false when it signalled the first instance and should exit.</returns>
    public bool Claim(Action activateWindow)
    {
        ArgumentNullException.ThrowIfNull(activateWindow);
        if (_claimed) throw new InvalidOperationException("Claim may be called only once.");
        _claimed = true;

        if (!_gate.TryClaim())
        {
            _gate.SignalFirstInstance();
            return false;
        }

        _gate.ListenForSignals(() =>
        {
            try { activateWindow(); }
            catch (Exception e) { Log.Warning(e, "Showing the window for a second launch failed"); }
        });
        return true;
    }

    public void Dispose() => _gate.Dispose();
}
