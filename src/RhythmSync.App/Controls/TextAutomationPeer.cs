using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;

namespace RhythmSync.App.Controls;

/// <summary>
/// Presents a templated control (<see cref="StatusChip"/>, <see cref="Banner"/>) to screen readers as one Text element
/// named by its text. The TextBlocks inside a template are not control elements, so without this the text is not read.
/// An explicit <c>AutomationProperties.Name</c> wins; the themes bind it to the same text.
/// </summary>
internal sealed class TextAutomationPeer : FrameworkElementAutomationPeer
{
    private readonly string _className;
    private readonly Func<string?> _text;

    public TextAutomationPeer(FrameworkElement owner, string className, Func<string?> text)
        : base(owner)
    {
        _className = className;
        _text = text;
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

    protected override string GetClassNameCore() => _className;

    protected override string GetNameCore()
    {
        string name = base.GetNameCore();
        return string.IsNullOrEmpty(name) ? _text() ?? string.Empty : name;
    }

    /// <summary>
    /// A control element while it and every ancestor up to its window are Visible, so a collapsed chip or banner stays out
    /// of the control view. (The base asks <see cref="UIElement.IsVisible"/>, which also needs a shown window; this
    /// answers the same in a shown window and can be checked without one.)
    /// </summary>
    protected override bool IsControlElementCore()
    {
        for (DependencyObject? e = Owner; e is not null; e = VisualTreeHelper.GetParent(e))
        {
            if (e is UIElement { Visibility: not Visibility.Visible }) return false;
        }
        return true;
    }
}
