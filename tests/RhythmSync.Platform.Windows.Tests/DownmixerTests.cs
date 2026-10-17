using RhythmSync.Platform.Windows;

namespace RhythmSync.Platform.Windows.Tests;

public class DownmixerTests
{
    private const float Tolerance = 1e-6f;

    [Fact]
    public void Downmix_5_1_follows_ITU_coefficients()
    {
        // Frame layout FL FR C LFE SL SR; the LFE is dropped.
        float[] src =
        {
            0.10f, 0.20f, 0.30f, 0.90f, 0.40f, 0.50f,      // frame 0
            -0.25f, 0.00f, 0.50f, -0.80f, 0.10f, -0.60f,   // frame 1
        };
        var dst = new float[4];

        Downmixer.ToStereo(src, 6, dst);

        Assert.Equal(0.10f + 0.707f * 0.30f + 0.707f * 0.40f, dst[0], Tolerance);
        Assert.Equal(0.20f + 0.707f * 0.30f + 0.707f * 0.50f, dst[1], Tolerance);
        Assert.Equal(-0.25f + 0.707f * 0.50f + 0.707f * 0.10f, dst[2], Tolerance);
        Assert.Equal(0.00f + 0.707f * 0.50f + 0.707f * -0.60f, dst[3], Tolerance);
    }

    [Fact]
    public void Downmix_5_1_ignores_LFE()
    {
        var withLfe = new float[] { 0.1f, 0.2f, 0.3f, 1.0f, 0.4f, 0.5f };
        var noLfe = new float[] { 0.1f, 0.2f, 0.3f, 0.0f, 0.4f, 0.5f };
        var a = new float[2];
        var b = new float[2];

        Downmixer.ToStereo(withLfe, 6, a);
        Downmixer.ToStereo(noLfe, 6, b);

        Assert.Equal(b, a);
    }

    [Fact]
    public void Downmix_mono_duplicates()
    {
        var dst = new float[6];
        Downmixer.ToStereo(new[] { 0.25f, -0.5f, 1f }, 1, dst);
        Assert.Equal(new[] { 0.25f, 0.25f, -0.5f, -0.5f, 1f, 1f }, dst);
    }

    [Fact]
    public void Downmix_stereo_copies()
    {
        var src = new[] { 0.1f, 0.2f, 0.3f, 0.4f };
        var dst = new float[4];
        Downmixer.ToStereo(src, 2, dst);
        Assert.Equal(src, dst);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(8)]
    public void Downmix_other_layouts_take_first_two_channels(int channels)
    {
        var src = new float[2 * channels];
        for (int i = 0; i < src.Length; i++) src[i] = i + 1;
        var dst = new float[4];

        Downmixer.ToStereo(src, channels, dst);

        Assert.Equal(new float[] { 1, 2, channels + 1, channels + 2 }, dst);
    }

    [Fact]
    public void Downmix_writes_only_the_frames_in_src_and_validates_arguments()
    {
        var dst = new float[8];
        Array.Fill(dst, 9f);
        Downmixer.ToStereo(new[] { 1f, 2f }, 2, dst);
        Assert.Equal(new[] { 1f, 2f, 9f, 9f, 9f, 9f, 9f, 9f }, dst);

        Assert.Throws<ArgumentOutOfRangeException>(() => Downmixer.ToStereo(new float[4], 0, new float[4]));
        Assert.Throws<ArgumentException>(() => Downmixer.ToStereo(new float[5], 2, new float[8]));    // partial frame
        Assert.Throws<ArgumentException>(() => Downmixer.ToStereo(new float[6], 2, new float[4]));    // dst too small
    }
}
