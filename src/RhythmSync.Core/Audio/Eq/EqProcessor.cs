using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace RhythmSync.Core.Audio.Eq;

/// <summary>
/// The 10-band EQ stage: one RBJ peaking biquad per band (transposed Direct Form II, coefficients and state in
/// <c>double</c>) followed by the automatic preamp. <see cref="SetGains"/> may be called from any thread: it builds
/// an immutable target (gains plus preamp) under a control lock and publishes it with one volatile write.
/// <see cref="Process"/> runs on one audio thread, reads that target once per call, and never allocates or locks.
/// Changes are smoothed per 32-frame sub-block: band gains in dB (tau = 10 ms) with their coefficients interpolated
/// per sample, and the preamp in dB, falling fast (2 ms) and rising slowly (20 ms) so the attenuation always leads
/// the boost. When the curve is Flat and fully settled, <see cref="Process"/> leaves the samples untouched (bit-exact).
/// </summary>
public sealed class EqProcessor : IAudioProcessor
{
    private const int MaxChannels = 2;
    private const int SubBlockFrames = 32;
    private const double GainSeconds = 0.010;
    private const double PreampFallSeconds = 0.002;
    private const double PreampRiseSeconds = 0.020;
    private const double SnapDb = 0.01;
    private const double DenormalLimit = 1e-30;

    /// <summary>What the audio thread moves toward. <see cref="GainsDb"/> is 0 for bands inactive at <see cref="Rate"/>.</summary>
    private sealed record Target(EqGains Gains, int Rate, double[] GainsDb, double PreampDb, double PreampLinear)
    {
        public bool IsBypass { get; } = PreampLinear == 1.0 && Array.TrueForAll(GainsDb, g => g == 0.0);
    }

    private readonly object _control = new();
    private Target _target; // replaced under _control, published with Volatile.Write, read once per Process call
    private int _sampleRate;

    // Rate constants: written by the constructor and SetSampleRate (while no Process call runs), read by Process.
    // The k tables hold the one-pole factor 1 - exp(-n / (tau * rate)) for a sub-block of n = 0..32 frames.
    private readonly double[] _cosW0 = new double[EqBands.Count];
    private readonly double[] _alpha = new double[EqBands.Count];
    private readonly bool[] _active = new bool[EqBands.Count];
    private readonly double[] _kGain = new double[SubBlockFrames + 1];
    private readonly double[] _kPreampFall = new double[SubBlockFrames + 1];
    private readonly double[] _kPreampRise = new double[SubBlockFrames + 1];

    // Audio-thread state (also reset by SetSampleRate). While a band moves, _coefFrom holds its coefficients at the
    // start of the sub-block and _coef those at its end; otherwise _coef alone is used.
    private readonly double[] _curDb = new double[EqBands.Count];
    private double _curPreampDb;
    private double _curPreamp;
    private readonly Biquad[] _coef = new Biquad[EqBands.Count];
    private readonly Biquad[] _coefFrom = new Biquad[EqBands.Count];
    private readonly bool[] _moving = new bool[EqBands.Count];
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
            Smooth(target, n);
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
        double preampDb = EqCurve.PreampDb(gains, sampleRate);
        return new Target(gains, sampleRate, gainsDb, preampDb, Math.Pow(10, preampDb / 20));
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
        for (int n = 0; n <= SubBlockFrames; n++)
        {
            _kGain[n] = 1 - Math.Exp(-n / (GainSeconds * rate));
            _kPreampFall[n] = 1 - Math.Exp(-n / (PreampFallSeconds * rate));
            _kPreampRise[n] = 1 - Math.Exp(-n / (PreampRiseSeconds * rate));
        }
        _curPreampDb = target.PreampDb;
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

    // One smoothing step for a sub-block of `frames` frames: moves the band gains and the preamp to their values
    // at the sub-block's end. Snaps assign the target exactly, so Flat can reach bypass.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Smooth(Target target, int frames)
    {
        double k = _kGain[frames];
        bool anyBandMoving = false;
        for (int band = 0; band < EqBands.Count; band++)
        {
            double goal = target.GainsDb[band], cur = _curDb[band];
            _moving[band] = cur != goal && _active[band];
            if (cur == goal) continue;
            anyBandMoving = true;
            cur += (goal - cur) * k;
            if (Math.Abs(goal - cur) <= SnapDb) cur = goal;
            _curDb[band] = cur;
            if (_active[band])
            {
                _coefFrom[band] = _coef[band];
                _coef[band] = Coefficients(band, cur);
            }
        }

        // In dB, falling fast and rising slowly, so the attenuation leads the band boosts both ways. A rise also
        // waits until no band is moving: a boosted band's output lags its coefficients (its resonance holds energy
        // for a few ms at 1 kHz and tens of ms at 31 Hz), so lifting the preamp earlier briefly overshoots 0 dBFS.
        double goalDb = target.PreampDb, preampDb = _curPreampDb;
        if (preampDb != goalDb && (goalDb < preampDb || !anyBandMoving))
        {
            preampDb += (goalDb - preampDb) * (goalDb < preampDb ? _kPreampFall[frames] : _kPreampRise[frames]);
            if (Math.Abs(goalDb - preampDb) <= SnapDb)
            {
                _curPreampDb = goalDb;
                _curPreamp = target.PreampLinear;
            }
            else
            {
                _curPreampDb = preampDb;
                _curPreamp = Math.Pow(10, preampDb / 20);
            }
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

    // Runs one sub-block of `frames` frames through every active band, then applies the preamp. A moving band's
    // coefficients, and the linear preamp, are interpolated per sample so the last frame lands on the end values.
    // Every interpolated filter is stable: the (a1, a2) stability triangle is convex.
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
                if (_moving[band])
                {
                    Biquad f = _coefFrom[band];
                    double step = 1.0 / frames;
                    double dB0 = (q.B0 - f.B0) * step, dB1 = (q.B1 - f.B1) * step, dB2 = (q.B2 - f.B2) * step;
                    double dA1 = (q.A1 - f.A1) * step, dA2 = (q.A2 - f.A2) * step;
                    for (int i = 0; i < frames; i++)
                    {
                        double t = i + 1;
                        double b0 = f.B0 + dB0 * t, b1 = f.B1 + dB1 * t, b2 = f.B2 + dB2 * t;
                        double a1 = f.A1 + dA1 * t, a2 = f.A2 + dA2 * t;
                        double input = x[i];
                        double y = b0 * input + s1;
                        s1 = b1 * input - a1 * y + s2;
                        s2 = b2 * input - a2 * y;
                        x[i] = y;
                    }
                }
                else
                {
                    for (int i = 0; i < frames; i++)
                    {
                        double input = x[i];
                        double y = q.B0 * input + s1;
                        s1 = q.B1 * input - q.A1 * y + s2;
                        s2 = q.B2 * input - q.A2 * y;
                        x[i] = y;
                    }
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
