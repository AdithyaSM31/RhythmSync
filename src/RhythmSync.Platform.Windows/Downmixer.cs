namespace RhythmSync.Platform.Windows;

/// <summary>Reduces interleaved multichannel float audio to interleaved stereo.</summary>
public static class Downmixer
{
    private const float Center = 0.707f;

    /// <summary>
    /// Downmixes <c>src.Length / channels</c> frames into <paramref name="dst"/> (2 samples per frame; the rest of
    /// <paramref name="dst"/> is untouched). 1 channel is duplicated, 2 are copied, 6 (FL FR C LFE SL SR) use the ITU
    /// coefficients L = FL + 0.707 C + 0.707 SL and R = FR + 0.707 C + 0.707 SR with the LFE dropped (the sum is not
    /// normalised, so loud material can exceed full scale), and any other layout takes channels 0 and 1.
    /// </summary>
    public static void ToStereo(ReadOnlySpan<float> src, int channels, Span<float> dst)
    {
        if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
        if (src.Length % channels != 0) throw new ArgumentException("Source must hold whole frames.", nameof(src));
        int frames = src.Length / channels;
        if (dst.Length < frames * 2) throw new ArgumentException("Destination too small.", nameof(dst));

        switch (channels)
        {
            case 1:
                for (int i = 0; i < frames; i++)
                {
                    dst[2 * i] = src[i];
                    dst[2 * i + 1] = src[i];
                }
                break;
            case 2:
                src.CopyTo(dst);
                break;
            case 6:
                for (int i = 0; i < frames; i++)
                {
                    ReadOnlySpan<float> f = src.Slice(i * 6, 6);      // FL FR C LFE SL SR
                    dst[2 * i] = f[0] + Center * f[2] + Center * f[4];
                    dst[2 * i + 1] = f[1] + Center * f[2] + Center * f[5];
                }
                break;
            default:
                for (int i = 0; i < frames; i++)
                {
                    dst[2 * i] = src[i * channels];
                    dst[2 * i + 1] = src[i * channels + 1];
                }
                break;
        }
    }
}
