namespace RhythmSync.Core.Audio;

/// <summary>
/// Click-free packet-loss concealment for 5 ms stereo frames (<see cref="AudioFormat.FrameFloats"/> interleaved
/// floats). A missing frame repeats the last good frame with a gain that falls 10% per frame down to silence;
/// every splice (start of a repeat, first good frame after a short gap) is blended from the last output sample
/// over <see cref="SpliceFrames"/> sample frames. The first good frame after a long gap, or after
/// <see cref="Reset"/>, fades in across the whole frame. Allocation-free after construction; single-threaded
/// (audio thread).
/// </summary>
public sealed class Concealer
{
    public const int SpliceFrames = 48;
    public const int MaxConcealFrames = 10;

    private const int Channels = AudioFormat.Channels;
    private const int FrameFloats = AudioFormat.FrameFloats;

    private readonly float[] _lastGood = new float[FrameFloats];
    private readonly float[] _lastOut = new float[Channels];
    private bool _hasGood;
    private bool _fadeInPending;
    private int _gapLength; // missing frames since the last present frame (saturating)

    /// <summary>Total number of missing frames passed to <see cref="Process"/>.</summary>
    public long ConcealedFrames { get; private set; }

    /// <summary>Forgets all history; the next present frame fades in 0 to 1 across all of its sample frames.</summary>
    public void Reset()
    {
        _hasGood = false;
        _fadeInPending = true;
        _gapLength = 0;
        Array.Clear(_lastOut);
    }

    /// <summary>
    /// Processes one frame in place. If <paramref name="present"/> is true, <paramref name="frame"/> holds the
    /// received audio; otherwise its contents are ignored and overwritten with concealment output.
    /// </summary>
    public void Process(Span<float> frame, bool present)
    {
        if (frame.Length != FrameFloats)
            throw new ArgumentException($"Frame must hold {FrameFloats} floats.", nameof(frame));

        if (present) ProcessPresent(frame);
        else ProcessMissing(frame);

        _lastOut[0] = frame[FrameFloats - 2];
        _lastOut[1] = frame[FrameFloats - 1];
    }

    private void ProcessPresent(Span<float> frame)
    {
        frame.CopyTo(_lastGood);
        _hasGood = true;

        if (_fadeInPending || _gapLength > MaxConcealFrames)
            Ramp(frame, 0f, 1f);
        else if (_gapLength > 0)
            Splice(frame);

        _fadeInPending = false;
        _gapLength = 0;
    }

    private void ProcessMissing(Span<float> frame)
    {
        ConcealedFrames++;
        if (_gapLength < int.MaxValue) _gapLength++;
        int m = _gapLength;

        if (!_hasGood || m > MaxConcealFrames)
        {
            frame.Clear();
            return;
        }

        float startGain = 1f - (m - 1) / (float)MaxConcealFrames;
        float endGain = 1f - m / (float)MaxConcealFrames;
        _lastGood.CopyTo(frame);
        Ramp(frame, startGain, endGain);
        Splice(frame);
    }

    /// <summary>Blends the start of <paramref name="frame"/> from the last output sample: out = (1-w)*last + w*new, w = i/SpliceFrames.</summary>
    private void Splice(Span<float> frame)
    {
        for (int i = 0; i < SpliceFrames; i++)
        {
            float w = i / (float)SpliceFrames;
            for (int c = 0; c < Channels; c++)
            {
                int idx = i * Channels + c;
                frame[idx] = (1f - w) * _lastOut[c] + w * frame[idx];
            }
        }
    }

    /// <summary>
    /// Multiplies <paramref name="interleavedStereo"/> by a gain that moves linearly from <paramref name="fromGain"/>
    /// at the first sample frame to <paramref name="toGain"/> at the last.
    /// </summary>
    public static void Ramp(Span<float> interleavedStereo, float fromGain, float toGain)
    {
        int frames = interleavedStereo.Length / Channels;
        if (frames == 0) return;
        float step = frames > 1 ? (toGain - fromGain) / (frames - 1) : 0f;
        for (int i = 0; i < frames; i++)
        {
            float g = i == frames - 1 ? toGain : fromGain + step * i;
            interleavedStereo[i * Channels] *= g;
            interleavedStereo[i * Channels + 1] *= g;
        }
    }
}
