namespace RhythmSync.Core.Audio;

/// <summary>
/// Receives one chunk of captured audio on the capture thread. <paramref name="interleavedStereo"/> is at the device
/// rate and only valid for the duration of the call; <paramref name="devicePositionFrames"/> is the device stream
/// position of its first frame, which was captured at session-clock time <paramref name="qpcTicks"/> (100 ns).
/// </summary>
public delegate void CaptureHandler(ReadOnlySpan<float> interleavedStereo, long devicePositionFrames, long qpcTicks);

/// <summary>A loopback/input capture device producing interleaved stereo float audio.</summary>
public interface IAudioCapture : IDisposable
{
    int SampleRate { get; }
    void Start(CaptureHandler h);
    void Stop();

    /// <summary>Raised when the device disappears or changes format; the owner must recreate the capture.</summary>
    event Action? DeviceInvalidated;
}
