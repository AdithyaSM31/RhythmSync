namespace RhythmSync.Core.Host;

/// <summary>Receives finished 5 ms, 48 kHz, 16-bit stereo frames from <see cref="FrameAssembler"/>.</summary>
public interface IFrameSink
{
    /// <summary>
    /// Called once per frame, in order. <paramref name="pcm"/> holds <see cref="AudioFormat.FrameFloats"/>
    /// interleaved samples and is only valid for the duration of the call (the buffer is reused).
    /// </summary>
    /// <param name="seq">Consecutive frame sequence number, starting at 0.</param>
    /// <param name="playAt">Session time (100 ns ticks) at which the first sample should be audible.</param>
    void OnFrame(uint seq, long playAt, ReadOnlySpan<short> pcm);
}
