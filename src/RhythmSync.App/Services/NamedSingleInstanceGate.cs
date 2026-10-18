using System.Runtime.InteropServices;

namespace RhythmSync.App.Services;

/// <summary>
/// <see cref="ISingleInstanceGate"/> with per-session named kernel objects: a mutex <c>Local\{name}</c> whose creator is
/// the first instance, and an auto-reset event <c>Local\{name}.Activate</c> a later instance sets.
/// </summary>
/// <remarks>
/// The event is opened before the mutex, so whenever a later instance finds the mutex, the event already exists and its
/// signal stays set until the first instance waits on it: no launch is lost to a race at startup.
/// </remarks>
public sealed class NamedSingleInstanceGate : ISingleInstanceGate
{
    public const string AppName = "RhythmSync.SingleInstance";

    private const int AsfwAny = -1;

    private readonly string _mutexName;
    private readonly string _eventName;
    private EventWaitHandle? _event;
    private Mutex? _mutex;
    private RegisteredWaitHandle? _registration;

    public NamedSingleInstanceGate(string name = AppName)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _mutexName = $@"Local\{name}";
        _eventName = $@"Local\{name}.Activate";
    }

    public bool TryClaim()
    {
        if (_mutex is not null) throw new InvalidOperationException("TryClaim may be called only once.");
        _event = new EventWaitHandle(false, EventResetMode.AutoReset, _eventName);
        _mutex = new Mutex(initiallyOwned: false, _mutexName, out bool createdNew);   // never owned: only its existence matters
        return createdNew;
    }

    public void SignalFirstInstance()
    {
        if (_event is null) throw new InvalidOperationException("Call TryClaim first.");
        AllowSetForegroundWindow(AsfwAny);   // this launch may bring a window to the front; let the first instance do it
        _event.Set();
    }

    public void ListenForSignals(Action onSignal)
    {
        ArgumentNullException.ThrowIfNull(onSignal);
        if (_event is null) throw new InvalidOperationException("Call TryClaim first.");
        _registration = ThreadPool.RegisterWaitForSingleObject(_event, (_, _) => onSignal(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _registration = null;
        _event?.Dispose();
        _event = null;
        _mutex?.Dispose();
        _mutex = null;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
