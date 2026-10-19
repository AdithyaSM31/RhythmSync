using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using RhythmSync.App.Tests.Ui;

namespace RhythmSync.App.Tests;

/// <summary>The "Dark studio" theme (Themes/Studio.xaml): token values, keyed and implicit styles, and clean rendering.</summary>
[Collection("Ui")]
public class ThemeTests
{
    private static readonly string[] SolidColours =
    [
        "Bg", "Surface", "Card", "CardBorder", "Raised", "RaisedBorder", "Track",
        "Text", "TextMuted", "Accent", "AccentSoft", "Teal", "Warning", "Danger",
    ];

    private static readonly string[] OtherKeys =
    [
        "AccentGradientBrush",
        "RadiusCard", "RadiusControl", "RadiusSmall", "RadiusPill",
        "FontUi", "FontMono", "FontIcons",
        "DisplayText", "TitleText", "SubtitleText", "BodyText", "CaptionText", "StatText",
        "PrimaryButton", "SecondaryButton", "GhostButton", "DangerButton",
        "ToggleSwitch", "NavItem", "CardBorder", "StudioFocusVisual", "StudioWindow",
    ];

    private static object Find(string key) => Application.Current.FindResource(key);

    private static Color ColourOf(string key) => ((SolidColorBrush)Find(key + "Brush")).Color;

    private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    [Fact]
    public void The_app_resources_and_the_merged_theme_resolve_together()
    {
        UiDispatcher.Run(() =>
        {
            var resources = Application.Current.Resources;
            Assert.IsType<BooleanToVisibilityConverter>(resources["BoolToVisibility"]);
            Assert.Equal(typeof(TextBlock), Assert.IsType<Style>(resources["OptionalText"]).TargetType);
            Assert.Equal(Hex("#8B7CFF"), Assert.IsType<SolidColorBrush>(resources["AccentBrush"]).Color);
        });
    }

    [Fact]
    public void Every_token_and_style_key_resolves()
    {
        UiDispatcher.Run(() =>
        {
            var keys = SolidColours.SelectMany(c => new[] { c + "Brush", c + "Color" }).Concat(OtherKeys);
            var missing = keys.Where(k => Application.Current.TryFindResource(k) is null).ToList();
            Assert.Empty(missing);

            foreach (string c in SolidColours)
            {
                Assert.IsType<SolidColorBrush>(Find(c + "Brush"));
                Assert.Equal(ColourOf(c), (Color)Find(c + "Color"));
            }
            Assert.IsType<LinearGradientBrush>(Find("AccentGradientBrush"));
            Assert.Equal(typeof(CheckBox), ((Style)Find("ToggleSwitch")).TargetType);
            Assert.Equal(typeof(RadioButton), ((Style)Find("NavItem")).TargetType);
            Assert.Equal(typeof(Border), ((Style)Find("CardBorder")).TargetType);
            foreach (string b in new[] { "PrimaryButton", "SecondaryButton", "GhostButton", "DangerButton" })
                Assert.Equal(typeof(Button), ((Style)Find(b)).TargetType);
            foreach (string t in new[] { "DisplayText", "TitleText", "SubtitleText", "BodyText", "CaptionText", "StatText" })
                Assert.Equal(typeof(TextBlock), ((Style)Find(t)).TargetType);
        });
    }

