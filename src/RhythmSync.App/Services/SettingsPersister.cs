using System.IO;
using RhythmSync.Core.Config;
using Serilog;

namespace RhythmSync.App.Services;

/// <summary>
/// The app's single copy of <see cref="AppSettings"/>. Changes apply in memory at once and are written to the
/// <see cref="SettingsStore"/> on a thread-pool thread shortly afterwards, coalescing bursts (a slider drag changes a
/// value many times a second), so the UI and the audio path never wait for the disk. <see cref="Dispose"/> writes anything
/// still pending. Thread-safe. A failed write is logged and retried with the next change.
/// </summary>
public sealed class SettingsPersister : IDisposable
{
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(500);

    private readonly SettingsStore _store;
    private readonly TimeSpan _delay;
    private readonly object _gate = new();
    private readonly object _writeGate = new();   // one write at a time, newest snapshot last
    private readonly Timer _timer;
    private AppSettings _current;
    private bool _dirty, _disposed;

    public SettingsPersister(SettingsStore store, AppSettings initial, TimeSpan? delay = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _current = initial ?? throw new ArgumentNullException(nameof(initial));
        _delay = delay ?? DefaultDelay;
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public AppSettings Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>Applies <paramref name="change"/> to the current settings and schedules a write (or writes now when <paramref name="immediate"/>).</summary>
    public void Update(Func<AppSettings, AppSettings> change, bool immediate = false)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            if (_disposed) return;
            _current = change(_current);
            _dirty = true;
            if (!immediate) _timer.Change(_delay, Timeout.InfiniteTimeSpan);
        }

        if (immediate) Flush();
    }

    /// <summary>Writes the current settings if they changed since the last write. Never throws.</summary>
    public void Flush()
    {
        lock (_writeGate)
        {
            AppSettings snapshot;
            lock (_gate)
            {
                if (!_dirty) return;
                _dirty = false;
                snapshot = _current;
            }

            try
            {
                _store.Save(snapshot);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warning(e, "Could not save settings");
                lock (_gate) _dirty = true;   // the next change (or Dispose) tries again
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _timer.Dispose();
        Flush();
    }
}
