using System.Text;
using RhythmSync.App.Services;
using RhythmSync.Core.Protocol;

namespace RhythmSync.App.Tests;

public class BeaconNameTests
{
    private static int Bytes(string s) => Encoding.UTF8.GetByteCount(s);

    [Fact]
    public void A_short_name_is_unchanged()
    {
        Assert.Equal("Living room", BeaconName.Fit("Living room"));
    }

    [Fact]
    public void The_name_is_trimmed()
    {
        Assert.Equal("Den", BeaconName.Fit("  Den \t"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_name_falls_back(string? name)
    {
        Assert.Equal("RhythmSync", BeaconName.Fit(name));
    }

    [Fact]
    public void A_name_of_exactly_64_bytes_is_kept()
    {
        string name = new('a', 64);
        Assert.Equal(name, BeaconName.Fit(name));
    }

    [Fact]
    public void A_long_ascii_name_is_cut_to_64_bytes()
    {
        string cut = BeaconName.Fit(new string('a', 200));
        Assert.Equal(64, Bytes(cut));
    }

    [Fact]
    public void Two_byte_characters_are_cut_on_a_character_boundary()
    {
        // 'é' is 2 bytes: 40 of them are 80 bytes, so 32 fit.
        string cut = BeaconName.Fit(new string('é', 40));
        Assert.Equal(new string('é', 32), cut);
        Assert.Equal(64, Bytes(cut));
    }

    [Fact]
    public void A_character_that_would_straddle_the_limit_is_dropped_whole()
    {
        // 63 ASCII bytes then 'é' (2 bytes): the 'é' does not fit.
        string cut = BeaconName.Fit(new string('a', 63) + "éxyz");
        Assert.Equal(new string('a', 63), cut);
    }

    [Fact]
    public void Three_byte_characters_are_cut_on_a_character_boundary()
    {
        // '日' is 3 bytes: 30 of them are 90 bytes; 21 fit (63 bytes).
        string cut = BeaconName.Fit(new string('日', 30));
        Assert.Equal(new string('日', 21), cut);
        Assert.Equal(63, Bytes(cut));
    }

    [Fact]
    public void Surrogate_pairs_are_never_split()
    {
        // '😀' is 4 bytes (a surrogate pair): 20 of them are 80 bytes; 16 fit.
        string cut = BeaconName.Fit(string.Concat(Enumerable.Repeat("😀", 20)));
        Assert.Equal(string.Concat(Enumerable.Repeat("😀", 16)), cut);
        Assert.Equal(64, Bytes(cut));
    }

    [Fact]
    public void Mixed_multibyte_names_always_fit_and_stay_valid_text()
    {
        string name = string.Concat(Enumerable.Repeat("Café 日本 😀 ", 12));
        string cut = BeaconName.Fit(name);

        Assert.True(Bytes(cut) <= 64);
        Assert.Equal(cut, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(cut)));   // round-trips: no lone surrogates
        Assert.StartsWith(cut, name.Trim());
    }

    [Fact]
    public void A_fitted_name_is_accepted_by_the_beacon_serializer()
    {
        string name = string.Concat(Enumerable.Repeat("é日😀", 40));
        var buffer = new byte[PacketSerializer.HeaderBytes + 4 + PacketSerializer.MaxBeaconNameBytes];

        int length = PacketSerializer.WriteBeacon(buffer, new BeaconPacket(1, 47801, 1, BeaconName.Fit(name)));

        Assert.True(PacketSerializer.TryReadBeacon(buffer.AsSpan(0, length), out var beacon));
        Assert.StartsWith(beacon!.Name, name);
    }

    [Fact]
    public void An_unfitted_long_multibyte_name_is_what_the_serializer_rejects()
    {
        var buffer = new byte[PacketSerializer.HeaderBytes + 4 + PacketSerializer.MaxBeaconNameBytes];
        Assert.Throws<ArgumentException>(() => PacketSerializer.WriteBeacon(buffer, new BeaconPacket(1, 47801, 1, new string('日', 30))));
    }
}
