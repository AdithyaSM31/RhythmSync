using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace RhythmSync.Platform.Windows;

/// <summary>
/// Watches for render-device changes. Events fire on a Core Audio notification thread; handlers must be quick
/// and must not call back into Core Audio.
/// </summary>
public sealed class DeviceWatcher : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly Client _client;
    private int _disposed;

    /// <summary>The default (multimedia) render device changed.</summary>
    public event Action? DefaultRenderChanged;

    /// <summary>A render device was removed, unplugged or disabled; the argument is its endpoint id.</summary>
    public event Action<string>? DeviceRemoved;

    public DeviceWatcher()
    {
        _client = new Client(this);
        _enumerator.RegisterEndpointNotificationCallback(_client);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try { _enumerator.UnregisterEndpointNotificationCallback(_client); } catch { /* best effort */ }
        _enumerator.Dispose();
    }

    private void RaiseDefaultChanged()
    {
        if (Volatile.Read(ref _disposed) == 1) return;
        try { DefaultRenderChanged?.Invoke(); } catch { /* never throw into COM */ }
    }

    private void RaiseRemoved(string id)
    {
        if (Volatile.Read(ref _disposed) == 1) return;
        try { DeviceRemoved?.Invoke(id); } catch { /* never throw into COM */ }
    }

    private sealed class Client(DeviceWatcher owner) : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            // Windows reports each role separately; Multimedia is the one render outputs follow.
            if (flow == DataFlow.Render && role == Role.Multimedia) owner.RaiseDefaultChanged();
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            if ((newState & DeviceState.Active) == 0) owner.RaiseRemoved(deviceId);
        }

        public void OnDeviceRemoved(string deviceId) => owner.RaiseRemoved(deviceId);

        public void OnDeviceAdded(string pwstrDeviceId) { }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
