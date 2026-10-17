namespace RhythmSync.Core.Audio;

/// <summary>
/// Runs a list of <see cref="IAudioProcessor"/>s in insertion order. Processors are added during setup,
/// before audio starts: <see cref="Add"/> is NOT thread-safe with <see cref="Process"/>.
/// </summary>
public sealed class DspChain : IAudioProcessor
{
    private readonly List<IAudioProcessor> _processors = new();

    public void Add(IAudioProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        _processors.Add(processor);
    }

    public void Process(Span<float> interleaved, int channels)
    {
        for (int i = 0; i < _processors.Count; i++)
            _processors[i].Process(interleaved, channels);
    }
}
