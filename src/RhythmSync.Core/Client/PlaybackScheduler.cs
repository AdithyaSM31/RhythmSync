using RhythmSync.Core.Audio;
using RhythmSync.Core.Clock;

namespace RhythmSync.Core.Client;

public enum PlaybackState { WaitingForData, Playing }

public readonly record struct PlaybackStats(double SyncErrorMs, double RatioPpm, long Resyncs, long ConcealedFrames, long LateFrames);

/// <summary>
/// The client's audio-thread render callback. For each output buffer it works out which stream sample must be
/// heard when the buffer's first sample reaches the speaker (device timing → client clock → host clock → jitter
/// buffer mapping), and plays the stream through a cubic resampler whose rate a PI loop trims (within
/// ±<see cref="MaxCorrectionPpm"/>) so that the error stays near zero. Errors beyond <see cref="HardResyncMs"/>
/// cause one hard resync: the current audio fades out, then playback restarts, faded in, at the right position
/// (immediately when behind; once the content is due when ahead). Frames go through a <see cref="Concealer"/>
/// strictly in sequence order. <see cref="Render"/> runs on the audio thread and does not allocate;
/// <see cref="SetOffset"/> and <see cref="GetStats"/> may be called from other threads.
/// </summary>
public sealed class PlaybackScheduler : IRenderCallback
{
    public const double HardResyncMs = 20, MaxCorrectionPpm = 500;

    /// <summary>Proportional gain: rate correction per stream sample of error.</summary>
    public const double Kp = 1e-5;

    /// <summary>Integral gain: rate correction per stream sample of error accumulated once per callback.</summary>
    public const double Ki = 2e-8;

    private const int Channels = AudioFormat.Channels;
    private const int FrameSamples = AudioFormat.FrameSamples;
    private const int FrameFloats = AudioFormat.FrameFloats;
    private const double TicksPerMs = AudioFormat.TicksPerSecond / 1000.0;
    private const double StreamSamplesPerMs = AudioFormat.SampleRate / 1000.0;
    private const double HardResyncSamples = HardResyncMs * StreamSamplesPerMs;
    private const double MaxCorrection = MaxCorrectionPpm * 1e-6;
    private const double MaxIntegral = MaxCorrection / Ki;
    private const int FadeFrames = FrameSamples;
    private const double MaxOffsetMs = 100;
    private const int MinOffsetHeadroomMs = 50;

    private readonly JitterBuffer _buffer;
    private readonly IClockMapping _clock;
    private readonly DspChain _dsp;
    private readonly int _deviceRate;
    private readonly double _nominalStep;
    private readonly int _maxRenderFrames;
    private readonly float[] _staging;          // resampler input window, interleaved stereo
    private readonly float[] _ring;             // processed (concealed) frames, slot = seq mod _ringFrames
    private readonly int _ringFrames;
    private readonly Concealer _concealer = new();

    private long _offsetTicks;                  // written by SetOffset (any thread)
    private volatile PlaybackState _state = PlaybackState.WaitingForData;

    // Audio-thread state.
    private bool _awaitingContent;              // ahead-resync in progress: silent until the content at _readPos is due
    private bool _fadeInPending;
    private double _readPos;                    // stream sample read at the next output frame
    private double _integral;
    private long _firstProcessed;               // stream frames before this are silence (playback (re)started here)
    private long _processedThrough;             // last stream frame passed through the concealer

    // Published for other threads.
    private double _syncErrorMs;
    private double _ratioPpm;
    private double _lastRenderStreamPosition = double.NaN;
    private long _resyncs;
    private long _concealedFrames;

    public PlaybackScheduler(JitterBuffer buffer, IClockMapping clock, int deviceSampleRate, DspChain dsp)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _dsp = dsp ?? throw new ArgumentNullException(nameof(dsp));
        ArgumentOutOfRangeException.ThrowIfLessThan(deviceSampleRate, 1);

