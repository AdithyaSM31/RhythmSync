using RhythmSync.Platform.Windows;

namespace RhythmSync.Platform.Windows.Tests;

public class ChannelMapperTests
{
    [Fact]
    public void Stereo_to_six_channels_puts_LR_first_and_zeros_rest()
    {
        float[] stereo = { 0.1f, 0.2f, 0.3f, 0.4f };
        float[] dst = new float[12];
        Array.Fill(dst, 9f); // stale data must be overwritten

        ChannelMapper.StereoToDevice(stereo, dst, 6);

        Assert.Equal(new[] { 0.1f, 0.2f, 0f, 0f, 0f, 0f, 0.3f, 0.4f, 0f, 0f, 0f, 0f }, dst);
    }

    [Fact]
    public void Stereo_to_mono_averages()
    {
        float[] stereo = { 1f, 0f, 0.5f, -0.5f };
        float[] dst = new float[2];

        ChannelMapper.StereoToDevice(stereo, dst, 1);

        Assert.Equal(0.5f, dst[0]);
        Assert.Equal(0f, dst[1]);
    }

    [Fact]
    public void Stereo_to_stereo_copies()
    {
        float[] stereo = { 0.1f, 0.2f, 0.3f, 0.4f };
        float[] dst = new float[4];
        ChannelMapper.StereoToDevice(stereo, dst, 2);
        Assert.Equal(stereo, dst);
    }
}