    [Fact]
    public void Token_colours_match_the_spec()
    {
        UiDispatcher.Run(() =>
        {
            var expected = new Dictionary<string, string>
            {
                ["Bg"] = "#0E0F13", ["Surface"] = "#14161C", ["Card"] = "#161921", ["CardBorder"] = "#232733",
                ["Raised"] = "#1D2029", ["RaisedBorder"] = "#2A2E3B", ["Track"] = "#262A36",
                ["Text"] = "#E8E9EE", ["TextMuted"] = "#8A8F9C", ["Accent"] = "#8B7CFF", ["AccentSoft"] = "#B4ABFF",
                ["Teal"] = "#4FD1C5", ["Warning"] = "#F5B94A", ["Danger"] = "#FF6B7A",
            };
            foreach (var (key, hex) in expected)
                Assert.True(ColourOf(key) == Hex(hex), $"{key} is {ColourOf(key)}, expected {hex}");

            var gradient = (LinearGradientBrush)Find("AccentGradientBrush");
            Assert.Equal(Hex("#8B7CFF"), gradient.GradientStops.Single(s => s.Offset == 0).Color);
            Assert.Equal(Hex("#4FD1C5"), gradient.GradientStops.Single(s => s.Offset == 1).Color);

            Assert.Equal(new CornerRadius(12), Find("RadiusCard"));
            Assert.Equal(new CornerRadius(9), Find("RadiusControl"));
            Assert.Equal(new CornerRadius(6), Find("RadiusSmall"));
            Assert.Equal(new CornerRadius(12), Find("RadiusPill"));

            Assert.Equal("Segoe UI Variable Text, Segoe UI", ((FontFamily)Find("FontUi")).Source);
            Assert.Equal("Cascadia Mono, Consolas", ((FontFamily)Find("FontMono")).Source);
            Assert.Equal("Segoe Fluent Icons, Segoe MDL2 Assets", ((FontFamily)Find("FontIcons")).Source);
        });
    }

    [Fact]
    public void Implicit_styles_apply_to_stock_controls()
    {
        UiDispatcher.Run(() =>
        {
            var button = new Button { Content = "Leave" };
            var slider = new Slider { Minimum = 0, Maximum = 1, Value = 0.6, Width = 200 };
            var textBox = new TextBox { Text = "Living room" };
            var comboBox = new ComboBox { ItemsSource = new[] { "Speakers", "Headphones" }, SelectedIndex = 0 };
            var scrollBar = new ScrollBar { Orientation = Orientation.Vertical, Height = 120 };
            var panel = new StackPanel { Children = { button, slider, textBox, comboBox, scrollBar } };

            UiDispatcher.Render(panel);

            Assert.Equal(ColourOf("Raised"), ((SolidColorBrush)button.Background).Color);
            Assert.Equal(ColourOf("RaisedBorder"), ((SolidColorBrush)button.BorderBrush).Color);
            Assert.Equal(FontWeights.SemiBold, button.FontWeight);

            var track = (Track)slider.Template.FindName("PART_Track", slider);
            var fill = Assert.IsType<LinearGradientBrush>(track.DecreaseRepeatButton.Background);
            Assert.Equal(ColourOf("Accent"), fill.GradientStops[0].Color);
            Assert.Equal(16, track.Thumb.ActualWidth);

            Assert.Equal(ColourOf("Bg"), ((SolidColorBrush)textBox.Background).Color);
            Assert.Equal(ColourOf("RaisedBorder"), ((SolidColorBrush)textBox.BorderBrush).Color);
            Assert.Equal(ColourOf("Bg"), ((SolidColorBrush)comboBox.Background).Color);
            Assert.Equal(10, scrollBar.ActualWidth);

            var window = new Window();
            window.BeginInit();   // implicit styles are looked up on initialisation, as when XAML builds a window
            window.EndInit();
            Assert.Equal(ColourOf("Bg"), ((SolidColorBrush)window.Background).Color);
            Assert.Equal(ColourOf("Text"), ((SolidColorBrush)window.Foreground).Color);
            Assert.Equal("Segoe UI Variable Text, Segoe UI", window.FontFamily.Source);
        });
    }

