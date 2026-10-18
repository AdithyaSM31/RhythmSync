using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace RhythmSync.App.Controls;

/// <summary>A pill with a status dot and a label (spec §2.3). The template lives in Themes/StatusChip.xaml.</summary>
public sealed class StatusChip : Control
{
    private const string DotPart = "PART_Dot";

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(StatusChip), new FrameworkPropertyMetadata(string.Empty));

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(StatusKind), typeof(StatusChip),
        new FrameworkPropertyMetadata(StatusKind.NotConnected, (d, _) => ((StatusChip)d).UpdatePulse()));

    private Storyboard? _pulse;
    private FrameworkElement? _pulseTarget;

    static StatusChip()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(StatusChip), new FrameworkPropertyMetadata(typeof(StatusChip)));
    }

    /// <summary>Whether the Syncing pulse may run; false when Windows animations are off. Tests replace it and restore it afterwards.</summary>
    internal static Func<bool> AnimationsEnabled { get; set; } = () => SystemParameters.ClientAreaAnimation;

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public StatusKind Kind
    {
        get => (StatusKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        // A new template means a new dot: the old storyboard no longer targets anything on screen.
        UpdatePulse();
    }

    private void UpdatePulse()
    {
        StopPulse();
        if (Kind != StatusKind.Syncing || !AnimationsEnabled() || GetTemplateChild(DotPart) is not FrameworkElement dot)
            return;

        var cycle = TryFindResource("PulseDuration") is Duration d ? d : new Duration(TimeSpan.FromSeconds(1.6));
        var opacity = new DoubleAnimationUsingKeyFrames { Duration = cycle, RepeatBehavior = RepeatBehavior.Forever };
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromPercent(0)));
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(0.5, KeyTime.FromPercent(0.5)));
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromPercent(1)));
        Storyboard.SetTargetProperty(opacity, new PropertyPath(OpacityProperty));

        var pulse = new Storyboard();
        pulse.Children.Add(opacity);
        pulse.Begin(dot, isControllable: true);
        _pulse = pulse;
        _pulseTarget = dot;
    }

    private void StopPulse()
    {
        if (_pulse is not null && _pulseTarget is not null)
            _pulse.Remove(_pulseTarget);
        _pulse = null;
        _pulseTarget = null;
    }
}
