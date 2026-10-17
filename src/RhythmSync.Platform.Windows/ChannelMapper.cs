namespace RhythmSync.Platform.Windows;

/// <summary>Maps interleaved stereo float audio onto a device's channel layout.</summary>
public static class ChannelMapper
{
    /// <summary>
    /// Writes <paramref name="stereo"/> (interleaved L,R) into <paramref name="dst"/> as <paramref name="deviceChannels"/>-channel
    /// interleaved audio. L and R go to channels 0 and 1, all other channels are zero, and a mono device gets (L+R)/2.
    /// Exactly <c>frames * deviceChannels</c> samples of <paramref name="dst"/> are written.
    /// </summary>
    public static void StereoToDevice(ReadOnlySpan<float> stereo, Span<float> dst, int deviceChannels)
    {
        if (deviceChannels < 1) throw new ArgumentOutOfRangeException(nameof(deviceChannels));
        if ((stereo.Length & 1) != 0) throw new ArgumentException("Stereo buffer must hold whole frames.", nameof(stereo));
        int frames = stereo.Length / 2;
        if (dst.Length < frames * deviceChannels) throw new ArgumentException("Destination too small.", nameof(dst));

        switch (deviceChannels)
        {
            case 1:
                for (int i = 0; i < frames; i++)
                    dst[i] = (stereo[2 * i] + stereo[2 * i + 1]) * 0.5f;
                break;
            case 2:
                stereo.CopyTo(dst);
                break;
            default:
                for (int i = 0; i < frames; i++)
                {
                    var frame = dst.Slice(i * deviceChannels, deviceChannels);
                    frame[0] = stereo[2 * i];
                    frame[1] = stereo[2 * i + 1];
                    frame[2..].Clear();
                }
                break;
        }
    }
}
