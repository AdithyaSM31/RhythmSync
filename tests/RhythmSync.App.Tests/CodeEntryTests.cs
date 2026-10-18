using RhythmSync.App.Controls;

namespace RhythmSync.App.Tests;

/// <summary>The pure four-slot pairing-code logic behind PinBox.</summary>
public class CodeEntryTests
{
    [Fact]
    public void Accepts_only_ascii_digits()
    {
        var entry = new CodeEntry();
        entry.Type('4');
        entry.Type('a');
        entry.Type('７');   // fullwidth 7
        entry.Type('2');
        Assert.Equal("42", entry.Code);
    }

    [Fact]
    public void Typing_advances_focus()
    {
        var entry = new CodeEntry();
        Assert.Equal(0, entry.Focus);
        foreach (var c in "4729")
            entry.Type(c);

        Assert.Equal("4729", entry.Code);
        Assert.Equal(3, entry.Focus);
        Assert.True(entry.IsComplete);
    }

    [Fact]
    public void Backspace_on_empty_slot_moves_back()
    {
        var entry = new CodeEntry();
        entry.Type('4');
        entry.Type('7');
        Assert.Equal(2, entry.Focus);

        entry.Backspace();
        Assert.Equal("4", entry.Code);
        Assert.Equal(1, entry.Focus);

        entry.Backspace();
        Assert.Equal("", entry.Code);
        Assert.Equal(0, entry.Focus);

        entry.Backspace();   // nothing left to clear
        Assert.Equal("", entry.Code);
        Assert.Equal(0, entry.Focus);
    }

    [Fact]
    public void Backspace_on_a_filled_last_slot_clears_it_in_place()
    {
        var entry = new CodeEntry();
        entry.Paste("4729");
        Assert.Equal(3, entry.Focus);

        entry.Backspace();
        Assert.Equal("472", entry.Code);
        Assert.Equal(3, entry.Focus);
        Assert.False(entry.IsComplete);
    }

    [Theory]
    [InlineData("47-29", "4729")]
    [InlineData("123456", "1234")]
    [InlineData("ab", "")]
    [InlineData("１２３４", "")]
    public void Paste_keeps_first_four_ascii_digits(string pasted, string expected)
    {
        var entry = new CodeEntry();
        entry.Type('9');
        entry.Paste(pasted);
        Assert.Equal(expected, entry.Code);
        Assert.Equal(Math.Min(expected.Length, 3), entry.Focus);
    }

    [Fact]
    public void Set_from_binding_filters_like_paste()
    {
        var entry = new CodeEntry();
        entry.Set("9x9");
        Assert.Equal("99", entry.Code);
        Assert.Equal(2, entry.Focus);

        entry.Set(null);
        Assert.Equal("", entry.Code);
        Assert.Equal(0, entry.Focus);
    }

    [Fact]
    public void Arrows_move_focus_only_within_the_filled_range()
    {
        var entry = new CodeEntry();
        entry.Paste("47");
        Assert.Equal(2, entry.Focus);

        entry.MoveRight();   // slot 2 is the first empty slot; focus cannot go past it
        Assert.Equal(2, entry.Focus);

        entry.MoveLeft();
        entry.MoveLeft();
        Assert.Equal(0, entry.Focus);
        entry.MoveLeft();
        Assert.Equal(0, entry.Focus);
        Assert.Equal("47", entry.Code);

        entry.MoveRight();
        Assert.Equal(1, entry.Focus);
    }

    [Fact]
    public void Typing_over_a_filled_slot_replaces_it_and_advances()
    {
        var entry = new CodeEntry();
        entry.Paste("4729");
        entry.MoveLeft();
        entry.MoveLeft();   // slot 1
        entry.Type('8');
        Assert.Equal("4829", entry.Code);
        Assert.Equal(2, entry.Focus);

        entry.MoveRight();
        entry.Type('1');   // last slot already filled: replaced, focus stays on it
        Assert.Equal("4821", entry.Code);
        Assert.Equal(3, entry.Focus);
    }

    [Fact]
    public void Backspace_in_the_middle_closes_the_gap()
    {
        var entry = new CodeEntry();
        entry.Paste("4729");
        entry.MoveLeft();
        entry.MoveLeft();   // slot 1
        entry.Backspace();
        Assert.Equal("429", entry.Code);
        Assert.Equal(1, entry.Focus);
    }

    [Fact]
    public void FocusAt_is_clamped_to_the_first_empty_slot()
    {
        var entry = new CodeEntry();
        entry.Paste("47");
        entry.FocusAt(3);
        Assert.Equal(2, entry.Focus);
        entry.FocusAt(0);
        Assert.Equal(0, entry.Focus);
        entry.FocusAt(-5);
        Assert.Equal(0, entry.Focus);
    }
}
