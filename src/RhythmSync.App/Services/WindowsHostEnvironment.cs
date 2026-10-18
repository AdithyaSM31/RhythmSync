using RhythmSync.Core.Audio;
using RhythmSync.Core.Net;
using RhythmSync.Platform.Windows;

namespace RhythmSync.App.Services;

/// <summary><see cref="IHostEnvironment"/> on this PC: WASAPI loopback and render, Core Audio device queries, real UDP sockets.</summary>
public sealed class WindowsHostEnvironment : IHostEnvironment
{
    private sealed class KeeperAdapter(SilenceKeeper keeper) : ISilenceKeeper
    {
        public void Start() => keeper.Start();
        public void Dispose() => keeper.Dispose();

        public event Action? DeviceInvalidated
        {
            add => keeper.DeviceInvalidated += value;
            remove => keeper.DeviceInvalidated -= value;
        }
    }

    private sealed class WatchHandle : IDisposable
    {
        private readonly DeviceWatcher _watcher;
        private readonly Action _handler;

        public WatchHandle(Action changed)
        {
            _handler = changed;
            _watcher = new DeviceWatcher();
            _watcher.DefaultRenderChanged += _handler;
        }

        public void Dispose()
        {
            _watcher.DefaultRenderChanged -= _handler;
            _watcher.Dispose();
        }
    }

    public IDatagramChannelFactory Channels { get; } = new UdpDatagramChannelFactory();

    public IAudioCapture CreateCapture(string? renderDeviceId) => new WasapiLoopbackSource(renderDeviceId);

    public ISilenceKeeper CreateSilenceKeeper(string? renderDeviceId) => new KeeperAdapter(new SilenceKeeper(renderDeviceId));

    public IAudioOutput CreateLocalOutput(string deviceId) => new WasapiRenderOutput(deviceId);

    public IReadOnlyList<AudioDeviceInfo> Speakers() => DeviceCatalog.RenderDevices();

    public string? DefaultRenderId() => DeviceCatalog.DefaultRenderId();

    public string? VbCableId() => DeviceCatalog.FindVbCableRender()?.Id;

    public IDisposable WatchDefaultRender(Action changed) => new WatchHandle(changed);
}
