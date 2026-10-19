using System.Diagnostics;
using System.Runtime.InteropServices;
using RhythmSync.Core.Audio.Eq;
using Xunit.Abstractions;

namespace RhythmSync.Tests.Audio;

/// <summary>
/// The EQ tests run alone, after the parallel tests. Under parallel load, GC.GetAllocatedBytesForCurrentThread
/// occasionally charges a few KB to the measuring thread that the measured code never allocated, which made
/// Process_does_not_allocate flaky. Running alone also keeps these CPU-heavy, buffer-heavy tests away from the
/// assembly's other allocation tests.
/// </summary>
[CollectionDefinition("EqProcessor", DisableParallelization = true)]
public sealed class EqProcessorCollection
{
}

[Collection("EqProcessor")]
public class EqProcessorTests
{
    private const int Rate = 48000;
    private const int Buffer = 480;

    private readonly ITestOutputHelper _output;

    public EqProcessorTests(ITestOutputHelper output) => _output = output;

    private static EqGains Preset(string name) => EqPresets.All.Single(p => p.Name == name).Gains;

    /// <summary>A sine of <paramref name="amp"/> at <paramref name="hz"/>, the same on every channel.</summary>
    private static float[] Sine(double hz, int rate, double seconds, float amp, int channels = 1)
    {
        int frames = (int)Math.Round(seconds * rate);
        var buf = new float[frames * channels];
        for (int n = 0; n < frames; n++)
        {
            float s = (float)(amp * Math.Sin(2 * Math.PI * hz * n / rate));
            for (int c = 0; c < channels; c++) buf[n * channels + c] = s;
        }
        return buf;
    }

    private static float[] Noise(int seed, int samples, float amp)
    {
        var rng = new Random(seed);
        var buf = new float[samples];
        for (int i = 0; i < buf.Length; i++) buf[i] = (float)((rng.NextDouble() * 2 - 1) * amp);
        return buf;
    }

    /// <summary>
    /// Level in dB of the <paramref name="hz"/> component, by a single-bin DFT over the largest whole number of
    /// periods that fits in the last second of a mono signal (so a non-integer cycle count does not leak).
    /// </summary>
    private static double LevelDb(ReadOnlySpan<float> mono, double hz, int rate)
    {
        int periods = (int)Math.Floor(hz);
        int n = (int)Math.Round(periods * rate / hz);
        var tail = mono[^n..];
        double w = 2 * Math.PI * hz / rate, re = 0, im = 0;
        for (int i = 0; i < n; i++)
        {
            re += tail[i] * Math.Cos(w * i);
            im += tail[i] * Math.Sin(w * i);
        }
        return 20 * Math.Log10(2 * Math.Sqrt(re * re + im * im) / n);
    }

    private static void ProcessInBuffers(EqProcessor eq, float[] audio, int channels, int framesPerCall = Buffer)
    {
        int frames = audio.Length / channels;
        for (int start = 0; start < frames; start += framesPerCall)
        {
            int n = Math.Min(framesPerCall, frames - start);
            eq.Process(audio.AsSpan(start * channels, n * channels), channels);
        }
    }

    private static void AssertBitEqual(float[] expected, float[] actual) =>
        Assert.True(MemoryMarshal.Cast<float, int>(expected).SequenceEqual(MemoryMarshal.Cast<float, int>(actual)),
            "Outputs differ bit for bit.");

