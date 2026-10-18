using RhythmSync.Core.Audio;
using RhythmSync.Core.Net;
using RhythmSync.Platform.Windows;

namespace RhythmSync.App.Services;

/// <summary>A started-once silence renderer that keeps loopback capture delivering packets.</summary>
public interface ISilenceKeeper : IDisposable
{
    void Start();

    /// <summary>Raised (on any thread) when the device is lost (or, for the default device, when the default changed).</summary>
    event Action? DeviceInvalidated;
}

/// <summary>
/// Everything the host needs from the machine: audio devices, sockets and device notifications. The Windows
/// implementation uses WASAPI; tests substitute fakes.
/// </summary>
public interface IHostEnvironment
{
    IDatagramChannelFactory Channels { get; }

    /// <summary>Loopback capture of the given playback endpoint, or of the default device when null (not yet started).</summary>
    IAudioCapture CreateCapture(string? renderDeviceId);

    /// <summary>A silence renderer on the given playback endpoint, or on the default device when null (not yet started).</summary>
    ISilenceKeeper CreateSilenceKeeper(string? renderDeviceId);

    /// <summary>A render output on the given real speakers (not yet started).</summary>
    IAudioOutput CreateLocalOutput(string deviceId);

    /// <summary>Active playback devices.</summary>
    IReadOnlyList<AudioDeviceInfo> Speakers();

    string? DefaultRenderId();

    /// <summary>Endpoint id of the VB-CABLE playback device, or null when it is not installed.</summary>
    string? VbCableId();

    /// <summary>Calls <paramref name="changed"/> (on any thread) when the default playback device changes. Dispose to stop.</summary>
    IDisposable WatchDefaultRender(Action changed);
}
