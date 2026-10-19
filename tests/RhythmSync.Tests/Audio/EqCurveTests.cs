using RhythmSync.Core.Audio.Eq;

namespace RhythmSync.Tests.Audio;

public class EqCurveTests
{
    private static EqGains RandomGrid(Random rng)
    {
        var g = new float[EqBands.Count];
        for (int i = 0; i < g.Length; i++) g[i] = rng.Next(-24, 25) * 0.5f;
        return EqGains.Create(g);
    }

    private static IEnumerable<double> LogPoints(double fromHz, double toHz, int count)
    {
        double ratio = Math.Log(toHz / fromHz);
        for (int i = 0; i < count; i++) yield return fromHz * Math.Exp(ratio * i / (count - 1));
    }

    [Fact]
    public void Flat_response_is_zero_everywhere()
    {
        foreach (double hz in new[] { 20.0, 1000.0, 15000.0 })
            Assert.InRange(EqCurve.ResponseDb(EqGains.Flat, 48000, hz), -1e-9, 1e-9);
    }

    [Fact]
    public void Each_band_at_plus_12_peaks_near_12_dB_at_its_centre()
    {
        for (int band = 0; band < EqBands.Count; band++)
        {
            if (!EqCurve.IsActive(band, 48000)) continue;
            var gains = EqGains.Flat.WithBand(band, 12);
            double db = EqCurve.ResponseDb(gains, 48000, EqBands.CentersHz[band]);
            Assert.InRange(db, 11.9, 12.1);
        }
    }

    [Fact]
    public void PeakDb_matches_a_dense_sweep()
    {
        var rng = new Random(7);
        for (int n = 0; n < 20; n++)
        {
            var gains = RandomGrid(rng);
            double dense = LogPoints(20, 20000, 20000).Max(hz => EqCurve.ResponseDb(gains, 48000, hz));
            Assert.InRange(EqCurve.PeakDb(gains, 48000), dense - 0.05, dense + 0.05);
        }
    }

    [Fact]
    public void All_bands_at_plus_12_needs_about_minus_18_4_dB_preamp()
    {
        var all12 = EqGains.Create(Enumerable.Repeat(12f, EqBands.Count).ToArray());
        Assert.InRange(EqCurve.PreampDb(all12, 48000), -18.5, -18.3);
    }

    [Fact]
    public void Preamped_response_never_exceeds_0_dB()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var gains = RandomGrid(new Random(seed));
            double preamp = EqCurve.PreampDb(gains, 48000);
            foreach (double hz in LogPoints(20, 20000, 2000))
                Assert.True(EqCurve.ResponseDb(gains, 48000, hz) + preamp <= 1e-6, $"seed {seed} at {hz:0.0} Hz: {EqCurve.ResponseDb(gains, 48000, hz) + preamp}");
        }
    }

    [Fact]
    public void Cut_only_curves_need_no_preamp()
    {
        var cut = EqGains.Create(Enumerable.Repeat(-6f, EqBands.Count).ToArray());
        Assert.Equal(0.0, EqCurve.PreampDb(cut, 48000));
    }

    [Fact]
    public void PreampDb_is_deterministic()
    {
        var gains = EqPresets.All[1].Gains;
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(EqCurve.PreampDb(gains, 48000)),
            BitConverter.DoubleToInt64Bits(EqCurve.PreampDb(gains, 48000)));
    }

    [Fact]
    public void IsActive_turns_off_bands_at_or_above_0_45_of_the_rate()
    {
        Assert.True(EqCurve.IsActive(7, 16000));
        Assert.False(EqCurve.IsActive(8, 16000));
        Assert.False(EqCurve.IsActive(9, 16000));
        for (int band = 0; band < EqBands.Count; band++) Assert.True(EqCurve.IsActive(band, 44100));
    }

    [Fact]
    public void A_16kHz_rate_ignores_inactive_bands_and_stays_finite()
    {
        var gains = EqGains.Flat.WithBand(9, 12).WithBand(8, 12);
        Assert.Equal(0.0, EqCurve.ResponseDb(gains, 16000, 7000));
        Assert.True(double.IsFinite(EqCurve.PeakDb(gains, 16000)));
        Assert.Equal(0.0, EqCurve.PreampDb(gains, 16000));
    }

    [Fact]
    public void Peaking_coefficients_at_zero_gain_pass_the_input_through()
    {
        var c = EqCurve.PeakingCoefficients(1000, EqBands.Q, 0, 48000);
        Assert.Equal(1.0, c.B0, 12);
        Assert.Equal(c.B1, c.A1, 12);
        Assert.Equal(c.B2, c.A2, 12);
    }

    [Fact]
    public void Presets_are_in_range_on_the_grid_and_named()
    {
        foreach (var (name, gains) in EqPresets.All)
        {
            for (int i = 0; i < EqBands.Count; i++)
            {
                Assert.InRange(gains[i], -12f, 12f);
                Assert.Equal(0f, gains[i] * 2 - MathF.Round(gains[i] * 2));
            }
            Assert.Equal(name, EqPresets.NameOf(gains));
        }
        Assert.Null(EqPresets.NameOf(EqGains.Flat.WithBand(0, 0.5)));
        Assert.Equal(new[] { "Flat", "Laptop speakers", "Bass boost", "Treble cut" }, EqPresets.All.Select(p => p.Name));
        Assert.Equal("Custom", EqPresets.CustomName);
    }

    [Fact]
    public void Preset_values_match_the_spec()
    {
        Assert.Equal(new float[] { -9, -6, -1, 2, 1, 0, 0, 1, 2, 1 }, EqPresets.All[1].Gains.ToArray());
        Assert.Equal(new float[] { 6, 5, 4, 2, 0, 0, 0, 0, 0, 0 }, EqPresets.All[2].Gains.ToArray());
        Assert.Equal(new float[] { 0, 0, 0, 0, 0, 0, -1, -3, -5, -6 }, EqPresets.All[3].Gains.ToArray());
    }
}
