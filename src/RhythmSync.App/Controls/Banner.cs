using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;

namespace RhythmSync.App.Controls;

public enum BannerSeverity
{
    Warning,
    Info,
}

/// <summary>
/// A tinted message strip with a coloured left bar, an icon glyph and an optional ghost action button (spec §2.3).
/// The template lives in Themes/Banner.xaml; the action is hidden while <see cref="ActionText"/> is empty.
/// </summary>
public sealed class Banner : Control
{
    public static readonly DependencyProperty SeverityProperty = DependencyProperty.Register(
        nameof(Severity), typeof(BannerSeverity), typeof(Banner), new FrameworkPropertyMetadata(BannerSeverity.Warning));

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(Banner), new FrameworkPropertyMetadata(string.Empty));

    public static readonly DependencyProperty ActionTextProperty = DependencyProperty.Register(
        nameof(ActionText), typeof(string), typeof(Banner),
        new FrameworkPropertyMetadata(null, (d, e) => d.SetValue(HasActionPropertyKey, !string.IsNullOrEmpty((string?)e.NewValue))));

    public static readonly DependencyProperty ActionCommandProperty = DependencyProperty.Register(
        nameof(ActionCommand), typeof(ICommand), typeof(Banner), new FrameworkPropertyMetadata(null));

    private static readonly DependencyPropertyKey HasActionPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(HasAction), typeof(bool), typeof(Banner), new FrameworkPropertyMetadata(false));

    /// <summary>True while <see cref="ActionText"/> has text; the template shows the action button only then.</summary>
    public static readonly DependencyProperty HasActionProperty = HasActionPropertyKey.DependencyProperty;

    static Banner()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(Banner), new FrameworkPropertyMetadata(typeof(Banner)));
    }

    public BannerSeverity Severity
    {
        get => (BannerSeverity)GetValue(SeverityProperty);
        set => SetValue(SeverityProperty, value);
    }

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public string? ActionText
    {
        get => (string?)GetValue(ActionTextProperty);
        set => SetValue(ActionTextProperty, value);
    }

    public ICommand? ActionCommand
    {
        get => (ICommand?)GetValue(ActionCommandProperty);
        set => SetValue(ActionCommandProperty, value);
    }

    public bool HasAction => (bool)GetValue(HasActionProperty);

    /// <summary>Screen readers see the banner as Text named by <see cref="Message"/>; the theme makes it a polite live region.</summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new TextAutomationPeer(this, nameof(Banner), () => Message);
}
