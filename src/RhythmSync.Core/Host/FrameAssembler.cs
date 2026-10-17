using RhythmSync.Core.Audio;

namespace RhythmSync.Core.Host;

/// <summary>
/// Turns captured device audio (interleaved stereo float at the device rate) into 5 ms, 48 kHz, 16-bit frames,
/// each stamped with <c>playAt = capture session time of its first sample + target latency</c>.
/// </summary>
/// <remarks>
/// 48 kHz output frame <c>k</c> starts at device position <c>origin + k * step</c> (<c>step = deviceRate / 48000</c>),
/// where <c>origin</c> is the position of the first frame of the first <see cref="Push"/>. The cubic resampler
/// needs one device frame before and two after each position, so the newest ~2 device frames are consumed by the
/// next push. Not thread-safe except for <see cref="TargetLatencyMs"/>: call <see cref="Push"/> from one thread.
/// Allocation-free after construction.
/// </remarks>
public sealed class FrameAssembler
{
    private const int Channels = AudioFormat.Channels;

    /// <summary>Gaps or overlaps larger than this many device frames re-anchor the stream.</summary>
    private const long DiscontinuityToleranceFrames = 1;

    private const long TicksPerMs = 10_000;

    private readonly CaptureTimestamper _timestamper;
    private readonly IFrameSink _sink;
    private readonly double _step;
    private readonly int _maxChunkFrames;
    private readonly float[] _history;          // device frames at positions _bufStart .. _bufStart + _bufFrames - 1
    private readonly float[] _frameFloat = new float[AudioFormat.FrameFloats];
    private readonly short[] _frameShort = new short[AudioFormat.FrameFloats];
    private readonly Random _dither;

    private volatile int _targetLatencyMs;

    private bool _anchored;
    private long _expectedNext;                 // device position expected at the start of the next Push
    private long _origin;                       // device position of output frame 0 of the current segment
    private long _k;                            // 48 kHz output sample-frame index of the next packet within the segment
    private long _bufStart;
    private int _bufFrames;
    private uint _seq;

    public FrameAssembler(int deviceSampleRate, CaptureTimestamper timestamper, IFrameSink sink, int targetLatencyMs, int ditherSeed = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(deviceSampleRate, 0);
        ArgumentNullException.ThrowIfNull(timestamper);
        ArgumentNullException.ThrowIfNull(sink);

        _timestamper = timestamper;
        _sink = sink;
        _targetLatencyMs = targetLatencyMs;
        _dither = new Random(ditherSeed);
        _step = (double)deviceSampleRate / AudioFormat.SampleRate;

        _maxChunkFrames = deviceSampleRate;     // 1 s of device audio per chunk
        int tail = (int)Math.Ceiling(AudioFormat.FrameSamples * _step) + 8;
        _history = new float[(_maxChunkFrames + tail) * Channels];
    }

    /// <summary>Target latency added to every frame's capture time. May be changed from another thread.</summary>
    public int TargetLatencyMs
    {
        get => _targetLatencyMs;
        set => _targetLatencyMs = value;
    }

    /// <summary>Feeds captured audio. <paramref name="devicePositionOfFirstFrame"/> is the device stream position of its first frame.</summary>
    public void Push(ReadOnlySpan<float> interleavedStereo, long devicePositionOfFirstFrame)
    {
        int totalFrames = interleavedStereo.Length / Channels;
        if (totalFrames == 0) return;

        if (!_anchored || Math.Abs(devicePositionOfFirstFrame - _expectedNext) > DiscontinuityToleranceFrames)
            Anchor(devicePositionOfFirstFrame);

        _expectedNext = devicePositionOfFirstFrame + totalFrames;

        int offset = 0;
        while (offset < totalFrames)
        {
            int n = Math.Min(_maxChunkFrames, totalFrames - offset);
            interleavedStereo.Slice(offset * Channels, n * Channels).CopyTo(_history.AsSpan(_bufFrames * Channels));
            _bufFrames += n;
            offset += n;
            EmitAvailableFrames();
            Trim();
        }
    }

    // Starts a new segment at 'position'. Buffer index 0 is a silent stand-in for the frame before the stream
    // start, so the resampler's one-frame lookbehind exists; at the integer origin it never affects the output.
    private void Anchor(long position)
    {
        _anchored = true;
        _origin = position;
        _k = 0;
        _bufStart = position - 1;
        _history.AsSpan(0, Channels).Clear();
        _bufFrames = 1;
    }

    private void EmitAvailableFrames()
    {
        while (true)
        {
            double rel = (_origin - _bufStart) + _k * _step;
            if (CubicResampler.RequiredInputFrames(rel, _step, AudioFormat.FrameSamples) > _bufFrames) return;

            CubicResampler.Process(_history.AsSpan(0, _bufFrames * Channels), rel, _step, _frameFloat);
            ConvertToInt16();

            long playAt = _timestamper.SessionTimeOf(_origin + _k * _step) + _targetLatencyMs * TicksPerMs;
            _sink.OnFrame(_seq++, playAt, _frameShort);
            _k += AudioFormat.FrameSamples;
        }
    }

    private void ConvertToInt16()
    {
        for (int i = 0; i < _frameFloat.Length; i++)
        {
            float sample = _frameFloat[i];
            if (float.IsNaN(sample)) sample = 0f;
            double v = sample * 32767.0 + (_dither.NextDouble() - _dither.NextDouble());
            v = Math.Clamp(Math.Round(v), -32767.0, 32767.0);
            _frameShort[i] = (short)v;
        }
    }

    // Drops frames the next output frame no longer needs (keeps one frame of lookbehind).
    private void Trim()
    {
        double rel = (_origin - _bufStart) + _k * _step;
        int drop = (int)Math.Floor(rel) - 1;
        if (drop <= 0) return;
        if (drop > _bufFrames) drop = _bufFrames;

        Array.Copy(_history, drop * Channels, _history, 0, (_bufFrames - drop) * Channels);
        _bufStart += drop;
        _bufFrames -= drop;
    }
}
