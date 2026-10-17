using System.Runtime.InteropServices;

namespace RhythmSync.App.Services;

/// <summary>Asks Windows for a finer system timer (<see cref="SessionTicker"/> calls Begin/End strictly paired).</summary>
public interface IHighResolutionTimer
{
    void Begin();
    void End();
}

/// <summary>winmm <c>timeBeginPeriod(1)</c> / <c>timeEndPeriod(1)</c>; End only undoes a Begin that succeeded.</summary>
public sealed class WinmmHighResolutionTimer : IHighResolutionTimer
{
    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint milliseconds);

    private bool _began;

    public void Begin()
    {
        if (!_began && timeBeginPeriod(1) == 0) _began = true;
    }

    public void End()
    {
        if (!_began) return;
        _began = false;
        timeEndPeriod(1);
    }
}