    [Fact]
    public void Eq_styles_resolve()
    {
        UiDispatcher.Run(() =>
        {
            Assert.Equal(typeof(Slider), Assert.IsType<Style>(Find("EqBandSlider")).TargetType);
            Assert.Equal(typeof(Expander), Assert.IsType<Style>(Find("EqSection")).TargetType);

            // Both render, bound to a real EQ view model, without a binding error.
            using var recorder = new BindingErrorRecorder();
            var eq = new RhythmSync.App.ViewModels.EqViewModel(RhythmSync.Core.Audio.Eq.EqGains.Flat) { IsExpanded = true };
            var slider = new Slider { Style = (Style)Find("EqBandSlider"), DataContext = eq.Bands[0] };
            slider.SetBinding(RangeBase.ValueProperty, new Binding("GainDb") { Mode = BindingMode.TwoWay });
            var section = new Expander { Style = (Style)Find("EqSection"), DataContext = eq, Content = new TextBlock { Text = "body" } };
            UiDispatcher.Render(new StackPanel { Children = { slider, section } });
            Assert.Empty(recorder.Errors);
            Assert.Equal("Equalizer", System.Windows.Automation.AutomationProperties.GetName(section));
            Assert.Equal(Orientation.Vertical, slider.Orientation);
            Assert.Equal(-12, slider.Minimum);
            Assert.Equal(12, slider.Maximum);
            Assert.Equal(0.5, slider.SmallChange);
            Assert.Equal(3, slider.LargeChange);

            // The expander header follows the view model, both ways.
            Assert.True(section.IsExpanded);
            section.IsExpanded = false;
            Assert.False(eq.IsExpanded);
        });
    }

    [Fact]
    public void The_eq_section_header_changes_on_hover_and_press()
    {
        UiDispatcher.Run(() =>
        {
            var template = Assert.IsType<ControlTemplate>(((Style)Find("EqSectionHeaderToggle")).Setters
                .OfType<Setter>().Single(s => s.Property == Control.TemplateProperty).Value);

            foreach (var property in new[] { UIElement.IsMouseOverProperty, ButtonBase.IsPressedProperty })
            {
                var trigger = Assert.Single(template.Triggers.OfType<Trigger>(), t => t.Property == property);
                Assert.Contains(trigger.Setters.OfType<Setter>(), s => s.TargetName == "Frame" && s.Property == Border.BackgroundProperty);
                Assert.Contains(trigger.Setters.OfType<Setter>(), s => s.TargetName == "Chevron" && s.Property == TextBlock.ForegroundProperty);
            }
        });
    }

    [Fact]
    public void The_eq_band_fill_runs_the_same_way_for_a_boost_and_a_cut()
    {
        UiDispatcher.Run(() =>
        {
            Color StopColour(double value, double offset)
            {
                var slider = new Slider { Style = (Style)Find("EqBandSlider"), Value = value };
                UiDispatcher.Render(slider);
                var fill = (Border)slider.Template.FindName("Fill", slider);
                var brush = Assert.IsType<LinearGradientBrush>(fill.Background);
                return brush.GradientStops.Single(s => s.Offset == offset).Color;
            }

            // The fill bar's top is the thumb for a boost and the 0 dB line for a cut, so the stops swap to keep
            // the thumb end violet and the 0 dB end teal in both.
            Assert.Equal(ColourOf("Accent"), StopColour(6, 0));
            Assert.Equal(ColourOf("Teal"), StopColour(6, 1));
            Assert.Equal(ColourOf("Teal"), StopColour(-6, 0));
            Assert.Equal(ColourOf("Accent"), StopColour(-6, 1));
        });
    }

