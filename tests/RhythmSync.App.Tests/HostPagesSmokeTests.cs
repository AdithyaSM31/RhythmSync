using System.Net;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using RhythmSync.App.Controls;
using RhythmSync.App.Tests.Ui;
using RhythmSync.App.ViewModels;
using RhythmSync.App.Views;
using RhythmSync.Core.Clock;
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

    private static ConnectedClient Client(int n, string name, SyncState state, double latePct = 0)
        => new(Id(n), name, new IPEndPoint(IPAddress.Loopback, 5000 + n), true,
            new HeartbeatStats(0.1, latePct, 42, 0.18, 0.21, state), 0.8f, 10);

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

            var chips = Descendants<StatusChip>(tab).Where(c => Shown(c)).ToList();
            Assert.Equal([StatusKind.Hosting, StatusKind.InSync, StatusKind.Degraded], chips.Select(c => c.Kind));
            Assert.Equal(["Hosting", "In sync", "Sync is degraded"], chips.Select(c => c.Text));

            var banner = Assert.Single(Descendants<Banner>(tab), b => Shown(b));
            Assert.Equal(HostViewModel.VbCableNotDefaultBanner, banner.Message);
            Assert.Equal("Open sound settings", banner.ActionText);

            // The code is one big glyph per character, and the speaker picker sits in the settings card.
            var code = Descendants<ItemsControl>(tab).Single(i => Equals(i.ItemsSource, "4729"));
            Assert.Equal(4, code.Items.Count);
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

    [Fact]
    public void Long_names_ellipsize_at_minimum_size()
    {
        UiDispatcher.Run(() =>
        {
            string longSession = new('S', 64);
            string longClient = new('C', 64);
            var host = NewHost(longSession);
            host.UpdateClients([Client(1, longClient, SyncState.InSync, latePct: 9)]);
            var tab = NewTab(host, longSession);
            UiDispatcher.Render(tab, 760, 520);

            foreach (string name in new[] { longSession, longClient })
            {
                var block = Assert.Single(Descendants<TextBlock>(tab), t => t.Text == name);
                Assert.Equal(TextTrimming.CharacterEllipsis, block.TextTrimming);
                var container = (FrameworkElement)VisualTreeHelper.GetParent(block);
                Assert.True(block.ActualWidth <= container.ActualWidth + 0.5,
                    $"{name[0]}-name is {block.ActualWidth} wide inside {container.ActualWidth}");
                Assert.True(block.ActualWidth < tab.ActualWidth - 100, "the name leaves room for the chip and the button");
            }

            // The right-hand controls stay on screen at the minimum size.
            var stop = Assert.Single(Descendants<Button>(tab), b => Equals(b.Content, "Stop hosting"));
            Assert.True(stop.TranslatePoint(new Point(stop.ActualWidth, 0), tab).X <= 760);
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
