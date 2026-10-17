using NAudio.Wave;

namespace RhythmSync.Platform.Windows;

/// <summary>Sample layouts of a shared-mode device mix format that the render and capture paths support.</summary>
internal enum SampleKind { Float32, Int16, Int24, Int32 }

internal static class MixFormat
{
    /// <exception cref="NotSupportedException">The format is not 32-bit float or 16/24/32-bit integer PCM.</exception>
    public static SampleKind Classify(WaveFormat format)
    {
        WaveFormatEncoding encoding = format.Encoding;
        if (format is WaveFormatExtensible ext)
            encoding = ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT ? WaveFormatEncoding.IeeeFloat
                     : ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_PCM ? WaveFormatEncoding.Pcm
                     : WaveFormatEncoding.Unknown;

        return (encoding, format.BitsPerSample) switch
        {
            (WaveFormatEncoding.IeeeFloat, 32) => SampleKind.Float32,
            (WaveFormatEncoding.Pcm, 16) => SampleKind.Int16,
            (WaveFormatEncoding.Pcm, 24) => SampleKind.Int24,
            (WaveFormatEncoding.Pcm, 32) => SampleKind.Int32,
            _ => throw new NotSupportedException(
                $"Unsupported device mix format ({encoding}, {format.BitsPerSample}-bit); supported: 32-bit float and 16/24/32-bit PCM."),
        };
    }

    public static int BytesPerSample(SampleKind kind) => kind switch
    {
        SampleKind.Int16 => 2,
        SampleKind.Int24 => 3,
        _ => 4,
    };
}
