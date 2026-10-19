using RhythmSync.Core.Audio.Eq;

namespace RhythmSync.Tests.Audio;

public class EqGainsTests
{
    [Fact]
    public void Flat_is_ten_zeros_and_IsFlat()
    {
        Assert.True(EqGains.Flat.IsFlat);
        Assert.Equal(new float[10], EqGains.Flat.ToArray());
        for (int i = 0; i < EqBands.Count; i++) Assert.Equal(0f, EqGains.Flat[i]);
    }

    [Fact]
    public void Equal_gains_are_equal_with_equal_hash_codes()
    {
        float[] a = { 1f, -2.5f, 0f, 3f, 4.5f, -6f, 7f, 8f, -9f, 12f };
        var x = EqGains.Create(a);
        var y = EqGains.Create(a.ToArray());

        Assert.True(x == y);
        Assert.False(x != y);
        Assert.True(x.Equals(y));
        Assert.True(x.Equals((object)y));
        Assert.Equal(x.GetHashCode(), y.GetHashCode());
        Assert.False(x == x.WithBand(0, 2));
        Assert.False(x.Equals(null));
    }

    [Fact]
    public void Create_clamps_to_12_dB_and_rounds_to_half_dB()
    {
        float[] input = { 13f, -20f, 2.26f, 2.24f, 0.25f, -0.25f, 0.1f, -0.1f, 11.9f, -2.26f };
        float[] expected = { 12f, -12f, 2.5f, 2f, 0.5f, -0.5f, 0f, 0f, 12f, -2.5f };

        var eq = EqGains.Create(input);

        Assert.Equal(expected, eq.ToArray());
        Assert.Equal(EqGains.Flat, EqGains.Create(new float[] { -0.1f, 0.1f, 0, 0, 0, 0, 0, 0, 0, 0 }));
    }

    [Fact]
    public void Create_throws_on_wrong_length_or_non_finite()
    {
        Assert.Throws<ArgumentException>(() => EqGains.Create(new float[9]));
        Assert.Throws<ArgumentException>(() => EqGains.Create(new float[11]));
        var bad = new float[10];
        bad[4] = float.NaN;
        Assert.Throws<ArgumentException>(() => EqGains.Create(bad));
    }

    [Fact]
    public void TryCreate_null_is_flat()
    {
        Assert.True(EqGains.TryCreate(null, out var eq));
        Assert.Equal(EqGains.Flat, eq);
    }

    [Fact]
    public void TryCreate_rejects_wrong_length_and_non_finite()
    {
        Assert.False(EqGains.TryCreate(new float[9], out _));
        Assert.False(EqGains.TryCreate(new float[11], out _));
        foreach (float bad in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            var gains = new float[10];
            gains[7] = bad;
            Assert.False(EqGains.TryCreate(gains, out _));
        }
    }

    [Fact]
    public void TryCreate_accepts_valid_input_and_clamps()
    {
        var gains = new float[10];
        gains[0] = 20f;
        Assert.True(EqGains.TryCreate(gains, out var eq));
        Assert.Equal(12f, eq[0]);
    }

    [Fact]
    public void FromStored_turns_invalid_input_into_flat()
    {
        Assert.Equal(EqGains.Flat, EqGains.FromStored(null));
        Assert.Equal(EqGains.Flat, EqGains.FromStored(new float[3]));
        var nan = new float[10];
        nan[0] = float.NaN;
        Assert.Equal(EqGains.Flat, EqGains.FromStored(nan));

        var valid = new float[10];
        valid[2] = 3f;
        Assert.Equal(3f, EqGains.FromStored(valid)[2]);
    }

    [Fact]
    public void WithBand_changes_one_band_and_snaps_it()
    {
        var eq = EqGains.Flat.WithBand(3, 2.3);

        Assert.Equal(2.5f, eq[3]);
        for (int i = 0; i < EqBands.Count; i++)
            if (i != 3) Assert.Equal(0f, eq[i]);
        Assert.True(EqGains.Flat.IsFlat);
        Assert.False(eq.IsFlat);
    }

    [Fact]
    public void WithBand_rejects_out_of_range_band_and_non_finite_gain()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EqGains.Flat.WithBand(-1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => EqGains.Flat.WithBand(10, 1));
        Assert.Throws<ArgumentException>(() => EqGains.Flat.WithBand(0, double.NaN));
    }

    [Fact]
    public void ToArray_returns_a_copy()
    {
        var eq = EqGains.Flat;
        var arr = eq.ToArray();
        arr[0] = 5f;

        Assert.Equal(0f, eq[0]);
        Assert.True(eq.IsFlat);
    }

    [Fact]
    public void Band_centres_and_labels_match_the_spec()
    {
        Assert.Equal(10, EqBands.CentersHz.Count);
        Assert.Equal(31.25, EqBands.CentersHz[0]);
        Assert.Equal(16000.0, EqBands.CentersHz[9]);
        Assert.Equal(
            new[] { "31", "63", "125", "250", "500", "1k", "2k", "4k", "8k", "16k" },
            EqBands.Labels);
    }
}
