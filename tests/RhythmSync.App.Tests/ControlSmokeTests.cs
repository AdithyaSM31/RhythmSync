using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using RhythmSync.App.Controls;
using RhythmSync.App.Tests.Ui;

namespace RhythmSync.App.Tests;

/// <summary>StatusChip and Banner (Themes/StatusChip.xaml, Themes/Banner.xaml) render cleanly and behave as specified.</summary>
[Collection("Ui")]
public class ControlSmokeTests
{
    private static Color Token(string key) => ((SolidColorBrush)Application.Current.FindResource(key)).Color;

    private static T Part<T>(Control control, string name) where T : class =>
        Assert.IsType<T>(control.Template.FindName(name, control));

    private static Color ColorOf(Brush? brush) => Assert.IsType<SolidColorBrush>(brush).Color;

    /// <summary>A code-created control only gets its style and template once it joins a tree, so tests render it inside a Grid, as the views do.</summary>
    private static Grid RenderInHost(FrameworkElement child, double width, double height)
    {
        var host = new Grid();
        host.Children.Add(child);
        UiDispatcher.Render(host, width, height);
        return host;
    }

    private static StatusChip Chip(StatusKind kind, string text = "In sync")
    {
        var chip = new StatusChip { Text = text, Kind = kind };
        RenderInHost(chip, 200, 30);
        return chip;
    }

    private static void Settle(StatusChip chip) =>
        UiDispatcher.Render((FrameworkElement)System.Windows.Media.VisualTreeHelper.GetParent(chip), 200, 30);

    private static void WithAnimations(bool enabled, Action body)
    {
        var previous = StatusChip.AnimationsEnabled;
        StatusChip.AnimationsEnabled = () => enabled;
        try
        {
            body();
        }
        finally
        {
            StatusChip.AnimationsEnabled = previous;
        }
    }

    [Fact]
    public void StatusChip_renders_every_kind_without_errors()
    {
        UiDispatcher.Run(() =>
        {
            using var recorder = new BindingErrorRecorder();
            foreach (var kind in Enum.GetValues<StatusKind>())
            {
                var chip = Chip(kind);
                Assert.NotNull(chip.Template);
                Assert.Equal("In sync", Part<TextBlock>(chip, "Label").Text);
                Assert.True(chip.ActualWidth > 0, $"{kind} has no size");
            }

            Assert.Empty(recorder.Errors);

            Assert.Equal(Token("TealBrush"), ColorOf(Part<Ellipse>(Chip(StatusKind.InSync), "PART_Dot").Fill));
            Assert.Equal(Token("TealBrush"), ColorOf(Part<Ellipse>(Chip(StatusKind.Hosting), "PART_Dot").Fill));
            Assert.Equal(Token("AccentBrush"), ColorOf(Part<Ellipse>(Chip(StatusKind.Syncing), "PART_Dot").Fill));
            Assert.Equal(Token("WarningBrush"), ColorOf(Part<Ellipse>(Chip(StatusKind.Degraded), "PART_Dot").Fill));
            Assert.Equal(Token("TextMutedBrush"), ColorOf(Part<Ellipse>(Chip(StatusKind.Connecting), "PART_Dot").Fill));
            Assert.Equal(Token("DangerBrush"), ColorOf(Part<Ellipse>(Chip(StatusKind.NotConnected), "PART_Dot").Fill));
        });
    }

    [Fact]
    public void StatusChip_glows_only_for_in_sync_and_hosting()
    {
        UiDispatcher.Run(() =>
        {
            foreach (var kind in Enum.GetValues<StatusKind>())
            {
                var effect = Part<Ellipse>(Chip(kind), "PART_Dot").Effect;
                if (kind is StatusKind.InSync or StatusKind.Hosting)
                {
                    var glow = Assert.IsType<DropShadowEffect>(effect);
                    Assert.Equal(Token("TealBrush"), glow.Color);
                    Assert.Equal(8, glow.BlurRadius);
                    Assert.Equal(0, glow.ShadowDepth);
                }
                else
                {
                    Assert.Null(effect);
                }
            }
        });
    }

