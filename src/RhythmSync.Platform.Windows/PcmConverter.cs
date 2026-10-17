using System.Runtime.InteropServices;

namespace RhythmSync.Platform.Windows;

/// <summary>Converts float samples to the integer PCM layouts a shared-mode mix format can use. Clamps; NaN becomes silence.</summary>
internal static class PcmConverter
{
    public static void ToInt16(ReadOnlySpan<float> src, Span<byte> dst)
    {
        var d = MemoryMarshal.Cast<byte, short>(dst);
        for (int i = 0; i < src.Length; i++)
            d[i] = (short)MathF.Round(Sanitize(src[i]) * short.MaxValue);
    }

    public static void ToInt24(ReadOnlySpan<float> src, Span<byte> dst)
    {
        for (int i = 0; i < src.Length; i++)
        {
            int v = (int)MathF.Round(Sanitize(src[i]) * 8_388_607f);
            dst[3 * i] = (byte)v;
            dst[3 * i + 1] = (byte)(v >> 8);
            dst[3 * i + 2] = (byte)(v >> 16);
        }
    }

    public static void ToInt32(ReadOnlySpan<float> src, Span<byte> dst)
    {
        var d = MemoryMarshal.Cast<byte, int>(dst);
        for (int i = 0; i < src.Length; i++)
            d[i] = (int)Math.Round(Sanitize(src[i]) * (double)int.MaxValue);
    }

    private static float Sanitize(float x) => float.IsNaN(x) ? 0f : Math.Clamp(x, -1f, 1f);
}
