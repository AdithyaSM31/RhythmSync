namespace RhythmSync.Core;

public static class AudioFormat
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int FrameSamples = 240;
    public const int FrameFloats = 480;
    public const int PcmBytes = 960;
    public const long TicksPerSecond = 10_000_000;
    public const long FrameTicks = 50_000;
}
