using RhythmSync.Core.Audio;

namespace RhythmSync.Platform.Windows;

/// <summary>Feeds a device-sized buffer to an <see cref="IRenderCallback"/> in bounded, whole-frame chunks.</summary>
internal static class RenderChunker
{
    /// <summary>
    /// Renders <paramref name="stereo"/> (interleaved, even length) in chunks of at most <paramref name="maxChunkFrames"/> frames.
    /// Each chunk's <see cref="RenderTiming.WritePositionFrames"/> is <paramref name="writePosition"/> plus the frames before it.
    /// </summary>
    public static void Render(IRenderCallback cb, Span<float> stereo, int maxChunkFrames,
        long devicePosition, long devicePositionClientTicks, long writePosition)
    {
        if ((stereo.Length & 1) != 0) throw new ArgumentException("Buffer must hold whole stereo frames.", nameof(stereo));
        int totalFrames = stereo.Length / 2;
        for (int done = 0; done < totalFrames; )
        {
            int n = Math.Min(totalFrames - done, maxChunkFrames);
            cb.Render(stereo.Slice(done * 2, n * 2),
                new RenderTiming(devicePosition, devicePositionClientTicks, writePosition + done));
            done += n;
        }
    }
}
