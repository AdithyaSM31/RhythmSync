namespace RhythmSync.Core.Audio;

/// <summary>
/// Catmull-Rom (cubic Hermite) fractional-rate interpolator for interleaved stereo float audio. Lets the
/// client play slightly faster or slower (up to about +/-500 ppm) to track clock drift. Stateless and
/// allocation-free, so it is safe to call from the audio thread.
/// </summary>
public static class CubicResampler
{
    private const int Channels = AudioFormat.Channels;

    /// <summary>
    /// Number of input frames that <see cref="Process"/> needs for <paramref name="outFrames"/> output frames
    /// starting at <paramref name="position"/> and advancing <paramref name="step"/> input frames per output frame.
    /// </summary>
    public static int RequiredInputFrames(double position, double step, int outFrames) =>
        (int)Math.Floor(position + (outFrames - 1) * step) + 3;

    /// <summary>
    /// Resamples <paramref name="input"/> (interleaved stereo) into <paramref name="output"/> (interleaved stereo;
    /// the frame count is <c>output.Length / 2</c>). Output frame k reads input position <c>position + k * step</c>,
    /// interpolating input frames <c>i-1 .. i+2</c> where <c>i = floor(pos)</c>.
    /// </summary>
    /// <param name="position">Fractional input frame index of the first output frame; must be at least 1.</param>
    /// <param name="step">Input frames consumed per output frame.</param>
    /// <returns>The input position after the last output frame (<c>position + outFrames * step</c>).</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="output"/> has an odd length, <paramref name="position"/> is below 1, or
    /// <paramref name="input"/> holds fewer than <see cref="RequiredInputFrames"/> frames.
    /// </exception>
    public static double Process(ReadOnlySpan<float> input, double position, double step, Span<float> output)
    {
        if (output.Length % Channels != 0)
            throw new ArgumentException("Output length must be a whole number of stereo frames.", nameof(output));
        if (!(position >= 1.0))
            throw new ArgumentException("Position must be at least 1 (one frame of history is needed).", nameof(position));

        int outFrames = output.Length / Channels;
        if (outFrames == 0) return position;

        int required = RequiredInputFrames(position, step, outFrames);
        if (input.Length / Channels < required)
            throw new ArgumentException(
                $"Input has {input.Length / Channels} frames but {required} are required.", nameof(input));

        for (int k = 0; k < outFrames; k++)
        {
            double pos = position + k * step;
            int i = (int)Math.Floor(pos);
            float x = (float)(pos - i);
            int b = (i - 1) * Channels;
            for (int ch = 0; ch < Channels; ch++)
            {
                float p0 = input[b + ch];
                float p1 = input[b + Channels + ch];
                float p2 = input[b + 2 * Channels + ch];
                float p3 = input[b + 3 * Channels + ch];
                output[k * Channels + ch] =
                    p1 + 0.5f * x * (p2 - p0 + x * (2f * p0 - 5f * p1 + 4f * p2 - p3 + x * (3f * (p1 - p2) + p3 - p0)));
            }
        }

        return position + outFrames * step;
    }
}
