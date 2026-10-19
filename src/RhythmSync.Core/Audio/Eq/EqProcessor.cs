using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace RhythmSync.Core.Audio.Eq;

/// <summary>
/// The 10-band EQ stage: one RBJ peaking biquad per band (transposed Direct Form II, coefficients and state in
/// <c>double</c>) followed by the automatic preamp. <see cref="SetGains"/> may be called from any thread: it builds
/// an immutable target (gains plus preamp) under a control lock and publishes it with one volatile write.
/// <see cref="Process"/> runs on one audio thread, reads that target once per call, and never allocates or locks.
/// Gain and preamp changes are smoothed per 32-frame sub-block (one-pole, tau = 10 ms), so they do not click.
/// When the curve is Flat and fully settled, <see cref="Process"/> leaves the samples untouched (bit-exact).
/// </summary>
public sealed class EqProcessor : IAudioProcessor
{
    private const int MaxChannels = 2;
    private const int SubBlockFrames = 32;
    private const double SmoothingSeconds = 0.010;
    private const double SnapDb = 0.01;
    private const double DenormalLimit = 1e-30;

    // A linear preamp within this fraction of its target is within 0.01 dB of it.
    private static readonly double PreampSnapRatio = Math.Pow(10, SnapDb / 20) - 1;

    /// <summary>What the audio thread moves toward. <see cref="GainsDb"/> is 0 for bands inactive at <see cref="Rate"/>.</summary>
    private sealed record Target(EqGains Gains, int Rate, double[] GainsDb, double PreampLinear)
    {
        public bool IsBypass { get; } = PreampLinear == 1.0 && Array.TrueForAll(GainsDb, g => g == 0.0);
    }

    private readonly object _control = new();
    private Target _target; // replaced under _control, published with Volatile.Write, read once per Process call
    private int _sampleRate;

    // Rate constants: written by the constructor and SetSampleRate (while no Process call runs), read by Process.
    private readonly double[] _cosW0 = new double[EqBands.Count];
    private readonly double[] _alpha = new double[EqBands.Count];
    private readonly bool[] _active = new bool[EqBands.Count];
    private double _k;

    // Audio-thread state (also reset by SetSampleRate).
    private readonly double[] _curDb = new double[EqBands.Count];
    private double _curPreamp;
    private readonly Biquad[] _coef = new Biquad[EqBands.Count];
    private readonly double[] _state = new double[MaxChannels * EqBands.Count * 2];
    private readonly double[] _scratch = new double[SubBlockFrames];
    private bool _bypassing; // true while Process passes audio through; the state is zero then

    public EqProcessor(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        _target = BuildTarget(EqGains.Flat, sampleRate);
        Retune(_target);
    }

    /// <summary>The last gains given to <see cref="SetGains"/> (Flat initially).</summary>
    public EqGains Gains => Volatile.Read(ref _target).Gains;

    /// <summary>The output sample rate the filters are tuned for.</summary>
    public int SampleRate => Volatile.Read(ref _sampleRate);

    /// <summary>Sets new band gains. Any thread; the audio thread ramps to them from its next sub-block.</summary>
    public void SetGains(EqGains gains)
    {
        ArgumentNullException.ThrowIfNull(gains);
        lock (_control)
        {
            Volatile.Write(ref _target, BuildTarget(gains, _sampleRate));
        }
    }

    /// <summary>
    /// Retunes the filters for <paramref name="sampleRate"/>, zeroes the filter state and jumps straight to the
    /// target (no ramp). Call only while no <see cref="Process"/> call can run; it may race <see cref="SetGains"/>.
    /// </summary>
    public void SetSampleRate(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        lock (_control)
        {
            Retune(BuildTarget(_target.Gains, sampleRate));
        }
    }

    // The audio-path methods are compiled once, fully optimised, on their first call. After that the real-time
    // thread never runs tier-0 code and never takes a tiering transition (call counting, on-stack replacement).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Process(Span<float> interleaved, int channels)
    {
        if (channels is < 1 or > MaxChannels) ThrowChannelsOutOfRange(channels);

        Target target = Volatile.Read(ref _target);
        if (target.IsBypass && IsAtRest())
        {
            if (!_bypassing)
            {
                Array.Clear(_state);
                _bypassing = true;
            }
            return;
        }
        _bypassing = false;

        int frames = interleaved.Length / channels;
        for (int start = 0; start < frames; start += SubBlockFrames)
        {
            int n = Math.Min(SubBlockFrames, frames - start);
            double preampFrom = _curPreamp;
            Smooth(target);
            Filter(interleaved.Slice(start * channels, n * channels), channels, n, preampFrom, _curPreamp);
        }

        for (int i = 0; i < _state.Length; i++)
        {
            if (Math.Abs(_state[i]) < DenormalLimit) _state[i] = 0;
        }
    }

    // Test hooks.
    internal (EqGains Gains, int SampleRate, double PreampLinear) CurrentTarget
    {
        get
        {
            var t = Volatile.Read(ref _target);
            return (t.Gains, t.Rate, t.PreampLinear);
        }
    }

