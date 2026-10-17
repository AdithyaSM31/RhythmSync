using RhythmSync.Core.Audio;

namespace RhythmSync.Platform.Windows;

/// <summary>
/// Renders silence on a device so a WASAPI loopback capture of that device keeps delivering packets (loopback
/// produces nothing while no stream is playing). Dispose stops it and releases the device. Single-use.
/// </summary>
public sealed class SilenceKeeper : IDisposable
{
    private sealed class ZeroCallback : IRenderCallback
    {
        public static readonly ZeroCallback Instance = new();
        public void Render(Span<float> stereo, in RenderTiming timing) => stereo.Clear();
    }

    private readonly WasapiRenderOutput _output;

    /// <param name="renderDeviceId">Render endpoint id, or null for the default device (followed if it changes).</param>
    public SilenceKeeper(string? renderDeviceId) => _output = new WasapiRenderOutput(renderDeviceId);

    /// <summary>Raised when the device is lost; same semantics and threading as <see cref="WasapiRenderOutput.DeviceInvalidated"/>.</summary>
    public event Action? DeviceInvalidated
    {
        add => _output.DeviceInvalidated += value;
        remove => _output.DeviceInvalidated -= value;
    }

    public void Start() => _output.Start(ZeroCallback.Instance);

    public void Dispose() => _output.Dispose();
}
