using System.Net;
using RhythmSync.App.ViewModels;
using RhythmSync.Core.Clock;
using RhythmSync.Core.Host;

namespace RhythmSync.App.Tests;

/// <summary>The precise offset controls of a client row on the host: the -1/+1 ms steps and the type-in box.</summary>
public class ClientRowViewModelTests
{
    private static readonly Guid Id = Guid.Parse("00000000-0000-0000-0000-00000000000a");

    private sealed class Rig
    {
        public readonly List<(float Volume, double Offset)> Reported = new();
        public readonly ClientRowViewModel Row;

        public Rig(double offset = 0)
        {
            var client = new ConnectedClient(Id, "Kitchen", new IPEndPoint(IPAddress.Loopback, 5000), true, null, 1f, offset);
            Row = new ClientRowViewModel(client, r => Reported.Add((r.Volume, r.OffsetMs)));
            Reported.Clear();
        }

        public void Type(string text)
        {
            Row.OffsetInput = text;
            Row.CommitOffsetInputCommand.Execute(null);
        }
    }

    // ---- -1 / +1 ms --------------------------------------------------------------------------------------------------

    [Fact]
    public void Plus_and_minus_step_the_offset_by_exactly_one_ms()
    {
        var rig = new Rig(offset: 5);

        rig.Row.IncreaseOffsetCommand.Execute(null);
        Assert.Equal(6, rig.Row.OffsetMs);
        rig.Row.DecreaseOffsetCommand.Execute(null);
        rig.Row.DecreaseOffsetCommand.Execute(null);
        Assert.Equal(4, rig.Row.OffsetMs);
        Assert.Equal([6.0, 5.0, 4.0], rig.Reported.Select(r => r.Offset));   // each step is one user change
    }

    [Theory]
    [InlineData(12.4, 1, 13)]     // rounded to a whole ms first, then stepped
    [InlineData(12.4, -1, 11)]
    [InlineData(12.6, 1, 14)]
    [InlineData(12.6, -1, 12)]
    [InlineData(-0.3, 1, 1)]
    [InlineData(-0.3, -1, -1)]
    [InlineData(99.7, 1, 100)]
    public void A_step_rounds_to_a_whole_ms_first(double start, int direction, double expected)
    {
        var rig = new Rig(start);

        (direction > 0 ? rig.Row.IncreaseOffsetCommand : rig.Row.DecreaseOffsetCommand).Execute(null);

        Assert.Equal(expected, rig.Row.OffsetMs);
    }

    [Fact]
    public void Steps_stop_at_plus_and_minus_100()
    {
        var top = new Rig(offset: 100);
        top.Row.IncreaseOffsetCommand.Execute(null);
        Assert.Equal(100, top.Row.OffsetMs);
        Assert.Empty(top.Reported);   // nothing changed, nothing sent

        var bottom = new Rig(offset: -100);
        bottom.Row.DecreaseOffsetCommand.Execute(null);
        Assert.Equal(-100, bottom.Row.OffsetMs);
        Assert.Empty(bottom.Reported);

        var nearTop = new Rig(offset: 99.5);
        nearTop.Row.IncreaseOffsetCommand.Execute(null);
        Assert.Equal(100, nearTop.Row.OffsetMs);
    }