    [Fact]
    public void Flat_is_bit_exact()
    {
        var input = Noise(1, 2 * Rate, 1f);
        var output = (float[])input.Clone();
        var eq = new EqProcessor(Rate);
        ProcessInBuffers(eq, output, 2);
        AssertBitEqual(input, output);

        // Back to Flat after a curve: once settled, bypass is bit-exact again.
        eq.SetGains(Preset("Laptop speakers"));
        ProcessInBuffers(eq, Noise(2, Rate, 0.5f), 2);
        eq.SetGains(EqGains.Flat);
        ProcessInBuffers(eq, Noise(3, Rate, 0.5f), 2);
        Assert.True(eq.StateIsZero); // zeroed on entering bypass, so leaving it later starts clean
        output = (float[])input.Clone();
        ProcessInBuffers(eq, output, 2);
        AssertBitEqual(input, output);
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    public void Each_band_measures_the_expected_level(int rate)
    {
        var failures = new List<string>();
        var output = new float[3 * rate];
        for (int band = 0; band < EqBands.Count; band++)
        {
            if (!EqCurve.IsActive(band, rate)) continue;
            double hz = EqBands.CentersHz[band];
            var input = Sine(hz, rate, 3, 0.5f);
            double inputDb = LevelDb(input, hz, rate);
            foreach (double gainDb in new[] { 12.0, -12.0, 6.0 })
            {
                var gains = EqGains.Flat.WithBand(band, gainDb);
                var eq = new EqProcessor(rate);
                eq.SetGains(gains);
                input.CopyTo(output, 0);
                ProcessInBuffers(eq, output, 1);

                double measured = LevelDb(output, hz, rate) - inputDb;
                double expected = EqCurve.ResponseDb(gains, rate, hz) + EqCurve.PreampDb(gains, rate);
                if (Math.Abs(measured - expected) > 0.1)
                    failures.Add($"band {band} {gainDb:+0;-0} dB @ {rate}: measured {measured:F3}, expected {expected:F3}");
            }
        }
        Assert.Empty(failures);
    }

    private static double MaxSecondDifference(float[] audio, int channels, int fromFrame, int toFrame)
    {
        double max = 0;
        for (int c = 0; c < channels; c++)
        {
            for (int n = Math.Max(fromFrame, 2); n < toFrame; n++)
            {
                double d = audio[n * channels + c] - 2.0 * audio[(n - 1) * channels + c] + audio[(n - 2) * channels + c];
                max = Math.Max(max, Math.Abs(d));
            }
        }
        return max;
    }

    /// <summary>
    /// Plays a 1 kHz sine at 0.25 in 480-frame buffers, applying each change at its time. Around every change, the
    /// largest second difference in the 200 ms after it must stay within 1.5x the larger of the settled 200 ms before
    /// it and the 200 ms from 1 s after it.
    /// </summary>
    private float[] CheckClickFree(List<string> failures, params (double AtSeconds, EqGains Gains)[] changes)
    {
        const int channels = 2;
        var audio = Sine(1000, Rate, changes[^1].AtSeconds + 1.2, 0.25f, channels);
        var eq = new EqProcessor(Rate);
        int frames = audio.Length / channels, next = 0;
        for (int start = 0; start < frames; start += Buffer)
        {
            while (next < changes.Length && start >= changes[next].AtSeconds * Rate) eq.SetGains(changes[next++].Gains);
            int n = Math.Min(Buffer, frames - start);
            eq.Process(audio.AsSpan(start * channels, n * channels), channels);
        }

        const int window = Rate / 5;
        foreach (var (atSeconds, gains) in changes)
        {
            int at = (int)(atSeconds * Rate);
            double before = MaxSecondDifference(audio, channels, at - window, at);
            double during = MaxSecondDifference(audio, channels, at, at + window);
            double after = MaxSecondDifference(audio, channels, at + Rate, at + Rate + window);
            double ratio = during / Math.Max(before, after);
            string line = $"Change to [{string.Join(' ', gains.ToArray())}] at {atSeconds} s: ratio {ratio:F3} (during {during:G4}, before {before:G4}, after {after:G4}).";
            _output.WriteLine(line);
            if (ratio > 1.5) failures.Add(line);
        }
        return audio;
    }

    [Fact]
    public void Gain_steps_are_click_free()
    {
        var failures = new List<string>();

        // Flat -> +12 at 1 kHz: the first second runs in bypass (bit-exact), so this also covers leaving bypass.
        var audio = CheckClickFree(failures, (1.0, EqGains.Flat.WithBand(5, 12)));
        AssertBitEqual(Sine(1000, Rate, 1.0, 0.25f, 2), audio[..(2 * Rate)]);

        CheckClickFree(failures, (1.0, Preset("Laptop speakers")), (2.5, EqGains.Flat));
        // High bands move b0 a lot for a small gain step: these clicked when coefficients stepped per sub-block.
        CheckClickFree(failures, (1.0, EqGains.Flat.WithBand(9, 12)));
        CheckClickFree(failures, (1.0, EqGains.Flat.WithBand(8, 12)));
        CheckClickFree(failures, (1.0, Preset("Treble cut")));
        CheckClickFree(failures, (1.0, Preset("Laptop speakers")), (2.5, Preset("Treble cut")));
        Assert.Empty(failures);
    }

    private static EqGains AllBands(float db) => EqGains.Create(Enumerable.Repeat(db, EqBands.Count).ToArray());

    // Frequency of the curve's highest point, from a dense log sweep.
    private static double PeakHz(EqGains gains, int rate)
    {
        double best = 0, bestDb = double.NegativeInfinity, from = 20, to = Math.Min(20000, 0.45 * rate);
        for (int i = 0; i < 4000; i++)
        {
            double hz = from * Math.Pow(to / from, i / 3999.0);
            double db = EqCurve.ResponseDb(gains, rate, hz);
            if (db > bestDb) (best, bestDb) = (hz, db);
        }
        return best;
    }

    [Theory]
    [InlineData(5)]  // +12 at 1 kHz
    [InlineData(-1)] // every band at +12
    [InlineData(0)]  // +12 at 31 Hz: the slowest resonance
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]  // +12 at 16 kHz
    public void Preset_and_band_ramps_never_exceed_full_scale(int band)
    {
        var curve = band < 0 ? AllBands(12) : EqGains.Flat.WithBand(band, 12);
        double hz = PeakHz(curve, Rate);
        // Full-scale sine at the curve's peak: Flat for 0.5 s, the curve for 1 s, then Flat again for 1 s.
        var audio = Sine(hz, Rate, 2.5, 1f, 2);
        var eq = new EqProcessor(Rate);
        for (int start = 0, frames = audio.Length / 2; start < frames; start += Buffer)
        {
            if (start == Rate / 2) eq.SetGains(curve);
            if (start == 3 * Rate / 2) eq.SetGains(EqGains.Flat);
            eq.Process(audio.AsSpan(2 * start, 2 * Math.Min(Buffer, frames - start)), 2);
        }

        int up = 0, down = 0;
        double peakUp = 0, peakDown = 0;
        for (int i = 0; i < audio.Length; i++)
        {
            double m = Math.Abs(audio[i]);
            if (i < 3 * Rate) peakUp = Math.Max(peakUp, m); else peakDown = Math.Max(peakDown, m);
            if (m > 1.0 + 1e-4) { if (i < 3 * Rate) up++; else down++; }
        }
        string summary = $"{hz:F1} Hz: {up} samples over full scale ramping up (peak {peakUp:F7}), {down} ramping back (peak {peakDown:F7}).";
        _output.WriteLine(summary);
        Assert.True(up == 0 && down == 0, summary);
    }

