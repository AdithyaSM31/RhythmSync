namespace RhythmSync.Core.Audio;

/// <summary>
/// Scalar gain stage. <see cref="Gain"/> may be set from a control thread while <see cref="Process"/> runs on
/// the audio thread; each buffer ramps linearly from the previously applied gain to the target so the last
/// sample frame lands exactly on the target and no zipper click is produced. Only one thread may call
/// <see cref="Process"/>.
/// </summary>
public sealed class GainProcessor : IAudioProcessor
{
    private volatile float _target;
    private float _applied; // audio-thread only

    public GainProcessor(float gain = 1f)
    {
        _target = gain;
        _applied = gain;
    }

    /// <summary>Target gain (linear). Safe to set from any thread; takes effect, ramped, on the next buffer.</summary>
    public float Gain
    {
        get => _target;
        set => _target = value;
    }

    public void Process(Span<float> interleaved, int channels)
    {
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        float target = _target;
        float prev = _applied;
        int frames = interleaved.Length / channels;

        if (prev == target)
        {
            if (target != 1f)
                for (int i = 0; i < interleaved.Length; i++) interleaved[i] *= target;
            return;
        }

        float delta = target - prev;
        int idx = 0;
        for (int f = 0; f < frames; f++)
        {
            float g = f == frames - 1 ? target : prev + delta * (f + 1) / frames;
            for (int c = 0; c < channels; c++) interleaved[idx++] *= g;
        }
        _applied = target;
    }
}