        _deviceRate = deviceSampleRate;
        _nominalStep = (double)AudioFormat.SampleRate / deviceSampleRate;
        _maxRenderFrames = deviceSampleRate;    // buffers up to 1 s
        int stagingFrames = (int)Math.Ceiling(_maxRenderFrames * _nominalStep * (1 + MaxCorrection)) + 4;
        _staging = new float[stagingFrames * Channels];
        _ringFrames = stagingFrames / FrameSamples + 4;
        _ring = new float[_ringFrames * FrameFloats];
    }

    /// <summary>
    /// Sets the per-device offset: positive plays later. Clamped to
    /// <c>[max(−100, −(targetLatencyMs − 50)), 100]</c> ms so playback never asks for audio that cannot have arrived.
    /// </summary>
    public void SetOffset(double offsetMs, int targetLatencyMs)
    {
        if (!double.IsFinite(offsetMs)) throw new ArgumentOutOfRangeException(nameof(offsetMs), "Offset must be finite.");
        double lower = Math.Max(-MaxOffsetMs, -(targetLatencyMs - MinOffsetHeadroomMs));
        double clamped = Math.Clamp(offsetMs, lower, MaxOffsetMs);
        Interlocked.Exchange(ref _offsetTicks, (long)Math.Round(clamped * TicksPerMs));
    }

    public double EffectiveOffsetMs => Interlocked.Read(ref _offsetTicks) / TicksPerMs;

    /// <summary>The output device rate this scheduler renders at.</summary>
    public int DeviceSampleRate => _deviceRate;

    public PlaybackState State => _state;

    /// <summary>Stream sample at the first sample of the last rendered buffer; NaN when that buffer was silence.</summary>
    public double LastRenderStreamPosition => Volatile.Read(ref _lastRenderStreamPosition);

    public PlaybackStats GetStats() => new(
        Volatile.Read(ref _syncErrorMs),
        Volatile.Read(ref _ratioPpm),
        Interlocked.Read(ref _resyncs),
        Interlocked.Read(ref _concealedFrames),
        _buffer.Late);

    public void Render(Span<float> stereo, in RenderTiming timing)
    {
        if (stereo.Length % Channels != 0)
            throw new ArgumentException("Buffer must hold whole stereo frames.", nameof(stereo));
        int n = stereo.Length / Channels;
        if (n > _maxRenderFrames)
            throw new ArgumentException($"Buffer exceeds {_maxRenderFrames} frames.", nameof(stereo));
        if (n == 0) return;

        long heardClient = timing.DevicePositionClientTicks + (long)Math.Round(
            (timing.WritePositionFrames - timing.DevicePositionFrames) * (double)AudioFormat.TicksPerSecond / _deviceRate);
        long hostTicks = _clock.ClientToHost(heardClient) - Interlocked.Read(ref _offsetTicks);

        if (!_buffer.TryMapHostTimeToPosition(hostTicks, out double should))
        {
            OutputSilence(stereo);
            return;
        }

        if (_state == PlaybackState.WaitingForData)
        {
            if (!IsAvailable(should))
            {
                ReleaseBefore(should);
                OutputSilence(stereo);
                return;
            }
            Restart(should);
            _state = PlaybackState.Playing;
        }
        else if (_awaitingContent)
        {
            if (should < _readPos)
            {
                ReleaseBefore(should);
                OutputSilence(stereo);
                return;
            }
            Restart(should);
            _awaitingContent = false;
        }

        double startPos = _readPos;
        double err = should - _readPos;         // stream samples; positive = we are behind
        double corr = 0;

        if (err > HardResyncSamples)
        {
            FadeOut(stereo, n);
            Restart(should + n * _nominalStep);
            Interlocked.Increment(ref _resyncs);
        }
        else if (err < -HardResyncSamples)
        {
            FadeOut(stereo, n);
            _integral = 0;
            _awaitingContent = true;
            Interlocked.Increment(ref _resyncs);
        }
        else
        {
            corr = Correction(err);
            Resample(stereo, n, _nominalStep * (1 + corr));
            if (_fadeInPending)
            {
                Concealer.Ramp(stereo[..(Math.Min(n, FadeFrames) * Channels)], 0f, 1f);
                _fadeInPending = false;
            }
        }

        _dsp.Process(stereo, Channels);
        MarkConsumed();

        Volatile.Write(ref _syncErrorMs, err / StreamSamplesPerMs);
        Volatile.Write(ref _ratioPpm, corr * 1e6);
        Volatile.Write(ref _lastRenderStreamPosition, startPos);
        Interlocked.Exchange(ref _concealedFrames, _concealer.ConcealedFrames);
    }

    /// <summary>
    /// PI rate correction for error <paramref name="err"/> (stream samples), clamped to ±<see cref="MaxCorrectionPpm"/>.
    /// Anti-windup: while the output is saturated in the direction the error pushes, the integral is held, so a long
    /// slew (e.g. a sub-resync step in the clock mapping) does not leave it wound up and overshoot once the error is gone.
    /// </summary>
    private double Correction(double err)
    {
        double integral = Math.Clamp(_integral + err, -MaxIntegral, MaxIntegral);
        double raw = Kp * err + Ki * integral;
        bool windingUp = (raw > MaxCorrection && err > 0) || (raw < -MaxCorrection && err < 0);
        if (windingUp)
            raw = Kp * err + Ki * _integral;
        else
            _integral = integral;
        return Math.Clamp(raw, -MaxCorrection, MaxCorrection);
    }

    private bool IsAvailable(double streamSample)
    {
        if (!(streamSample >= 0)) return false;
        long seq = FrameOf(streamSample);
        return seq <= uint.MaxValue && _buffer.Contains((uint)seq);
    }

    /// <summary>Restarts playback at <paramref name="streamSample"/>: fresh concealer history, fade-in, PI integral cleared.</summary>
    private void Restart(double streamSample)
    {
        _readPos = streamSample;
        _firstProcessed = FrameOf(streamSample);
        _processedThrough = _firstProcessed - 1;
        _concealer.Reset();
        _fadeInPending = true;
        _integral = 0;
    }

    /// <summary>Plays up to <see cref="FadeFrames"/> frames of the current audio ramped 1→0, then silence.</summary>
    private void FadeOut(Span<float> stereo, int n)
    {
        int fadeFloats = Math.Min(n, FadeFrames) * Channels;
        Span<float> fade = stereo[..fadeFloats];
        Resample(fade, fadeFloats / Channels, _nominalStep);
        Concealer.Ramp(fade, 1f, 0f);
        stereo[fadeFloats..].Clear();
    }

    private void Resample(Span<float> output, int frames, double step)
    {
        long first = (long)Math.Floor(_readPos) - 1;   // one frame of history for the cubic taps
        double position = _readPos - first;
        int needed = CubicResampler.RequiredInputFrames(position, step, frames);
        ProcessThrough(FloorDiv(first + needed - 1, FrameSamples));
        FillStaging(first, needed);
        _readPos = first + CubicResampler.Process(_staging.AsSpan(0, needed * Channels), position, step, output);
    }

    /// <summary>Passes every not-yet-processed frame up to <paramref name="lastSeq"/> through the concealer, in order.</summary>
    private void ProcessThrough(long lastSeq)
    {
        while (_processedThrough < lastSeq)
        {
            long seq = ++_processedThrough;
            Span<float> slot = RingSlot(seq);
            bool present = seq >= 0 && seq <= uint.MaxValue && _buffer.TryReadFrame((uint)seq, slot);
            _concealer.Process(slot, present);
        }
    }

    /// <summary>Copies stream samples [<paramref name="first"/>, first + count) into the staging buffer.</summary>
    private void FillStaging(long first, int count)
    {
        int written = 0;
        long sample = first;
        while (written < count)
        {
            long seq = FloorDiv(sample, FrameSamples);
            int offset = (int)(sample - seq * FrameSamples);
            int length = Math.Min(FrameSamples - offset, count - written);
            Span<float> dst = _staging.AsSpan(written * Channels, length * Channels);
            if (seq < _firstProcessed) dst.Clear();
            else RingSlot(seq).Slice(offset * Channels, length * Channels).CopyTo(dst);
            written += length;
            sample += length;
        }
    }

    private Span<float> RingSlot(long seq)
    {
        int slot = (int)(((seq % _ringFrames) + _ringFrames) % _ringFrames);
        return _ring.AsSpan(slot * FrameFloats, FrameFloats);
    }

    /// <summary>Frees frames that lie wholly before the resampler's history tap.</summary>
    private void MarkConsumed()
    {
        long through = FrameOf(_readPos) - 2;
        if (through >= 0 && through <= uint.MaxValue)
            _buffer.MarkConsumedThrough((uint)through);
    }

    /// <summary>
    /// While silent: frees frames that are already past due at <paramref name="should"/>. Without this, frames that
    /// piled up while nothing was rendered (e.g. the output was down for over a second) would fill the buffer, every
    /// newer frame would be refused, and the needed frame would never arrive.
    /// </summary>
    private void ReleaseBefore(double should)
    {
        long through = FrameOf(should) - 2;   // as in MarkConsumed: keep the frame before for the history tap
        if (through >= 0 && through <= uint.MaxValue)
            _buffer.MarkConsumedThrough((uint)through);
    }

    private void OutputSilence(Span<float> stereo)
    {
        stereo.Clear();
        Volatile.Write(ref _lastRenderStreamPosition, double.NaN);
    }

    private static long FrameOf(double streamSample) => (long)Math.Floor(streamSample / FrameSamples);

    private static long FloorDiv(long a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);
}
