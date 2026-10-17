using RhythmSync.Core.Audio;

namespace RhythmSync.Platform.Windows;

/// <summary>Creates WASAPI outputs for the device chosen at creation time (null = system default).</summary>
public sealed class WasapiOutputFactory(Func<string?> deviceId) : IAudioOutputFactory
{
    public IAudioOutput Create() => new WasapiRenderOutput(deviceId());
}