    [Fact]
    public void Styled_controls_render_without_binding_errors()
    {
        UiDispatcher.Run(() =>
        {
            Style Keyed(string key) => (Style)Find(key);

            using var recorder = new BindingErrorRecorder();
            var panel = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "4729", Style = Keyed("DisplayText") },
                    new TextBlock { Text = "Page title", Style = Keyed("TitleText") },
                    new TextBlock { Text = "Section", Style = Keyed("SubtitleText") },
                    new TextBlock { Text = "Body", Style = Keyed("BodyText") },
                    new TextBlock { Text = "Caption", Style = Keyed("CaptionText") },
                    new TextBlock { Text = "Sync error 0.21 ms", Style = Keyed("StatText") },
                    new Button { Content = "Start hosting", Style = Keyed("PrimaryButton") },
                    new Button { Content = "Leave", Style = Keyed("SecondaryButton") },
                    new Button { Content = "Open sound settings", Style = Keyed("GhostButton") },
                    new Button { Content = "Stop hosting", Style = Keyed("DangerButton") },
                    new Button { Content = "Disabled", IsEnabled = false },
                    new Slider { Minimum = 0, Maximum = 1, Value = 0.4 },
                    new Slider { Minimum = 0, Maximum = 1, Value = 0.4, IsEnabled = false },
                    new CheckBox { Content = "Start with Windows", IsChecked = true, Style = Keyed("ToggleSwitch") },
                    new CheckBox { Content = "Start minimized", IsChecked = false, Style = Keyed("ToggleSwitch") },
                    new RadioButton { Content = "Host", Tag = "\uE7F5", IsChecked = true, GroupName = "nav", Style = Keyed("NavItem") },
                    new RadioButton { Content = "Join", Tag = "\uE896", GroupName = "nav", Style = Keyed("NavItem") },
                    new TextBox { Text = "Living room" },
                    new TextBox { Text = "Read only", IsEnabled = false },
                    new ComboBox { ItemsSource = new[] { "Speakers", "Headphones" }, SelectedIndex = 1 },
                    new ComboBox { ItemsSource = new[] { "Speakers" }, SelectedIndex = 0, IsEnabled = false },
                    new ScrollBar { Orientation = Orientation.Vertical, Height = 80 },
                    new ScrollBar { Orientation = Orientation.Horizontal, Width = 80 },
                    new Border { Style = Keyed("CardBorder"), Child = new TextBlock { Text = "Card" } },
                },
            };
            var scroller = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Visible };

            UiDispatcher.Render(scroller);

            Assert.Empty(recorder.Errors);
        });
    }

    [Fact]
    public void Every_visual_state_in_the_templates_can_be_entered()
    {
        UiDispatcher.Run(() =>
        {
            Style Keyed(string key) => (Style)Find(key);
            var panel = new StackPanel
            {
                Children =
                {
                    new Button { Content = "Start hosting", Style = Keyed("PrimaryButton") },
                    new Button { Content = "Leave" },
                    new Slider { Minimum = 0, Maximum = 1, Value = 0.4 },
                    new CheckBox { Content = "Start with Windows", Style = Keyed("ToggleSwitch") },
                    new RadioButton { Content = "Host", Tag = "\uE7F5", Style = Keyed("NavItem") },
                    new TextBox { Text = "Living room" },
                    new ComboBox { ItemsSource = new[] { "Speakers" }, SelectedIndex = 0 },
                    new ScrollBar { Orientation = Orientation.Vertical, Height = 80 },
                },
            };
            UiDispatcher.Render(panel);

            // Walk every templated control (including template parts such as thumbs) and enter each of its
            // states with transitions, so a storyboard with a bad target or property path fails here.
            int entered = 0;
            foreach (var control in Descendants(panel).OfType<Control>())
            {
                if (VisualTreeHelper.GetChildrenCount(control) == 0 || VisualTreeHelper.GetChild(control, 0) is not FrameworkElement root)
                    continue;
                foreach (VisualStateGroup group in VisualStateManager.GetVisualStateGroups(root))
                {
                    foreach (VisualState state in group.States)
                    {
                        Assert.True(VisualStateManager.GoToState(control, state.Name, useTransitions: true),
                            $"{control.GetType().Name} could not enter {state.Name}");
                        entered++;
                    }
                }
            }
            UiDispatcher.Render(panel);
            Assert.True(entered > 20, $"only {entered} states were found");
        });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var grandchild in Descendants(child))
                yield return grandchild;
        }
    }
}