    // ---- the type-in box ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("-13", -13)]
    [InlineData("+5", 5)]
    [InlineData("-12.5", -12.5)]
    [InlineData("0", 0)]
    [InlineData("  7  ", 7)]
    [InlineData("7ms", 7)]
    [InlineData("7 ms", 7)]
    [InlineData(" -3.5 MS ", -3.5)]
    [InlineData("12.34", 12.3)]       // rounds to 0.1 ms
    [InlineData("12.36", 12.4)]
    [InlineData("-12.36", -12.4)]
    [InlineData("0.04", 0)]
    [InlineData("250", 100)]          // out of range clamps
    [InlineData("-250", -100)]
    [InlineData("100.04", 100)]
    public void Typed_values_are_parsed_rounded_and_clamped(string text, double expected)
    {
        var rig = new Rig(offset: 3);

        rig.Type(text);

        Assert.Equal(expected, rig.Row.OffsetMs);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ms")]
    [InlineData("--5")]
    [InlineData("5 5")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1e2")]
    [InlineData("5,5")]
    public void Invalid_text_is_ignored_and_the_box_reverts(string text)
    {
        var rig = new Rig(offset: -13);

        rig.Type(text);

        Assert.Equal(-13, rig.Row.OffsetMs);
        Assert.Equal("-13", rig.Row.OffsetInput);
        Assert.Empty(rig.Reported);
    }

    [Fact]
    public void A_null_box_text_is_invalid_too()
    {
        var rig = new Rig(offset: 4);
        rig.Row.OffsetInput = null!;
        rig.Row.CommitOffsetInputCommand.Execute(null);
        Assert.Equal(4, rig.Row.OffsetMs);
        Assert.Equal("4", rig.Row.OffsetInput);
    }

    [Fact]
    public void Parsing_ignores_the_current_culture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");   // decimal comma
            var rig = new Rig();
            rig.Type("12.5");
            Assert.Equal(12.5, rig.Row.OffsetMs);
            Assert.Equal("12.5", rig.Row.OffsetInput);
            rig.Type("12,5");
            Assert.Equal(12.5, rig.Row.OffsetMs);   // invalid: ignored
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void A_committed_value_is_normalized_in_the_box_and_goes_through_the_user_change_path()
    {
        var rig = new Rig();

        rig.Type(" +12.34 ms");

        Assert.Equal("12.3", rig.Row.OffsetInput);
        Assert.Equal("Offset: +12 ms", rig.Row.OffsetText);
        Assert.Equal((1f, 12.3), Assert.Single(rig.Reported));
    }

    [Fact]
    public void Committing_the_unchanged_value_sends_nothing_but_normalizes_the_box()
    {
        var rig = new Rig(offset: 5);

        rig.Type("+5.00");

        Assert.Equal("5", rig.Row.OffsetInput);
        Assert.Empty(rig.Reported);
    }

    // ---- the box follows the offset -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, "0")]
    [InlineData(12.5, "12.5")]
    [InlineData(-13, "-13")]
    [InlineData(12.3456, "12.3")]
    [InlineData(-0.04, "0")]          // never "-0"
    [InlineData(100, "100")]
    public void The_box_shows_the_offset_with_at_most_one_decimal(double offset, string expected)
    {
        Assert.Equal(expected, new Rig(offset).Row.OffsetInput);
    }

    [Fact]
    public void The_box_follows_the_slider_the_buttons_and_adoption()
    {
        var rig = new Rig();
        var changed = new List<string?>();
        rig.Row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        rig.Row.OffsetMs = 7.5;                       // the slider
        Assert.Equal("7.5", rig.Row.OffsetInput);
        Assert.Contains(nameof(ClientRowViewModel.OffsetInput), changed);

        rig.Row.IncreaseOffsetCommand.Execute(null);  // 7.5 rounds to 8, +1
        Assert.Equal("9", rig.Row.OffsetInput);

        rig.Row.Adopt(0.5f, -42);                     // the host / settings store
        Assert.Equal("-42", rig.Row.OffsetInput);
        Assert.Equal("Offset: -42 ms", rig.Row.OffsetText);

        rig.Row.OffsetMs = 1000;                      // clamped
        Assert.Equal("100", rig.Row.OffsetInput);
    }

    [Fact]
    public void An_unfinished_draft_is_replaced_when_the_offset_changes_elsewhere()
    {
        var rig = new Rig(offset: 1);
        rig.Row.OffsetInput = "-";

        rig.Row.OffsetMs = 20;

        Assert.Equal("20", rig.Row.OffsetInput);
    }

    [Fact]
    public void An_unchanged_adoption_leaves_a_draft_being_typed_alone()
    {
        var rig = new Rig(offset: 5);
        rig.Row.OffsetInput = "1";

        rig.Row.Adopt(1f, 5);   // e.g. the next heartbeat's snapshot: same value

        Assert.Equal("1", rig.Row.OffsetInput);
    }
}
