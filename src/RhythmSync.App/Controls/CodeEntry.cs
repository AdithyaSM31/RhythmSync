using System.Text;

namespace RhythmSync.App.Controls;

/// <summary>
/// The pure logic of the four-box pairing-code entry: which digits are filled and which slot takes the next keystroke.
/// The code never has gaps, and <see cref="Focus"/> is never past the first empty slot.
/// </summary>
public sealed class CodeEntry
{
    public const int Length = 4;

    private string _code = string.Empty;

    /// <summary>The digits typed so far, at most <see cref="Length"/>, with no gaps.</summary>
    public string Code => _code;

    /// <summary>Index (0..3) of the slot that receives the next keystroke.</summary>
    public int Focus { get; private set; }

    public bool IsComplete => _code.Length == Length;

    /// <summary>Fills the focused slot with an ASCII digit and advances; anything else is ignored.</summary>
    public void Type(char c)
    {
        if (!char.IsAsciiDigit(c))
            return;

        _code = Focus < _code.Length
            ? string.Concat(_code.AsSpan(0, Focus), [c], _code.AsSpan(Focus + 1))
            : _code + c;
        Focus = Math.Min(Focus + 1, Length - 1);
    }

    /// <summary>Clears the focused slot if it is filled; otherwise moves back one slot and clears that one.</summary>
    public void Backspace()
    {
        if (Focus >= _code.Length)
        {
            if (Focus == 0)
                return;
            Focus--;
        }

        _code = _code.Remove(Focus, 1);
    }

    /// <summary>Replaces the code with the first four ASCII digits found in <paramref name="text"/>.</summary>
    public void Paste(string? text)
    {
        var digits = new StringBuilder(Length);
        foreach (var c in text ?? string.Empty)
        {
            if (!char.IsAsciiDigit(c))
                continue;
            digits.Append(c);
            if (digits.Length == Length)
                break;
        }

        _code = digits.ToString();
        Focus = Math.Min(_code.Length, Length - 1);
    }

    /// <summary>Takes a code from a binding, filtering it like a paste.</summary>
    public void Set(string? code) => Paste(code);

    /// <summary>Moves focus one slot left without changing the code.</summary>
    public void MoveLeft() => Focus = Math.Max(Focus - 1, 0);

    /// <summary>Moves focus one slot right, but never past the first empty slot.</summary>
    public void MoveRight() => Focus = Math.Min(Focus + 1, Math.Min(_code.Length, Length - 1));

    /// <summary>Moves focus to <paramref name="slot"/>, clamped to the filled range plus the first empty slot.</summary>
    public void FocusAt(int slot) => Focus = Math.Clamp(slot, 0, Math.Min(_code.Length, Length - 1));
}
