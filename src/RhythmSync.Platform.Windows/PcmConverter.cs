using System.Runtime.InteropServices;

namespace RhythmSync.Platform.Windows;

/// <summary>
/// Converts between float samples and the integer PCM layouts a shared-mode mix format can use. The float to integer
/// direction clamps and turns NaN into silence; the integer to float direction divides by 2^(bits-1), so full-scale
/// negative is exactly -1.0 and no value exceeds 1.0.
/// </summary>
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

    public static void FromInt16(ReadOnlySpan<byte> src, Span<float> dst)
    {
        var s = MemoryMarshal.Cast<byte, short>(src);
        for (int i = 0; i < dst.Length; i++)
            dst[i] = s[i] * (1f / 32_768f);
    }

    public static void FromInt24(ReadOnlySpan<byte> src, Span<float> dst)
    {
        for (int i = 0; i < dst.Length; i++)
        {
            int v = (src[3 * i] << 8 | src[3 * i + 1] << 16 | src[3 * i + 2] << 24) >> 8;    // sign-extend from bit 23
            dst[i] = v * (1f / 8_388_608f);
        }
    }

    public static void FromInt32(ReadOnlySpan<byte> src, Span<float> dst)
    {
        var s = MemoryMarshal.Cast<byte, int>(src);
        for (int i = 0; i < dst.Length; i++)
            dst[i] = (float)(s[i] * (1.0 / 2_147_483_648.0));
    }

    private static float Sanitize(float x) => float.IsNaN(x) ? 0f : Math.Clamp(x, -1f, 1f);
}
