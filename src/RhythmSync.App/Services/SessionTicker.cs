using System.Runtime.InteropServices;
using Serilog;

namespace RhythmSync.App.Services;

/// <summary>
/// Drives the session's polling from one background task: <c>tick</c> every <see cref="DefaultInterval"/> (10 ms) and
/// <c>slowTick</c> after every <c>slowEvery</c>-th tick (100 → about once a second). An exception from either callback
/// is logged and the ticker keeps running. <see cref="Dispose"/> stops it and waits briefly for an in-flight tick.
/// While running it asks Windows for 1 ms timer resolution, otherwise a 10 ms period is rounded up to about 15.6 ms.
/// </summary>
public sealed class SessionTicker : IDisposable
{
    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint milliseconds);

    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private readonly Action _tick;
    private readonly Action? _slowTick;
    private readonly int _slowEvery;
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly bool _highResolution;
    private int _disposed;

    public SessionTicker(Action tick, Action? slowTick = null, int slowEvery = 100, TimeSpan? interval = null)
    {
        _tick = tick ?? throw new ArgumentNullException(nameof(tick));
        ArgumentOutOfRangeException.ThrowIfLessThan(slowEvery, 1);
        _slowTick = slowTick;
        _slowEvery = slowEvery;
        _timer = new PeriodicTimer(interval ?? DefaultInterval);
        _highResolution = timeBeginPeriod(1) == 0;
        _loop = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        long count = 0;
        try
        {
            while (await _timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                Invoke(_tick, "tick");
                if (_slowTick is not null && ++count % _slowEvery == 0) Invoke(_slowTick, "slow tick");
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        _timer.Dispose();
        bool stopped = false;
        try { stopped = _loop.Wait(StopTimeout); }
        catch (AggregateException) { stopped = true; }
        if (stopped) _cts.Dispose();
        if (_highResolution) timeEndPeriod(1);
    }
}