    [Fact]
    public void Syncing_pulses_only_when_animations_are_enabled()
    {
        UiDispatcher.Run(() =>
        {
            Assert.Equal(new Duration(TimeSpan.FromSeconds(1.6)), Application.Current.FindResource("PulseDuration"));

            WithAnimations(false, () =>
            {
                var still = Chip(StatusKind.Syncing);
                Assert.False(Part<Ellipse>(still, "PART_Dot").HasAnimatedProperties);
            });

            WithAnimations(true, () =>
            {
                var pulsing = Chip(StatusKind.Syncing);
                var dot = Part<Ellipse>(pulsing, "PART_Dot");
                Assert.True(dot.HasAnimatedProperties);

                pulsing.Kind = StatusKind.InSync;   // leaving Syncing stops the pulse
                Settle(pulsing);   // WPF drops the removed clocks on the next idle pass
                Assert.False(dot.HasAnimatedProperties);
                Assert.Equal(1, dot.Opacity);

                pulsing.Kind = StatusKind.Syncing;   // and coming back restarts it
                Settle(pulsing);
                Assert.True(dot.HasAnimatedProperties);

                Assert.False(Part<Ellipse>(Chip(StatusKind.Degraded), "PART_Dot").HasAnimatedProperties);
            });
        });
    }

    [Fact]
    public void Banner_hides_action_when_text_is_empty()
    {
        UiDispatcher.Run(() =>
        {
            using var recorder = new BindingErrorRecorder();
            int runs = 0;
            var banner = new Banner
            {
                Message = "Set CABLE Input as the default playback device so this PC plays in sync.",
                ActionCommand = new RelayCommand(() => runs++),
            };
            var host = RenderInHost(banner, 600, 60);

            var action = Part<Button>(banner, "PART_Action");
            Assert.Equal(Visibility.Collapsed, action.Visibility);
            Assert.Equal(banner.Message, Part<TextBlock>(banner, "PART_Message").Text);

            banner.ActionText = "";
            UiDispatcher.Render(host, 600, 60);
            Assert.Equal(Visibility.Collapsed, action.Visibility);

            banner.ActionText = "Open sound settings";
            UiDispatcher.Render(host, 600, 60);
            Assert.Equal(Visibility.Visible, action.Visibility);
            Assert.Equal("Open sound settings", action.Content);

            // The automation peer is how a click without a mouse reaches Button.OnClick; it dispatches asynchronously.
            ((IInvokeProvider)new ButtonAutomationPeer(action).GetPattern(PatternInterface.Invoke)!).Invoke();
            UiDispatcher.Render(host, 600, 60);
            Assert.Equal(1, runs);

            banner.ActionText = null;
            UiDispatcher.Render(host, 600, 60);
            Assert.Equal(Visibility.Collapsed, action.Visibility);

            Assert.Empty(recorder.Errors);
        });
    }

    [Fact]
    public void Banner_shows_the_warning_glyph_and_switches_to_accent_for_info()
    {
        UiDispatcher.Run(() =>
        {
            var banner = new Banner { Message = "Heads up" };
            var host = RenderInHost(banner, 600, 60);

            var glyph = Part<TextBlock>(banner, "PART_Glyph");
            Assert.Equal("", glyph.Text);
            Assert.Equal("Warning", AutomationProperties.GetName(glyph));
            Assert.Equal(Token("WarningBrush"), ColorOf(Part<Border>(banner, "Bar").Background));
            Assert.Equal(3, Part<Border>(banner, "Bar").Width);
            Assert.Equal(Token("WarningBrush"), ColorOf(Part<Border>(banner, "Root").BorderBrush));

            banner.Severity = BannerSeverity.Info;
            UiDispatcher.Render(host, 600, 60);
            Assert.Equal(Token("AccentBrush"), ColorOf(Part<Border>(banner, "Bar").Background));
            Assert.Equal(Token("AccentBrush"), ColorOf(Part<Border>(banner, "Root").BorderBrush));
        });
    }

    private sealed class RelayCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute();
    }
}
