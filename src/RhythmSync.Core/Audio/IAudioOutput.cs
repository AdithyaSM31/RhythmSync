namespace RhythmSync.Core.Audio;

/// <summary>
/// Device timing for one render callback. <see cref="DevicePositionFrames"/> is the device frame being heard at
/// client session-clock time <see cref="DevicePositionClientTicks"/>; the buffer being rendered starts at device
/// frame <see cref="WritePositionFrames"/>.
/// </summary>
public readonly record struct RenderTiming(long DevicePositionFrames, long DevicePositionClientTicks, long WritePositionFrames);

/// <summary>Fills output buffers on the audio thread. Implementations must not allocate or block.</summary>
public interface IRenderCallback
{
    /// <summary>Fills <paramref name="stereo"/> (interleaved stereo; frames = <c>Length / 2</c>) at the device rate.</summary>
    void Render(Span<float> stereo, in RenderTiming timing);
}

/// <summary>An audio output device that pulls stereo float audio from an <see cref="IRenderCallback"/>.</summary>
public interface IAudioOutput : IDisposable
{
    int SampleRate { get; }
    void Start(IRenderCallback cb);
    void Stop();

    /// <summary>Raised when the device disappears or changes format; the owner must recreate the output.</summary>
    event Action? DeviceInvalidated;
}

public interface IAudioOutputFactory
{
    IAudioOutput Create();
}