    [Fact]
    public void Smoothing_speed_does_not_depend_on_call_size()
    {
        double GainAfter10Ms(int framesPerCall)
        {
            var eq = new EqProcessor(Rate);
            eq.SetGains(EqGains.Flat.WithBand(3, 12));
            ProcessInBuffers(eq, Sine(250, Rate, 0.01, 0.25f, 2), 2, framesPerCall);
            return eq.CurrentGainDb(3);
        }

        double reference = GainAfter10Ms(480); // 15 full sub-blocks: 12 (1 - e^-1)
        Assert.Equal(12 * (1 - Math.Exp(-1)), reference, 1e-9);
        foreach (int framesPerCall in new[] { 1, 7, 33 })
            Assert.Equal(reference, GainAfter10Ms(framesPerCall), 1e-9);
    }

    [Fact]
    public void Process_does_not_allocate()
    {
        var eq = new EqProcessor(Rate);
        var a = Preset("Laptop speakers");
        var b = Preset("Bass boost");
        var source = Sine(1000, Rate, (double)Buffer / Rate, 0.25f, 2);
        var work = new float[source.Length];

        void Prepare(int call)
        {
            if (call % 10 == 0) eq.SetGains(call / 10 % 2 == 0 ? a : b);
            source.CopyTo(work, 0);
        }

        for (int call = 0; call < 200; call++)
        {
            Prepare(call);
            eq.Process(work, 2);
        }

        long allocated = 0;
        for (int call = 0; call < 1000; call++)
        {
            Prepare(call); // SetGains allocates by design; only Process is measured.
            long before = GC.GetAllocatedBytesForCurrentThread();
            eq.Process(work, 2);
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Silence_flushes_state_to_zero()
    {
        var eq = new EqProcessor(Rate);
        eq.SetGains(Preset("Laptop speakers"));
        ProcessInBuffers(eq, Sine(31.25, Rate, 0.5, 0.5f, 2), 2);
        Assert.False(eq.StateIsZero);

        var silence = new float[2 * Rate * 2];
        ProcessInBuffers(eq, silence, 2);

        int nonZero = 0;
        foreach (float s in silence.AsSpan(2 * Rate)) if (s != 0f) nonZero++;
        Assert.Equal(0, nonZero);
        Assert.True(eq.StateIsZero);
    }

    [Fact]
    public void SetSampleRate_retunes_and_resets()
    {
        var gains = EqGains.Flat.WithBand(5, 12);
        var eq = new EqProcessor(48000);
        eq.SetGains(gains);
        ProcessInBuffers(eq, Sine(1000, 48000, 0.01, 0.5f, 2), 2); // one buffer: state set, gain mid-ramp
        Assert.False(eq.StateIsZero);
        Assert.True(eq.CurrentGainDb(5) < 12);

        eq.SetSampleRate(44100);

        Assert.Equal(44100, eq.SampleRate);
        Assert.Equal(44100, eq.CurrentTarget.SampleRate);
        Assert.Equal(gains, eq.CurrentTarget.Gains);
        Assert.Equal(Math.Pow(10, EqCurve.PreampDb(gains, 44100) / 20), eq.CurrentTarget.PreampLinear);
        Assert.True(eq.StateIsZero);
        Assert.Equal(12, eq.CurrentGainDb(5)); // no ramp after a rate change

        var input = Sine(1000, 44100, 3, 0.5f);
        var output = (float[])input.Clone();
        ProcessInBuffers(eq, output, 1, 441);
        double measured = LevelDb(output, 1000, 44100) - LevelDb(input, 1000, 44100);
        double expected = EqCurve.ResponseDb(gains, 44100, 1000) + EqCurve.PreampDb(gains, 44100);
        Assert.InRange(measured, expected - 0.1, expected + 0.1);
    }

    [Fact]
    public async Task Gains_are_published_as_one_snapshot()
    {
        var a = Preset("Bass boost");
        var b = Preset("Treble cut");
        var eq = new EqProcessor(Rate);
        eq.SetGains(b);
        double preampB = eq.CurrentTarget.PreampLinear;
        eq.SetGains(a);
        double preampA = eq.CurrentTarget.PreampLinear;

        using var started = new ManualResetEventSlim();
        var writer = Task.Factory.StartNew(() =>
        {
            var clock = Stopwatch.StartNew();
            started.Set();
            for (bool flip = false; clock.ElapsedMilliseconds < 200; flip = !flip) eq.SetGains(flip ? a : b);
        }, TaskCreationOptions.LongRunning);
        started.Wait();

        int torn = 0, reads = 0;
        for (; reads < 100_000 || !writer.IsCompleted; reads++)
        {
            var t = eq.CurrentTarget;
            bool isA = t.Gains == a && t.PreampLinear == preampA;
            bool isB = t.Gains == b && t.PreampLinear == preampB;
            if (!isA && !isB) torn++;
        }
        await writer;
        Assert.Equal(0, torn);
    }

    [Fact]
    public void Smoothing_reaches_target_within_100_ms()
    {
        var eq = new EqProcessor(Rate);
        eq.SetGains(EqGains.Flat.WithBand(3, 12));
        var audio = Sine(250, Rate, 0.1, 0.25f, 2);

        eq.Process(audio.AsSpan(0, 2 * Buffer), 2);
        Assert.InRange(eq.CurrentGainDb(3), 0.1, 11); // moving, not jumping

        eq.Process(audio.AsSpan(2 * Buffer), 2);
        Assert.InRange(eq.CurrentGainDb(3), 12 - 0.01, 12 + 0.01);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Channels_outside_1_to_2_throw(int channels)
    {
        var eq = new EqProcessor(Rate);
        Assert.Throws<ArgumentOutOfRangeException>(() => eq.Process(new float[96], channels));
    }

    [Fact]
    public async Task Concurrent_SetGains_and_SetSampleRate_end_with_a_preamp_for_the_final_rate()
    {
        // The two threads make their calls in lock step, released together by a barrier, so every one of the 1 000
        // pairs is a race. The barrier's post-phase action checks the result while both threads are parked.
        var eq = new EqProcessor(48000);
        var presets = EqPresets.All.Select(p => p.Gains).ToArray();
        var failures = new List<string>();
        using var step = new Barrier(2, b =>
        {
            var target = eq.CurrentTarget;
            double expectedPreamp = Math.Pow(10, EqCurve.PreampDb(eq.Gains, eq.SampleRate) / 20);
            if (target.SampleRate != eq.SampleRate || target.PreampLinear != expectedPreamp)
                failures.Add($"call {b.CurrentPhaseNumber}: target rate {target.SampleRate}, rate {eq.SampleRate}, preamp {target.PreampLinear} vs {expectedPreamp}");
        });

        // A thread that leaves (normally or by throwing) drops out of the barrier, so the other never waits forever.
        Task Race(Action call) => Task.Factory.StartNew(() =>
        {
            try
            {
                for (int i = 0; i < 1000; i++)
                {
                    call();
                    step.SignalAndWait();
                }
            }
            finally
            {
                step.RemoveParticipant();
            }
        }, TaskCreationOptions.LongRunning);

        var gainsRng = new Random(1);
        var rateRng = new Random(2);
        await Task.WhenAll(
            Race(() => eq.SetGains(presets[gainsRng.Next(presets.Length)])),
            Race(() => eq.SetSampleRate(rateRng.Next(2) == 0 ? 44100 : 48000)));

        Assert.Empty(failures);
        var final = eq.CurrentTarget;
        Assert.Equal(eq.SampleRate, final.SampleRate);
        Assert.Equal(Math.Pow(10, EqCurve.PreampDb(eq.Gains, eq.SampleRate) / 20), final.PreampLinear);
    }

    [Fact]
    public void A_16kHz_device_ignores_the_top_bands_and_stays_finite()
    {
        const int rate = 16000;
        var allUp = EqGains.Create(Enumerable.Repeat(12f, EqBands.Count).ToArray());
        var topBandsAtZero = allUp.WithBand(8, 0).WithBand(9, 0);
        Assert.True(double.IsFinite(EqCurve.PeakDb(allUp, rate)));

        var input = Noise(4, 2 * rate * 2, 0.5f);
        var withTop = (float[])input.Clone();
        var withoutTop = (float[])input.Clone();
        var eqWithTop = new EqProcessor(rate);
        eqWithTop.SetGains(allUp);
        ProcessInBuffers(eqWithTop, withTop, 2, 160);
        var eqWithoutTop = new EqProcessor(rate);
        eqWithoutTop.SetGains(topBandsAtZero);
        ProcessInBuffers(eqWithoutTop, withoutTop, 2, 160);

        Assert.All(withTop, s => Assert.True(float.IsFinite(s) && Math.Abs(s) <= 1f, $"sample {s}"));
        AssertBitEqual(withoutTop, withTop);
    }

    [Fact]
    public void Steady_state_output_does_not_depend_on_how_buffers_are_split()
    {
        const int frames = 4800;
        var gains = EqGains.Flat.WithBand(1, 6).WithBand(5, 6).WithBand(8, 6);
        var settle = Noise(5, 2 * Rate, 0.5f);
        var block = Noise(6, 2 * frames, 0.5f);

        float[] Run(int[] callFrames, bool emptyCalls)
        {
            var eq = new EqProcessor(Rate);
            eq.SetGains(gains);
            ProcessInBuffers(eq, (float[])settle.Clone(), 2);
            var output = (float[])block.Clone();
            for (int frame = 0, call = 0; frame < frames; call++)
            {
                int n = Math.Min(callFrames[call % callFrames.Length], frames - frame);
                eq.Process(output.AsSpan(2 * frame, 2 * n), 2);
                if (emptyCalls) eq.Process(Span<float>.Empty, 2);
                frame += n;
            }
            return output;
        }

        var reference = Run([frames], emptyCalls: false);
        foreach (var split in new[] { new[] { 1 }, new[] { 31 }, new[] { 33 }, new[] { 1023 }, new[] { 1, 31, 33, 1023 } })
            AssertBitEqual(reference, Run(split, emptyCalls: true));
    }
}
