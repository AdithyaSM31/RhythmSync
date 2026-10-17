using System.Runtime.InteropServices;
using RhythmSync.Platform.Windows;

namespace RhythmSync.Platform.Windows.Tests;

public class PcmConverterTests
{
    private static readonly float[] Samples = { 0f, 1f, -1f, 0.5f, 2f, -3f, float.NaN };

    [Fact]
    public void Int16_scales_clamps_and_silences_NaN()
    {
        var dst = new byte[Samples.Length * 2];
        PcmConverter.ToInt16(Samples, dst);
        Assert.Equal(new short[] { 0, 32767, -32767, 16384, 32767, -32767, 0 }, MemoryMarshal.Cast<byte, short>(dst).ToArray());
    }

    [Fact]
    public void Int24_is_little_endian_three_bytes()
    {
        var dst = new byte[3 * 3];
        PcmConverter.ToInt24(new[] { 1f, -1f, 0f }, dst);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0x7F, 0x01, 0x00, 0x80, 0, 0, 0 }, dst);
    }

    [Fact]
    public void Int32_scales_and_clamps()
    {
        var dst = new byte[Samples.Length * 4];
        PcmConverter.ToInt32(Samples, dst);
        var ints = MemoryMarshal.Cast<byte, int>(dst).ToArray();
        Assert.Equal(0, ints[0]);
        Assert.Equal(int.MaxValue, ints[1]);
        Assert.Equal(-int.MaxValue, ints[2]);
        Assert.Equal(int.MaxValue, ints[4]);
        Assert.Equal(0, ints[6]);
    }

    [Fact]
    public void ScaleToFrames_handles_large_positions_without_overflow()
    {
        // 10 MHz device clock, 30 days of audio at 48 kHz
        ulong freq = 10_000_000;
        ulong pos = 30UL * 24 * 3600 * freq + freq / 2;
        Assert.Equal(30L * 24 * 3600 * 48000 + 24000, WasapiRenderOutput.ScaleToFrames(pos, freq, 48000));
    }

    [Fact]
    public void FromInt16_scales_to_unit_range()
    {
        var src = new byte[6];
        MemoryMarshal.Cast<byte, short>(src.AsSpan()).Fill(0);
        var shorts = MemoryMarshal.Cast<byte, short>(src.AsSpan());
        shorts[0] = 0; shorts[1] = short.MaxValue; shorts[2] = short.MinValue;
        var dst = new float[3];

        PcmConverter.FromInt16(src, dst);

        Assert.Equal(0f, dst[0]);
        Assert.Equal(32767f / 32768f, dst[1]);
        Assert.Equal(-1f, dst[2]);
    }

    [Fact]
    public void FromInt24_sign_extends_little_endian()
    {
        var src = new byte[] { 0xFF, 0xFF, 0x7F, 0x00, 0x00, 0x80, 0x00, 0x00, 0x40, 0, 0, 0 };
        var dst = new float[4];

        PcmConverter.FromInt24(src, dst);

        Assert.Equal(8_388_607f / 8_388_608f, dst[0]);
        Assert.Equal(-1f, dst[1]);
        Assert.Equal(0.5f, dst[2]);
        Assert.Equal(0f, dst[3]);
    }

    [Fact]
    public void FromInt32_scales_to_unit_range()
    {
        var src = new byte[12];
        var ints = MemoryMarshal.Cast<byte, int>(src.AsSpan());
        ints[0] = 0; ints[1] = int.MinValue; ints[2] = 1 << 30;
        var dst = new float[3];

        PcmConverter.FromInt32(src, dst);

        Assert.Equal(0f, dst[0]);
        Assert.Equal(-1f, dst[1]);
        Assert.Equal(0.5f, dst[2]);
    }

    [Fact]
    public void Int16_round_trip_is_within_one_step()
    {
        var original = new[] { 0f, 0.25f, -0.75f, 0.999f };
        var bytes = new byte[original.Length * 2];
        var back = new float[original.Length];

        PcmConverter.ToInt16(original, bytes);
        PcmConverter.FromInt16(bytes, back);

        for (int i = 0; i < original.Length; i++) Assert.Equal(original[i], back[i], 1e-4f);
    }
}
