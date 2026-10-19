using System.Net;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using RhythmSync.App.Controls;
using RhythmSync.App.Tests.Ui;
using RhythmSync.App.ViewModels;
using RhythmSync.App.Views;
using RhythmSync.Core.Clock;
using RhythmSync.Core.Audio.Eq;
using RhythmSync.Core.Host;
using RhythmSync.Core.Protocol;
using RhythmSync.Platform.Windows;

namespace RhythmSync.App.Tests;

/// <summary>The Host pages (idle and running) lay out with the real theme, bind without errors and keep long names inside their cards.</summary>
[Collection("Ui")]
public class HostPagesSmokeTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Controls = "clr-namespace:RhythmSync.App.Controls";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    /// <summary>Whether the element and all its ancestors are Visible. UIElement.IsVisible is false for every element here, because the views are never attached to a window.</summary>
    private static bool Shown(DependencyObject element)
    {
        for (DependencyObject? e = element; e is not null; e = VisualTreeHelper.GetParent(e))
            if (e is UIElement { Visibility: not Visibility.Visible })
                return false;
        return true;
    }

    private static Guid Id(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    private static ConnectedClient Client(int n, string name, SyncState state, double latePct = 0, EqGains? eq = null)
        => new(Id(n), name, new IPEndPoint(IPAddress.Loopback, 5000 + n), true,
            new HeartbeatStats(0.1, latePct, 42, 0.18, 0.21, state), 0.8f, 10, eq ?? EqGains.Flat);

    /// <summary>A curve that matches no preset.</summary>
    private static readonly EqGains CustomCurve = EqGains.Create(new float[] { 3, 2.5f, 0, 0, -1, 0, 0, 4, 0, -6 });

    private static HostViewModel NewHost(string sessionName = "Living Room", HostAudioStatus status = HostAudioStatus.Ok)
        => new(new ShellTests.FakeHost(), () => status, _ => null, (_, _) => { }, "4729", sessionName);

    /// <summary>A Host tab (real view models over the fake runtime) that is already hosting, or still idle when <paramref name="host"/> is null.</summary>
    private static HostTabView NewTab(HostViewModel? host, string sessionName = "Living Room")
    {
        var runtime = new ShellTests.FakeRuntime();
        var vm = new HostTabViewModel(runtime, sessionName);
        if (host is not null)
            runtime.Host(host);
        return new HostTabView { DataContext = vm };
    }

    private static string ViewXaml(string file, [CallerFilePath] string here = "")
    {
        string repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
        return File.ReadAllText(Path.Combine(repo, "src", "RhythmSync.App", "Views", file));
    }

    [Fact]
    public void Idle_host_page_renders_without_errors()
    {
        UiDispatcher.Run(() =>
        {
            using var recorder = new BindingErrorRecorder();
            var tab = NewTab(null);
            UiDispatcher.Render(tab, 960, 640);

            var start = Assert.Single(Descendants<Button>(tab), b => Equals(b.Content, "Start hosting"));
            Assert.True(Shown(start));
            Assert.True(start.ActualWidth > 300, "the Start button spans the card");
            Assert.Same(Application.Current.FindResource("PrimaryButton"), start.Style);

            var card = Assert.Single(Descendants<Border>(tab), b => Shown(b) && b.Style == Application.Current.FindResource("CardBorder"));
            Assert.Equal(360, card.ActualWidth);
            Assert.True(card.ActualHeight < 520 - 32 - 32, "the idle card fits the minimum window");

            Assert.Contains(Descendants<TextBlock>(tab), t => t.Text == "Share this PC's audio");
            Assert.DoesNotContain(Descendants<StatusChip>(tab), c => Shown(c));
            Assert.Empty(recorder.Errors);
        });
    }

    [Fact]
    public void Running_host_page_with_clients_and_stats_renders_without_errors()
    {
        UiDispatcher.Run(() =>
        {
            using var recorder = new BindingErrorRecorder();
            var host = NewHost(status: HostAudioStatus.VbCableNotDefault);
            host.UpdateClients([
                Client(1, "Kitchen Laptop", SyncState.InSync),
                Client(2, "Study PC", SyncState.Degraded, latePct: 9),
            ]);
            host.ShowStats = true;
            var tab = NewTab(host);
            UiDispatcher.Render(tab, 960, 640);

            var stop = Assert.Single(Descendants<Button>(tab), b => Equals(b.Content, "Stop hosting"));
            Assert.True(Shown(stop));
            Assert.Same(Application.Current.FindResource("DangerButton"), stop.Style);

            // The session chip, the This PC card (not playing locally: the routing is broken here), then one per client.
            var chips = Descendants<StatusChip>(tab).Where(c => Shown(c)).ToList();
            Assert.Equal([StatusKind.Hosting, StatusKind.NotConnected, StatusKind.InSync, StatusKind.Degraded], chips.Select(c => c.Kind));
            Assert.Equal(["Hosting", LocalSpeakerRowViewModel.NotPlayingText, "In sync", "Sync is degraded"], chips.Select(c => c.Text));

            var banner = Assert.Single(Descendants<Banner>(tab), b => Shown(b));
            Assert.Equal(HostViewModel.VbCableNotDefaultBanner, banner.Message);
            Assert.Equal("Open sound settings", banner.ActionText);

            // The code is one big glyph per character, and the speaker picker sits in the settings card.
            var code = Descendants<ItemsControl>(tab).Single(i => Equals(i.ItemsSource, "4729"));
            Assert.Equal(4, code.Items.Count);
            Assert.Equal("Pairing code 4729", AutomationProperties.GetName(code));   // read as one name, not four glyphs
            var picker = Assert.Single(Descendants<ComboBox>(tab), c => Shown(c));
            var tabVm = (HostTabViewModel)tab.DataContext;
            Assert.Single(picker.Items);
            Assert.NotNull(tabVm.SelectedSpeaker);
            Assert.Same(tabVm.SelectedSpeaker, picker.SelectedItem);
            picker.SelectedItem = null;   // the picker is bound two-way to the tab's view model, not to the running host
            Assert.Null(tabVm.SelectedSpeaker);

            // Show stats on: both rows show the five stat columns, the congested warning is shown once.
            var panels = Descendants<StatsPanel>(tab).Where(p => Shown(p)).ToList();
            Assert.Equal(2, panels.Count);
            Assert.Contains(Descendants<TextBlock>(tab), t => t.Text == "Clock fit residual" && Shown(t));
            Assert.Single(Descendants<TextBlock>(tab), t => t.Text == ClientRowViewModel.CongestedMessage && Shown(t));
            Assert.DoesNotContain(Descendants<TextBlock>(tab), t => t.Text == "Waiting for PCs to join…" && Shown(t));

            host.ShowStats = false;
            UiDispatcher.Render(tab, 960, 640);
            Assert.DoesNotContain(Descendants<StatsPanel>(tab), p => Shown(p));

            Assert.Empty(recorder.Errors);
        });
    }

    private static Border CardOf(DependencyObject root, Func<Border, bool> pick)
        => Descendants<Border>(root).First(b => Shown(b) && b.Style == Application.Current.FindResource("CardBorder") && pick(b));

    private static Border ClientCard(DependencyObject root, string name)
        => CardOf(root, b => Descendants<TextBlock>(b).Any(t => t.Text == name));

    [Fact]
    public void Running_host_page_with_an_expanded_eq_renders_without_errors()
    {
        UiDispatcher.Run(() =>
        {
            using var recorder = new BindingErrorRecorder();
            var host = NewHost();
            host.UpdateClients([Client(1, "Kitchen Laptop", SyncState.InSync, eq: CustomCurve)]);
            host.Clients[0].Eq.IsExpanded = true;
            host.ShowStats = true;
            var tab = NewTab(host);
            UiDispatcher.Render(tab, 960, 640);

            var card = ClientCard(tab, "Kitchen Laptop");
            var editor = Assert.Single(Descendants<EqEditor>(card));
            Assert.True(Shown(editor));
            var sliders = Descendants<Slider>(editor).ToList();
            Assert.Equal(10, sliders.Count);
            Assert.Equal(CustomCurve.ToArray().Select(g => (double)g), sliders.Select(s => s.Value));
            Assert.Equal(EqBands.Labels.Select(l => $"EQ {l} Hz"), sliders.Select(s => AutomationProperties.GetName(s)));
            Assert.Equal(host.Clients[0].Eq.Bands.Select(b => b.AutomationText), sliders.Select(s => AutomationProperties.GetHelpText(s)));
            Assert.Same(Application.Current.FindResource("EqBandSlider"), sliders[0].Style);

            // Header and control line.
            var section = Assert.Single(Descendants<Expander>(card));
            Assert.True(section.IsExpanded);
            Assert.Equal("Equalizer", AutomationProperties.GetName(section));
            Assert.Contains(Descendants<TextBlock>(section), t => t.Text == "EQ" && Shown(t));
            Assert.Contains(Descendants<TextBlock>(section), t => t.Text == "Custom" && Shown(t));   // the header: no preset matches
            Assert.Contains(Descendants<TextBlock>(editor), t => t.Text == "Preset" && Shown(t));
            Assert.Contains(Descendants<TextBlock>(editor), t => t.Text == "-12" && Shown(t));   // ASCII hyphen-minus, like GainText and PreampText
            Assert.Contains(Descendants<TextBlock>(editor), t => t.Text == host.Clients[0].Eq.PreampText && Shown(t));
            var flat = Assert.Single(Descendants<Button>(editor), b => Equals(b.Content, "Flat"));
            Assert.Same(Application.Current.FindResource("GhostButton"), flat.Style);
            var preset = Assert.Single(Descendants<ComboBox>(editor));
            Assert.Equal("Custom", preset.SelectedItem);
            Assert.Equal(160, preset.ActualWidth);

            // The value above each band is muted at 0 dB and plain otherwise.
            var muted = ((SolidColorBrush)Application.Current.FindResource("TextMutedBrush")).Color;
            var normal = ((SolidColorBrush)Application.Current.FindResource("TextBrush")).Color;
            var zero = Descendants<TextBlock>(editor).First(t => t.Text == "0" && Shown(t) && t.FontSize == 12);   // 12 px StatText, not the 11 px "0" gridline caption
            Assert.Equal(muted, ((SolidColorBrush)zero.Foreground).Color);
            var boosted = Descendants<TextBlock>(editor).First(t => t.Text == "+3" && Shown(t) && t.FontSize == 12);
            Assert.Equal(normal, ((SolidColorBrush)boosted.Foreground).Color);

            // Dragging a band reaches the view model.
            sliders[2].Value = 4;
            Assert.Equal(4, host.Clients[0].Eq.Bands[2].GainDb);

            // Collapsing hides the editor; the header stays.
            section.IsExpanded = false;
            UiDispatcher.Render(tab, 960, 640);
            Assert.False(host.Clients[0].Eq.IsExpanded);
            Assert.False(Shown(editor));
            Assert.Contains(Descendants<TextBlock>(section), t => t.Text == "EQ" && Shown(t));

            Assert.Empty(recorder.Errors);
        });
    }

    [Fact]
    public void Eq_is_collapsed_by_default_and_the_chevron_follows_it()
    {
        UiDispatcher.Run(() =>
        {
            using var recorder = new BindingErrorRecorder();
            var host = NewHost();
            host.UpdateClients([Client(1, "Kitchen Laptop", SyncState.InSync)]);
            var tab = NewTab(host);
            UiDispatcher.Render(tab, 960, 640);

            var card = ClientCard(tab, "Kitchen Laptop");
            Assert.DoesNotContain(Descendants<EqEditor>(card), e => Shown(e));   // not even built until it is opened
            Assert.Contains(Descendants<TextBlock>(card), t => t.Text == "Flat" && Shown(t) && t.FontSize == 11);   // the header's caption
            Assert.Contains(Descendants<TextBlock>(card), t => t.Text == "" && Shown(t));
            Assert.DoesNotContain(Descendants<TextBlock>(card), t => t.Text == "" && Shown(t));

            host.Clients[0].Eq.IsExpanded = true;
            UiDispatcher.Render(tab, 960, 640);
            Assert.True(Shown(Assert.Single(Descendants<EqEditor>(card))));
            Assert.Contains(Descendants<TextBlock>(card), t => t.Text == "" && Shown(t));
            Assert.DoesNotContain(Descendants<TextBlock>(card), t => t.Text == "" && Shown(t));
            Assert.Empty(recorder.Errors);
        });
    }

    [Fact]
    public void Picking_a_preset_while_on_Custom_applies_it_and_the_box_shows_it()
    {
        UiDispatcher.Run(() =>
        {
            using var recorder = new BindingErrorRecorder();
            var host = NewHost();
            host.UpdateClients([Client(1, "Kitchen Laptop", SyncState.InSync, eq: CustomCurve)]);
            var eq = host.Clients[0].Eq;
            eq.IsExpanded = true;
            var tab = NewTab(host);
            UiDispatcher.Render(tab, 960, 640);

            var editor = Assert.Single(Descendants<EqEditor>(ClientCard(tab, "Kitchen Laptop")));
            var preset = Assert.Single(Descendants<ComboBox>(editor));
            Assert.Contains("Custom", eq.Presets);
            Assert.Equal("Custom", preset.SelectedItem);

            var bass = EqPresets.All.First(p => p.Name == "Bass boost").Gains;
            preset.SelectedItem = "Bass boost";   // "Custom" leaves the list while this selection is being made
            UiDispatcher.Render(tab, 960, 640);

            Assert.Equal(bass, eq.Gains);
            Assert.DoesNotContain("Custom", eq.Presets);
            Assert.Equal("Bass boost", preset.SelectedItem);
            Assert.Equal("Bass boost", eq.HeaderText);

            // And the Flat button resets from here.
            var flat = Assert.Single(Descendants<Button>(editor), b => Equals(b.Content, "Flat"));
            flat.Command.Execute(null);
            UiDispatcher.Render(tab, 960, 640);
            Assert.True(eq.Gains.IsFlat);
            Assert.Equal("Flat", preset.SelectedItem);
            Assert.Empty(recorder.Errors);
        });
    }

    [Fact]
    public void This_pc_card_renders_playing_and_not_playing()
    {
        UiDispatcher.Run(() =>
        {
            using var recorder = new BindingErrorRecorder();
            var host = NewHost(status: HostAudioStatus.Ok);
            var tab = NewTab(host);
            UiDispatcher.Render(tab, 960, 640);

            var card = ClientCard(tab, "This PC");
            var name = Assert.Single(Descendants<TextBlock>(card), t => t.Text == "This PC");
            Assert.Same(Application.Current.FindResource("SubtitleText"), name.Style);
            var chip = Assert.Single(Descendants<StatusChip>(card));
            Assert.Equal(StatusKind.Hosting, chip.Kind);
            Assert.Equal("Playing here", chip.Text);

            // No volume or offset sliders and no stats: just the EQ, collapsed.
            Assert.DoesNotContain(Descendants<Slider>(card), s => Shown(s));
            Assert.Empty(Descendants<StatsPanel>(card));
            Assert.Equal("Equalizer", AutomationProperties.GetName(Assert.Single(Descendants<Expander>(card))));
            Assert.DoesNotContain(Descendants<EqEditor>(card), e => Shown(e));

            // It sits above the (empty) client list and is not counted as a connected PC.
            var empty = ClientCard(tab, "Waiting for PCs to join…");
            Assert.True(card.TranslatePoint(new Point(0, 0), tab).Y < empty.TranslatePoint(new Point(0, 0), tab).Y);
            Assert.Contains(Descendants<TextBlock>(tab), t => t.Text == "0" && Shown(t) && t.Style == Application.Current.FindResource("CaptionText"));

            // Not playing locally: the same card, still editable, with the other chip.
            host.LocalSpeaker.IsPlaying = false;
            host.LocalSpeaker.Eq.IsExpanded = true;
            UiDispatcher.Render(tab, 960, 640);
            Assert.Equal(StatusKind.NotConnected, chip.Kind);
            Assert.Equal("Not playing on this PC", chip.Text);
            var sliders = Descendants<Slider>(card).Where(s => Shown(s)).ToList();
            Assert.Equal(10, sliders.Count);
            Assert.All(sliders, s => Assert.True(s.IsEnabled));
            Assert.Empty(recorder.Errors);

            host.LocalSpeaker.IsPlaying = true;
            UiDispatcher.Render(tab, 960, 640);
            Assert.Equal(StatusKind.Hosting, chip.Kind);
            Assert.Empty(recorder.Errors);
        });
    }

    [Fact]
    public void Eq_strip_fits_a_client_card_at_the_minimum_window_size()
    {
        UiDispatcher.Run(() =>
        {
            using var recorder = new BindingErrorRecorder();
            var host = NewHost();
            host.UpdateClients([Client(1, "Kitchen Laptop", SyncState.InSync, eq: CustomCurve)]);
            host.Clients[0].Eq.IsExpanded = true;
            host.LocalSpeaker.Eq.IsExpanded = true;
            host.ShowStats = true;
            var tab = NewTab(host);
            UiDispatcher.Render(tab, MinPageWidth, MinPageHeight);

            foreach (string owner in new[] { "Kitchen Laptop", "This PC" })
            {
                var card = ClientCard(tab, owner);
                double content = card.ActualWidth - card.Padding.Left - card.Padding.Right - card.BorderThickness.Left - card.BorderThickness.Right;
                var editor = Assert.Single(Descendants<EqEditor>(card));
                Assert.True(Shown(editor));

                Assert.True(editor.DesiredSize.Width <= content + 0.5, $"{owner}: the EQ editor wants {editor.DesiredSize.Width} of {content}");
                double limit = card.ActualWidth - card.Padding.Right - card.BorderThickness.Right + 0.5;
                foreach (var element in Descendants<FrameworkElement>(editor).Where(e => Shown(e) && e.ActualWidth > 0 && (e is Slider || e is TextBlock || e is Button || e is ComboBox)))
                    Assert.True(Right(element, card) <= limit, $"{owner}: {element.GetType().Name} sticks out of the card ({Right(element, card)} > {limit})");
                var sliders = Descendants<Slider>(editor).ToList();
                Assert.Equal(10, sliders.Count);
                Assert.All(sliders, s => Assert.Equal(120, s.ActualHeight, 0.5));
            }
            Assert.Empty(recorder.Errors);
        });
    }

    [Fact]
    public void Running_host_page_without_clients_shows_the_empty_state()
    {
        UiDispatcher.Run(() =>
        {
            using var recorder = new BindingErrorRecorder();
            var tab = NewTab(NewHost());
            UiDispatcher.Render(tab, 960, 640);

            Assert.Contains(Descendants<TextBlock>(tab), t => t.Text == "Waiting for PCs to join…" && Shown(t));
            Assert.DoesNotContain(Descendants<Banner>(tab), b => Shown(b));
            Assert.Empty(recorder.Errors);
        });
    }

    [Fact]
    public void Stopping_the_host_returns_to_the_idle_card_without_binding_errors()
    {
        UiDispatcher.Run(() =>
        {
            var runtime = new ShellTests.FakeRuntime();
            var vm = new HostTabViewModel(runtime, "Living Room");
            var tab = new HostTabView { DataContext = vm };
            runtime.Host(NewHost());
            UiDispatcher.Render(tab, 960, 640);

            using var recorder = new BindingErrorRecorder();
            runtime.StopAsync();
            UiDispatcher.Render(tab, 960, 640);

            Assert.Single(Descendants<Button>(tab), b => Equals(b.Content, "Start hosting") && Shown(b));
            Assert.DoesNotContain(Descendants<Button>(tab), b => Equals(b.Content, "Stop hosting") && Shown(b));
            Assert.Empty(recorder.Errors);

            runtime.Host(NewHost("Den"));   // and back to running with a new host
            UiDispatcher.Render(tab, 960, 640);
            Assert.Contains(Descendants<TextBlock>(tab), t => t.Text == "Den" && Shown(t));
            Assert.Empty(recorder.Errors);
        });
    }

    [Fact]
    public void Running_host_page_with_eight_clients_scrolls()
    {
        UiDispatcher.Run(() =>
        {
            using var recorder = new BindingErrorRecorder();
            var host = NewHost();
            host.UpdateClients(Enumerable.Range(1, 8).Select(n => Client(n, $"PC {n}", SyncState.InSync)).ToList());
            host.ShowStats = true;
            var tab = NewTab(host);
            UiDispatcher.Render(tab, 960, 640);

            var scroll = Assert.Single(Descendants<ScrollViewer>(tab), s => s.Name == "HostScroll");
            Assert.True(scroll.ScrollableHeight > 0, "eight client cards do not fit in 640 px");
            Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);

            // The header stays above the scrolling content.
            var stop = Assert.Single(Descendants<Button>(tab), b => Equals(b.Content, "Stop hosting"));
            Assert.True(stop.TranslatePoint(new Point(0, 0), tab).Y < scroll.TranslatePoint(new Point(0, 0), tab).Y);
            Assert.Empty(recorder.Errors);
        });
    }

    // The page area at the 760x520 minimum window: 760 - 200 sidebar - 48 page margins wide, and about 440 tall under the title bar.
    private const double MinPageWidth = 512;
    private const double MinPageHeight = 440;

    private static double NaturalWidth(string text, string style)
    {
        var probe = new TextBlock { Text = text, Style = (Style)Application.Current.FindResource(style) };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Width;
    }

    private static double Left(FrameworkElement element, Visual root) => element.TransformToAncestor(root).Transform(new Point(0, 0)).X;

    private static double Right(FrameworkElement element, Visual root) => Left(element, root) + element.ActualWidth;

    [Fact]
    public void Long_client_names_ellipsize_but_normal_ones_are_not_squeezed_at_minimum_size()
    {
        UiDispatcher.Run(() =>
        {
            string normal = "Kitchen Laptop";
            string longName = new('C', 64);
            var host = NewHost();
            // Degraded and congested is the widest row: the chip text is long and the warning is shown.
            host.UpdateClients([Client(1, normal, SyncState.Degraded, latePct: 9), Client(2, longName, SyncState.Degraded, latePct: 9)]);
            host.ShowStats = true;
            var tab = NewTab(host);
            UiDispatcher.Render(tab, MinPageWidth, MinPageHeight);

            var kitchen = Assert.Single(Descendants<TextBlock>(tab), t => t.Text == normal);
            Assert.True(kitchen.ActualWidth >= NaturalWidth(normal, "SubtitleText") - 0.5,
                $"'{normal}' is squeezed to {kitchen.ActualWidth} (needs {NaturalWidth(normal, "SubtitleText")})");

            var long64 = Assert.Single(Descendants<TextBlock>(tab), t => t.Text == longName);
            Assert.Equal(TextTrimming.CharacterEllipsis, long64.TextTrimming);
            Assert.True(long64.ActualWidth < NaturalWidth(longName, "SubtitleText") - 50, "a 64-character name is trimmed");
            Assert.True(long64.ActualWidth >= 120 - 0.5, "the name column keeps a floor");
            var container = (FrameworkElement)VisualTreeHelper.GetParent(long64);
            Assert.True(long64.ActualWidth <= container.ActualWidth + 0.5);

            // The chip, the sync error and the warning of that row stay inside the card.
            var card = Descendants<Border>(tab).First(b => Shown(b) && b.Style == Application.Current.FindResource("CardBorder")
                && Descendants<TextBlock>(b).Any(t => t.Text == longName));
            foreach (var t in Descendants<TextBlock>(card).Where(t => Shown(t) && t.ActualWidth > 0))
                Assert.True(Right(t, card) <= card.ActualWidth + 0.5, $"'{t.Text}' sticks out of its card");
            Assert.Equal(2, Descendants<TextBlock>(tab).Count(t => t.Text == ClientRowViewModel.CongestedMessage && Shown(t)));
        });
    }

    [Fact]
    public void Client_row_offset_controls_fit_the_minimum_width_and_drive_the_offset()
    {
        UiDispatcher.Run(() =>
        {
            using var recorder = new BindingErrorRecorder();
            var host = NewHost();
            host.UpdateClients([Client(1, "Kitchen Laptop", SyncState.Degraded, latePct: 9)]);
            host.ShowStats = true;
            var row = host.Clients[0];
            var tab = NewTab(host);
            UiDispatcher.Render(tab, MinPageWidth, MinPageHeight);

            Button ButtonNamed(string name) => Assert.Single(Descendants<Button>(tab), b => AutomationProperties.GetName(b) == name && Shown(b));
            var minus = ButtonNamed("Offset of Kitchen Laptop minus 1 millisecond");
            var plus = ButtonNamed("Offset of Kitchen Laptop plus 1 millisecond");
            var box = Assert.Single(Descendants<TextBox>(tab), t => AutomationProperties.GetName(t) == "Exact offset of Kitchen Laptop in milliseconds" && Shown(t));
            var slider = Assert.Single(Descendants<Slider>(tab), s => AutomationProperties.GetName(s) == "Offset of Kitchen Laptop in milliseconds");
            Assert.Same(Application.Current.FindResource("SecondaryButton"), minus.Style);
            Assert.Same(Application.Current.FindResource("SecondaryButton"), plus.Style);
            Assert.Equal(56, box.ActualWidth, 0.5);
            Assert.Equal("10", box.Text);   // the client's stored offset

            // Everything stays inside the card at the minimum width, and the client name is not squeezed.
            var card = Descendants<Border>(tab).First(b => Shown(b) && b.Style == Application.Current.FindResource("CardBorder")
                && Descendants<TextBlock>(b).Any(t => t.Text == "Kitchen Laptop"));
            foreach (FrameworkElement e in new FrameworkElement[] { minus, plus, box, slider })
            {
                Assert.True(e.ActualWidth > 0);
                Assert.True(Right(e, card) <= card.ActualWidth + 0.5, $"'{AutomationProperties.GetName(e)}' sticks out of its card");
            }
            var name = Assert.Single(Descendants<TextBlock>(tab), t => t.Text == "Kitchen Laptop");
            Assert.True(name.ActualWidth >= NaturalWidth("Kitchen Laptop", "SubtitleText") - 0.5, "the name is not squeezed");

            // The buttons step the offset by 1 ms, and the box follows.
            plus.Command!.Execute(plus.CommandParameter);
            Assert.Equal(11, row.OffsetMs);
            minus.Command!.Execute(minus.CommandParameter);
            minus.Command.Execute(minus.CommandParameter);
            Assert.Equal(9, row.OffsetMs);
            UiDispatcher.Render(tab, MinPageWidth, MinPageHeight);
            Assert.Equal("9", box.Text);
            Assert.Equal(9, slider.Value);

            // Typing and leaving the box commits; Enter is bound to the same command.
            box.Text = "-12.5";
            Assert.Equal(9, row.OffsetMs);   // not yet
            box.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Assert.Equal(-12.5, row.OffsetMs);
            Assert.Equal("-12.5", box.Text);
            var enter = Assert.Single(box.InputBindings.OfType<System.Windows.Input.KeyBinding>());
            Assert.Equal(System.Windows.Input.Key.Return, enter.Key);
            box.Text = "abc";
            enter.Command!.Execute(enter.CommandParameter);
            Assert.Equal("-12.5", box.Text);   // invalid text reverts
            Assert.Equal(-12.5, row.OffsetMs);

            Assert.Empty(recorder.Errors);
        });
    }

    [Fact]
    public void Session_name_ellipsizes_while_the_chip_and_stop_button_stay_whole()
    {
        UiDispatcher.Run(() =>
        {
            string longSession = new('S', 64);
            var tab = NewTab(NewHost(longSession), longSession);
            UiDispatcher.Render(tab, MinPageWidth, MinPageHeight);

            var name = Assert.Single(Descendants<TextBlock>(tab), t => t.Text == longSession);
            Assert.Equal(TextTrimming.CharacterEllipsis, name.TextTrimming);
            Assert.True(name.ActualWidth < NaturalWidth(longSession, "TitleText") - 50, "the name is trimmed");
            var chip = Assert.Single(Descendants<StatusChip>(tab), c => Shown(c) && c.Kind == StatusKind.Hosting && c.Text == "Hosting");
            var stop = Assert.Single(Descendants<Button>(tab), b => Equals(b.Content, "Stop hosting") && Shown(b));

            Assert.True(chip.ActualWidth > 40, "the chip is whole");
            Assert.True(Right(name, tab) + 12 <= Left(chip, tab) + 0.5, "the chip follows the name");
            Assert.True(Right(chip, tab) <= Left(stop, tab) + 0.5, "the chip ends before the Stop button");
            Assert.True(Right(stop, tab) <= MinPageWidth + 0.5, "the Stop button is fully on screen");
            Assert.True(stop.ActualWidth >= NaturalWidth("Stop hosting", "BodyText"), "the button shows its whole label");
        });
    }

    [Fact]
    public void Session_chip_sits_right_after_a_short_name_and_stop_is_alone_on_the_right()
    {
        UiDispatcher.Run(() =>
        {
            var tab = NewTab(NewHost("Living Room"), "Living Room");
            UiDispatcher.Render(tab, MinPageWidth, MinPageHeight);

            var name = Assert.Single(Descendants<TextBlock>(tab), t => t.Text == "Living Room");
            var chip = Assert.Single(Descendants<StatusChip>(tab), c => Shown(c) && c.Kind == StatusKind.Hosting && c.Text == "Hosting");
            var stop = Assert.Single(Descendants<Button>(tab), b => Equals(b.Content, "Stop hosting") && Shown(b));

            Assert.Equal(NaturalWidth("Living Room", "TitleText"), name.ActualWidth, 0.5);
            Assert.Equal(Right(name, tab) + 12, Left(chip, tab), 0.5);   // right after the name
            Assert.True(Left(stop, tab) - Right(chip, tab) > 50, "only the Stop button is pushed to the right");
            Assert.Equal(MinPageWidth, Right(stop, tab), 0.5);
        });
    }

    [Fact]
    public void LeadingShrinkPanel_shrinks_only_its_first_child()
    {
        UiDispatcher.Run(() =>
        {
            var first = new TextBlock { Text = new string('W', 40), TextTrimming = TextTrimming.CharacterEllipsis, Style = (Style)Application.Current.FindResource("BodyText") };
            var second = new Border { Width = 70, Height = 10, Margin = new Thickness(12, 0, 0, 0) };
            var panel = new LeadingShrinkPanel { Children = { first, second } };
            var host = new Grid { Children = { panel } };
            double natural = NaturalWidth(first.Text, "BodyText");
            Assert.True(natural > 300);

            UiDispatcher.Render(host, 300, 40);
            Assert.InRange(first.ActualWidth, 200, 300 - 82 + 0.5);   // gave way
            Assert.Equal(70, second.ActualWidth, 0.5);                  // whole, right after it
            Assert.Equal(first.ActualWidth + 12, second.TranslatePoint(new Point(0, 0), panel).X, 0.5);
            Assert.True(second.TranslatePoint(new Point(70, 0), panel).X <= 300.5);

            UiDispatcher.Render(host, 600, 40);   // room for both: nothing shrinks, nothing is pushed away
            Assert.Equal(natural, first.ActualWidth, 0.5);
            Assert.Equal(natural + 12, second.TranslatePoint(new Point(0, 0), panel).X, 0.5);
        });
    }

    [Fact]
    public void Host_markup_uses_the_design_components()
    {
        var tab = XDocument.Parse(ViewXaml("HostTabView.xaml"));
        var host = XDocument.Parse(ViewXaml("HostView.xaml"));

        string StyleOf(XDocument doc, string content) =>
            (string)Assert.Single(doc.Descendants(Wpf + "Button"), b => (string?)b.Attribute("Content") == content).Attribute("Style")!;

        Assert.Contains("DangerButton", StyleOf(tab, "Stop hosting"));
        Assert.Contains("PrimaryButton", StyleOf(tab, "Start hosting"));
        Assert.NotEmpty(tab.Descendants(Controls + "StatusChip"));
        Assert.NotEmpty(host.Descendants(Controls + "StatusChip"));
        Assert.NotEmpty(host.Descendants(Controls + "Banner"));
        Assert.Contains("ToggleSwitch", (string)Assert.Single(host.Descendants(Wpf + "CheckBox")).Attribute("Style")!);
        Assert.Equal("HostScroll", (string)Assert.Single(host.Descendants(Wpf + "ScrollViewer")).Attribute(Xaml + "Name")!);
        Assert.DoesNotContain(host.Descendants(Wpf + "Slider"), s => s.Attribute("TickPlacement") is not null);
    }

    [Fact]
    public void SyncStateToKind_converter_maps_states_to_chip_kinds()
    {
        var converter = new SyncStateToKindConverter();
        var culture = System.Globalization.CultureInfo.InvariantCulture;

        Assert.Equal(StatusKind.InSync, converter.Convert(SyncState.InSync, typeof(StatusKind), null!, culture));
        Assert.Equal(StatusKind.Syncing, converter.Convert(SyncState.Syncing, typeof(StatusKind), null!, culture));
        Assert.Equal(StatusKind.Degraded, converter.Convert(SyncState.Degraded, typeof(StatusKind), null!, culture));
        Assert.Equal(StatusKind.Connecting, converter.Convert(SyncState.Connecting, typeof(StatusKind), null!, culture));
        Assert.Equal(StatusKind.NotConnected, converter.Convert(SyncState.Disconnected, typeof(StatusKind), null!, culture));
        Assert.Equal(StatusKind.NotConnected, converter.Convert(null!, typeof(StatusKind), null!, culture));
        Assert.Equal(StatusKind.NotConnected, converter.Convert("not a state", typeof(StatusKind), null!, culture));
        Assert.Throws<NotSupportedException>(() => { converter.ConvertBack(StatusKind.InSync, typeof(SyncState), null!, culture); });
    }
}
