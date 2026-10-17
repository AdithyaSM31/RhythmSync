using Serilog;

namespace RhythmSync.App.Services;

/// <summary>
/// Drives the session's polling from one background task. While a session is active it ticks every
/// <see cref="ActiveInterval"/> (10 ms) and holds a 1 ms system timer resolution; while idle (disconnected, or no
/// session) it ticks every <see cref="IdleInterval"/> (100 ms) and holds no resolution request, so a tray-resident
/// app costs nothing. <c>slowTick</c> (discovery pruning) runs about once a second in both modes. Which mode applies
/// comes from <c>isActive</c>, polled every tick, or from <see cref="SetActive"/>. An exception from any callback is
/// logged and the ticker keeps running. <see cref="Dispose"/> stops it, releases the resolution and waits briefly for
/// an in-flight tick. Begin/End on the <see cref="IHighResolutionTimer"/> are strictly paired under a lock.
/// </summary>
public sealed class SessionTicker : IDisposable
{
    public static readonly TimeSpan ActiveInterval = TimeSpan.FromMilliseconds(10);
    public static readonly TimeSpan IdleInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private readonly Action _tick;
    private readonly Action? _slowTick;
    private readonly Func<bool>? _isActive;
    private readonly IHighResolutionTimer _resolution;
    private readonly TimeSpan _activeInterval, _idleInterval;
    private readonly int _slowEveryActive, _slowEveryIdle;
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly Task _loop;
    private bool _active, _disposed;

    /// <param name="tick">Runs every tick (the client session's Tick).</param>
    /// <param name="slowTick">Runs after every <paramref name="slowEveryActive"/>-th active tick (100 → ~1 s) or <paramref name="slowEveryIdle"/>-th idle tick (10 → ~1 s).</param>
    /// <param name="isActive">Polled each tick; true while a session is active. Null means always active.</param>
    /// <param name="resolution">Timer-resolution seam; defaults to winmm.</param>
    public SessionTicker(Action tick, Action? slowTick = null, Func<bool>? isActive = null,
        IHighResolutionTimer? resolution = null, TimeSpan? activeInterval = null, TimeSpan? idleInterval = null,
        int slowEveryActive = 100, int slowEveryIdle = 10)
    {
        _tick = tick ?? throw new ArgumentNullException(nameof(tick));
        ArgumentOutOfRangeException.ThrowIfLessThan(slowEveryActive, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(slowEveryIdle, 1);
        _slowTick = slowTick;
        _isActive = isActive;
        _resolution = resolution ?? new WinmmHighResolutionTimer();
        _activeInterval = activeInterval ?? ActiveInterval;
        _idleInterval = idleInterval ?? IdleInterval;
        _slowEveryActive = slowEveryActive;
        _slowEveryIdle = slowEveryIdle;

        bool startActive = isActive?.Invoke() ?? true;
        _timer = new PeriodicTimer(startActive ? _activeInterval : _idleInterval);
        _active = startActive;
        if (startActive) _resolution.Begin();
        _loop = Task.Run(RunAsync);
    }

    /// <summary>The period the timer currently runs at.</summary>
    public TimeSpan CurrentInterval
    {
        get { lock (_gate) return _active ? _activeInterval : _idleInterval; }
    }

    /// <summary>Switches between the active and idle cadence. Setting the current mode again does nothing. Thread-safe.</summary>
    public void SetActive(bool active)
    {
        lock (_gate)
        {
            if (_disposed || active == _active) return;
            _active = active;
            if (active)
            {
                _resolution.Begin();   // finer resolution first, then the shorter period
                _timer.Period = _activeInterval;
            }
            else
            {
                _timer.Period = _idleInterval;
                _resolution.End();
            }
        }
    }

    private async Task RunAsync()
    {
        int count = 0;
        try
        {
            while (await _timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                if (_isActive is not null) Invoke(() => SetActive(_isActive()), "state poll");
                Invoke(_tick, "tick");
                if (_slowTick is null) continue;

                bool active;
                lock (_gate) active = _active;
                if (++count >= (active ? _slowEveryActive : _slowEveryIdle))
                {
                    count = 0;
                    Invoke(_slowTick, "slow tick");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void Invoke(Action action, string what)
    {
        try { action(); }
        catch (Exception e) { Log.Error(e, "Session {What} failed", what); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_active)
            {
                _active = false;
                _resolution.End();
            }
        }

        _cts.Cancel();
        _timer.Dispose();
        bool stopped = false;
        try { stopped = _loop.Wait(StopTimeout); }
        catch (AggregateException) { stopped = true; }
        if (stopped) _cts.Dispose();
    }
}