    internal bool StateIsZero => Array.TrueForAll(_state, s => s == 0.0);

    internal double CurrentGainDb(int band) => _curDb[band];

    private static Target BuildTarget(EqGains gains, int sampleRate)
    {
        var gainsDb = new double[EqBands.Count];
        for (int band = 0; band < EqBands.Count; band++)
            gainsDb[band] = EqCurve.IsActive(band, sampleRate) ? gains[band] : 0.0;
        return new Target(gains, sampleRate, gainsDb, Math.Pow(10, EqCurve.PreampDb(gains, sampleRate) / 20));
    }

    // Caller holds _control (or is the constructor), and no Process call runs.
    private void Retune(Target target)
    {
        int rate = target.Rate;
        for (int band = 0; band < EqBands.Count; band++)
        {
            double w0 = 2 * Math.PI * EqBands.CentersHz[band] / rate;
            _cosW0[band] = Math.Cos(w0);
            _alpha[band] = Math.Sin(w0) / (2 * EqBands.Q);
            _active[band] = EqCurve.IsActive(band, rate);
            _curDb[band] = target.GainsDb[band];
            if (_active[band]) _coef[band] = Coefficients(band, _curDb[band]);
        }
        _k = 1 - Math.Exp(-SubBlockFrames / (SmoothingSeconds * rate));
        _curPreamp = target.PreampLinear;
        Array.Clear(_state);
        _bypassing = true;
        Volatile.Write(ref _sampleRate, rate);
        Volatile.Write(ref _target, target);
    }

    [DoesNotReturn]
    private static void ThrowChannelsOutOfRange(int channels) =>
        throw new ArgumentOutOfRangeException(nameof(channels), channels, "The EQ processes 1 or 2 channels.");

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private bool IsAtRest()
    {
        if (_curPreamp != 1.0) return false;
        foreach (double db in _curDb)
        {
            if (db != 0.0) return false;
        }
        return true;
    }

    // One smoothing step, at the start of a sub-block. Snaps assign the target exactly, so Flat can reach bypass.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Smooth(Target target)
    {
        double k = _k;
        for (int band = 0; band < EqBands.Count; band++)
        {
            double goal = target.GainsDb[band], cur = _curDb[band];
            if (cur == goal) continue;
            cur += (goal - cur) * k;
            if (Math.Abs(goal - cur) <= SnapDb) cur = goal;
            _curDb[band] = cur;
            if (_active[band]) _coef[band] = Coefficients(band, cur);
        }

        double goalPreamp = target.PreampLinear, preamp = _curPreamp;
        if (preamp != goalPreamp)
        {
            preamp += (goalPreamp - preamp) * k;
            if (Math.Abs(goalPreamp - preamp) <= goalPreamp * PreampSnapRatio) preamp = goalPreamp;
            _curPreamp = preamp;
        }
    }

    // RBJ peaking filter from the precomputed cos w0 and alpha; the same arithmetic as EqCurve.PeakingCoefficients.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private Biquad Coefficients(int band, double gainDb)
    {
        double a = Math.Pow(10, gainDb / 40);
        double alpha = _alpha[band], cosW0 = _cosW0[band];
        double a0 = 1 + alpha / a;
        return new Biquad(
            (1 + alpha * a) / a0,
            -2 * cosW0 / a0,
            (1 - alpha * a) / a0,
            -2 * cosW0 / a0,
            (1 - alpha / a) / a0);
    }

    // Runs one sub-block of `frames` frames through every active band, then applies the preamp, ramped linearly
    // from preampFrom to preampTo so the last frame lands on preampTo.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Filter(Span<float> block, int channels, int frames, double preampFrom, double preampTo)
    {
        Span<double> x = _scratch.AsSpan(0, frames);
        for (int c = 0; c < channels; c++)
        {
            for (int i = 0; i < frames; i++) x[i] = block[i * channels + c];

            int s = c * EqBands.Count * 2;
            for (int band = 0; band < EqBands.Count; band++, s += 2)
            {
                if (!_active[band]) continue;
                Biquad q = _coef[band];
                double s1 = _state[s], s2 = _state[s + 1];
                for (int i = 0; i < frames; i++)
                {
                    double input = x[i];
                    double y = q.B0 * input + s1;
                    s1 = q.B1 * input - q.A1 * y + s2;
                    s2 = q.B2 * input - q.A2 * y;
                    x[i] = y;
                }
                _state[s] = s1;
                _state[s + 1] = s2;
            }

            if (preampFrom == preampTo)
            {
                for (int i = 0; i < frames; i++) block[i * channels + c] = (float)(x[i] * preampTo);
            }
            else
            {
                double delta = preampTo - preampFrom;
                for (int i = 0; i < frames; i++)
                {
                    double p = i == frames - 1 ? preampTo : preampFrom + delta * (i + 1) / frames;
                    block[i * channels + c] = (float)(x[i] * p);
                }
            }
        }
    }
}
