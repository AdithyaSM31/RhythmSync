namespace RhythmSync.Core.Audio;

/// <summary>
/// An in-place processing stage on interleaved float audio. Implementations run on the audio thread and must
/// not allocate or block inside <see cref="Process"/>.
/// </summary>
public interface IAudioProcessor
{
    /// <summary>Processes <paramref name="interleaved"/> in place; its length is a whole number of <paramref name="channels"/>-sample frames.</summary>
    void Process(Span<float> interleaved, int channels);
}
