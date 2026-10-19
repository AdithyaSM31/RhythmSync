using RhythmSync.Core.Audio.Eq;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using RhythmSync.App.Controls;
using RhythmSync.App.Tests.Ui;
using RhythmSync.App.ViewModels;
using RhythmSync.App.Views;
using RhythmSync.Core.Client;
using RhythmSync.Core.Clock;
using RhythmSync.Core.Net;
using RhythmSync.Core.Protocol;

namespace RhythmSync.App.Tests;

/// <summary>The Join page lays out with the real theme, binds without errors, submits from the pairing boxes and keeps long names inside their cards.</summary>
[Collection("Ui")]
public class JoinPageSmokeTests
{
    private static readonly DiscoveredHost Living = new(1, "Living Room", new IPEndPoint(IPAddress.Parse("192.168.1.24"), 47801), 0);
    private static readonly DiscoveredHost Office = new(2, "Office PC", new IPEndPoint(IPAddress.Parse("192.168.1.31"), 47801), 0);

    private sealed class FakeControl : IClientSessionControl
    {
        public SyncState State { get; set; } = SyncState.Disconnected;
        public event Action? StateChanged;
        public float Volume { get; set; } = 0.8f;
        public double OffsetMs { get; set; } = 10;
        public EqGains Eq { get; set; } = EqGains.Flat;
        public PlaybackStats? Stats { get; set; }
        public long OutputFailures { get; set; }

        public Task LeaveAsync() => Task.CompletedTask;
        public void Raise() => StateChanged?.Invoke();
    }

    private sealed class Page
    {
        public ClientViewModel Vm = null!;
        public ClientView View = null!;
        public int Joins;
    }

    /// <summary>A Join page over a real view model whose fake join succeeds at once and counts its calls.</summary>
    private static Page NewPage(IReadOnlyList<DiscoveredHost> hosts, FakeControl? control = null)
    {
        var page = new Page();
        page.Vm = new ClientViewModel(() => hosts, (_, _) =>
        {
            page.Joins++;
            return Task.FromResult(new JoinResult(JoinOutcome.Joined));
        }, control);
        page.Vm.RefreshHosts();
        page.View = new ClientView { DataContext = page.Vm };
        return page;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    /// <summary>Whether the element and all its ancestors are Visible (the view is never attached to a window).</summary>
    private static bool Shown(DependencyObject element)
    {
        for (DependencyObject? e = element; e is not null; e = VisualTreeHelper.GetParent(e))
            if (e is UIElement { Visibility: not Visibility.Visible })
                return false;
        return true;
    }

    private static TextBlock Text(DependencyObject root, string text) => Assert.Single(Descendants<TextBlock>(root), t => t.Text == text && Shown(t));

    private static bool HasText(DependencyObject root, string text) => Descendants<TextBlock>(root).Any(t => t.Text == text && Shown(t));

    private static IEnumerable<Border> ShownCards(DependencyObject root)
        => Descendants<Border>(root).Where(b => Shown(b) && b.Style == Application.Current.FindResource("CardBorder"));

    private static Color BrushColor(string key) => ((SolidColorBrush)Application.Current.FindResource(key)).Color;

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

    private static void Join(Page page, FakeControl control, DiscoveredHost host, SyncState state)
    {
        page.Vm.SelectedHost = host;
        page.Vm.Code = "4729";
        page.Vm.JoinCommand.Execute(null);   // the fake join completes at once
        control.State = state;
        control.Stats = new PlaybackStats(0.21, 42, 0, 3, 0);
        control.Raise();
        page.Vm.Refresh();
    }

    [Fact]
    public void Join_page_empty_renders_without_errors()
    {
        UiDispatcher.Run(() =>
        {
            using var recorder = new BindingErrorRecorder();
            var page = NewPage([]);
            UiDispatcher.Render(page.View, 960, 640);

            Assert.True(HasText(page.View, "Join a host"));
            Assert.True(HasText(page.View, "Hosts on this network"));
            Assert.True(HasText(page.View, ClientViewModel.SearchingText));
            Assert.True(HasText(page.View, "Code"));
            var join = Assert.Single(Descendants<Button>(page.View), b => Equals(b.Content, "Join"));
            Assert.Same(Application.Current.FindResource("PrimaryButton"), join.Style);
            Assert.False(join.IsEnabled);
            Assert.Single(Descendants<PinBox>(page.View));
            Assert.Empty(ShownCards(page.View));   // no host cards, and the status card is hidden
            Assert.DoesNotContain(Descendants<StatusChip>(page.View), c => Shown(c));
            Assert.Empty(recorder.Errors);
        });
    }

    [Fact]
    public void Join_page_with_hosts_renders_without_errors()
    {
        UiDispatcher.Run(() =>
        {
            using var recorder = new BindingErrorRecorder();
            var page = NewPage([Living, Office]);
            UiDispatcher.Render(page.View, 960, 640);

            Assert.False(HasText(page.View, ClientViewModel.SearchingText));
            var list = Assert.Single(Descendants<ListBox>(page.View));
            Assert.Equal(2, list.Items.Count);
            Assert.True(HasText(page.View, "Living Room"));
            Assert.True(HasText(page.View, "192.168.1.24"));
            Assert.True(HasText(page.View, "Office PC"));
            Assert.Equal(2, ShownCards(page.View).Count());

            // Picking a host (two-way) selects its card and enables Join once the code is complete.
            var join = Assert.Single(Descendants<Button>(page.View), b => Equals(b.Content, "Join"));
            list.SelectedItem = Office;
            Assert.Same(Office, page.Vm.SelectedHost);
            Assert.False(join.IsEnabled);
            var pin = Assert.Single(Descendants<PinBox>(page.View));
            pin.Code = "4729";
            Assert.Equal("4729", page.Vm.Code);
            Assert.True(join.IsEnabled);

            page.Vm.ErrorText = "Wrong code";
            UiDispatcher.Render(page.View, 960, 640);
            Assert.Equal(BrushColor("DangerBrush"), ((SolidColorBrush)Text(page.View, "Wrong code").Foreground).Color);
            Assert.Empty(recorder.Errors);
        });
    }

    [Fact]
    public void Selected_host_card_has_an_accent_border_and_the_others_do_not()
    {
        UiDispatcher.Run(() =>
        {
            var page = NewPage([Living, Office]);
            page.Vm.SelectedHost = Living;
            UiDispatcher.Render(page.View, 960, 640);

            var accent = BrushColor("AccentBrush");
            Color BorderOf(string name)
            {
                var card = ShownCards(page.View).Single(b => Descendants<TextBlock>(b).Any(t => t.Text == name));
                return ((SolidColorBrush)card.BorderBrush).Color;
            }

            Assert.Equal(accent, BorderOf("Living Room"));
            Assert.NotEqual(accent, BorderOf("Office PC"));

            page.Vm.SelectedHost = Office;
            UiDispatcher.Render(page.View, 960, 640);
            Assert.Equal(accent, BorderOf("Office PC"));
            Assert.NotEqual(accent, BorderOf("Living Room"));
        });
    }

    private static void AssertNoBlackText(DependencyObject root)
    {
        var black = Descendants<TextBlock>(root)
            .Where(t => Shown(t) && t.Text.Length > 0 && t.Foreground is SolidColorBrush { Color: { A: 255, R: 0, G: 0, B: 0 } })
            .Select(t => t.Text).ToList();
        Assert.True(black.Count == 0, "black text on the dark page: " + string.Join(", ", black));
    }

    /// <summary>Hosts the view the way the Window does (it supplies the inherited text colour), so only text that cuts the inheritance can turn out black.</summary>
    private static Grid InWindowText(ClientView view) => new()
    {
        Children = { view },
    };

    [Fact]
    public void Host_names_and_all_other_text_are_never_black()
    {
        UiDispatcher.Run(() =>
        {
            var control = new FakeControl();
            var page = NewPage([Living, Office], control);
            page.Vm.SelectedHost = Living;
            page.Vm.ErrorText = "Wrong code";
            var shell = InWindowText(page.View);
            TextElement.SetForeground(shell, (Brush)Application.Current.FindResource("TextBrush"));
            UiDispatcher.Render(shell, 960, 640);

            Assert.Equal(BrushColor("TextBrush"), ((SolidColorBrush)Text(shell, "Living Room").Foreground).Color);
            Assert.Equal(BrushColor("TextBrush"), ((SolidColorBrush)Text(shell, "Office PC").Foreground).Color);
            AssertNoBlackText(shell);

            Join(page, control, Living, SyncState.InSync);
            UiDispatcher.Render(shell, 960, 640);
            AssertNoBlackText(shell);

            var empty = NewPage([]);
            var emptyShell = InWindowText(empty.View);
            TextElement.SetForeground(emptyShell, (Brush)Application.Current.FindResource("TextBrush"));
            UiDispatcher.Render(emptyShell, 960, 640);
            AssertNoBlackText(emptyShell);
        });
    }

    [Fact]
    public void Host_cards_sit_far_enough_inside_the_viewport_for_the_selection_and_focus_rings()
    {
        UiDispatcher.Run(() =>
        {
            var page = NewPage([Living, Office]);
            page.Vm.SelectedHost = Living;
            UiDispatcher.Render(page.View, MinPageWidth, MinPageHeight);

            // 3 px selection ring + 4 px keyboard focus visual (StudioFocusVisual, Margin -4) around the item's own bounds.
            var viewer = Descendants<ScrollViewer>(page.View).First();
            var items = Descendants<ListBoxItem>(page.View).ToList();
            Assert.Equal(2, items.Count);
            foreach (var item in items)
            {
                Assert.True(Left(item, viewer) >= 4 - 0.5, $"a host item starts {Left(item, viewer)} px from the viewport edge");
                Assert.True(Right(item, viewer) + 4 <= viewer.ViewportWidth + 0.5, "and the focus ring fits on the right");
            }

            // The cards stay aligned with the page title.
            var title = Text(page.View, "Join a host");
            var card = ShownCards(page.View).First();
            Assert.Equal(Left(title, page.View), Left(card, page.View), 0.5);
        });
    }

    [Fact]
    public void Join_page_joined_renders_without_errors()
    {
        UiDispatcher.Run(() =>
        {
            using var recorder = new BindingErrorRecorder();
            var control = new FakeControl();
            var page = NewPage([Living, Office], control);
            UiDispatcher.Render(page.View, 960, 640);
            Assert.DoesNotContain(Descendants<StatusChip>(page.View), c => Shown(c));   // the status card is hidden until connected

            Join(page, control, Living, SyncState.InSync);
            UiDispatcher.Render(page.View, 960, 640);

            Assert.True(HasText(page.View, "Joined · Living Room"));
            var chip = Assert.Single(Descendants<StatusChip>(page.View), c => Shown(c));
            Assert.Equal(StatusKind.InSync, chip.Kind);
            Assert.Equal("In sync", chip.Text);
            var leave = Assert.Single(Descendants<Button>(page.View), b => Equals(b.Content, "Leave"));
            Assert.Same(Application.Current.FindResource("SecondaryButton"), leave.Style);
            Assert.True(Shown(leave));

            var stats = Assert.Single(Descendants<TextBlock>(page.View), t => Shown(t) && t.Text.StartsWith("Sync error: ", StringComparison.Ordinal));
            Assert.Equal(page.Vm.SyncErrorText + " · " + page.Vm.DriftText + " · " + page.Vm.CountersText, stats.Text);
            Assert.Same(Application.Current.FindResource("StatText"), stats.Style);
            Assert.True(HasText(page.View, "Volume: 80%"));
            Assert.True(HasText(page.View, "Offset: +10 ms"));
            Assert.True(HasText(page.View, ClientViewModel.SetOnHostText));
            Assert.Empty(Descendants<Slider>(page.View));

            // A problem next to the status shows in Warning.
            control.State = SyncState.Syncing;
            control.OutputFailures = 3;
            control.Raise();
            page.Vm.Refresh();
            UiDispatcher.Render(page.View, 960, 640);
            Assert.Equal(BrushColor("WarningBrush"), ((SolidColorBrush)Text(page.View, ClientViewModel.AudioDeviceText).Foreground).Color);
            Assert.Equal(StatusKind.Syncing, Assert.Single(Descendants<StatusChip>(page.View), c => Shown(c)).Kind);
            Assert.Empty(recorder.Errors);
        });
    }

    [Fact]
    public void The_status_card_is_guarded_by_the_connection_not_by_the_joined_name()
    {
        UiDispatcher.Run(() =>
        {
            using var recorder = new BindingErrorRecorder();
            var control = new FakeControl();
            var page = NewPage([Living], control);

            // Joined, but the session is still Disconnected: JoinedHostName is set, the card must stay hidden.
            page.Vm.SelectedHost = Living;
            page.Vm.Code = "4729";
            page.Vm.JoinCommand.Execute(null);
            Assert.Equal("Living Room", page.Vm.JoinedHostName);
            Assert.False(page.Vm.IsConnected);
            UiDispatcher.Render(page.View, 960, 640);
            Assert.False(HasText(page.View, "Joined · Living Room"));
            Assert.DoesNotContain(Descendants<Button>(page.View), b => Equals(b.Content, "Leave") && Shown(b));

            // Connected without a known host name (for example a reconnect): no dangling separator.
            page.Vm.JoinedHostName = null;
            control.State = SyncState.Connecting;
            control.Raise();
            UiDispatcher.Render(page.View, 960, 640);
            Assert.True(HasText(page.View, "Joined"));
            Assert.Equal(StatusKind.Connecting, Assert.Single(Descendants<StatusChip>(page.View), c => Shown(c)).Kind);
            Assert.Empty(recorder.Errors);
        });
    }

    [Fact]
    public void Enter_in_pin_box_runs_join_when_enabled()
    {
        UiDispatcher.Run(() =>
        {
            using var recorder = new BindingErrorRecorder();
            var page = NewPage([Living, Office]);
            UiDispatcher.Render(page.View, 960, 640);
            var pin = Assert.Single(Descendants<PinBox>(page.View));

            // No host picked: the code is complete and Enter submits it, but Join cannot run.
            pin.HandleText("4729");
            Assert.Equal("4729", page.Vm.Code);
            Assert.True(pin.HandleKey(Key.Enter));
            Assert.Equal(0, page.Joins);

            page.Vm.SelectedHost = Living;
            Assert.True(pin.HandleKey(Key.Enter));
            Assert.Equal(1, page.Joins);
            Assert.Equal("Living Room", page.Vm.JoinedHostName);

            // An incomplete code is not submitted at all.
            pin.HandleKey(Key.Back);
            Assert.Equal("472", page.Vm.Code);
            Assert.False(pin.HandleKey(Key.Enter));
            Assert.Equal(1, page.Joins);
            Assert.Empty(recorder.Errors);
        });
    }

    [Fact]
    public void Long_host_names_ellipsize_at_minimum_size()
    {
        UiDispatcher.Run(() =>
        {
            string normal = "Living Room";
            string longName = new('H', 64);
            var longHost = new DiscoveredHost(3, longName, new IPEndPoint(IPAddress.Parse("192.168.1.99"), 47801), 0);
            var page = NewPage([Living, longHost]);
            UiDispatcher.Render(page.View, MinPageWidth, MinPageHeight);

            var living = Text(page.View, normal);
            Assert.True(living.ActualWidth >= NaturalWidth(normal, "SubtitleText") - 0.5,
                $"'{normal}' is squeezed to {living.ActualWidth} (needs {NaturalWidth(normal, "SubtitleText")})");

            var long64 = Text(page.View, longName);
            Assert.Equal(TextTrimming.CharacterEllipsis, long64.TextTrimming);
            Assert.True(long64.ActualWidth < NaturalWidth(longName, "SubtitleText") - 50, "a 64-character name is trimmed");

            // Its IP stays whole, and everything stays inside the card and the page.
            var card = ShownCards(page.View).Single(b => Descendants<TextBlock>(b).Any(t => t.Text == longName));
            var ip = Text(card, "192.168.1.99");
            Assert.True(ip.ActualWidth >= NaturalWidth("192.168.1.99", "CaptionText") - 0.5);
            foreach (var t in Descendants<TextBlock>(card).Where(t => Shown(t) && t.ActualWidth > 0))
                Assert.True(Right(t, card) <= card.ActualWidth + 0.5, $"'{t.Text}' sticks out of its card");
            Assert.True(Right(card, page.View) <= MinPageWidth + 0.5, "the card stays inside the page");
        });
    }

    [Fact]
    public void A_long_joined_host_name_ellipsizes_while_the_chip_and_Leave_stay_whole()
    {
        UiDispatcher.Run(() =>
        {
            string longName = new('H', 64);
            var longHost = new DiscoveredHost(3, longName, new IPEndPoint(IPAddress.Parse("192.168.1.99"), 47801), 0);
            var control = new FakeControl();
            var page = NewPage([longHost], control);
            Join(page, control, longHost, SyncState.Degraded);
            UiDispatcher.Render(page.View, MinPageWidth, MinPageHeight);

            string joinedText = "Joined · " + longName;
            var title = Text(page.View, joinedText);
            Assert.Equal(TextTrimming.CharacterEllipsis, title.TextTrimming);
            Assert.True(title.ActualWidth < NaturalWidth(joinedText, "SubtitleText") - 50, "the name is trimmed");
            var chip = Assert.Single(Descendants<StatusChip>(page.View), c => Shown(c));
            var leave = Assert.Single(Descendants<Button>(page.View), b => Equals(b.Content, "Leave") && Shown(b));
            Assert.True(chip.ActualWidth > 40, "the chip is whole");
            Assert.True(Right(title, page.View) + 12 <= Left(chip, page.View) + 0.5, "the chip follows the name");
            Assert.True(Right(chip, page.View) <= Left(leave, page.View) + 0.5, "the chip ends before Leave");
            Assert.True(Right(leave, page.View) <= MinPageWidth + 0.5, "Leave is fully on screen");

            // The stat line wraps inside the card instead of running off it.
            var stats = Assert.Single(Descendants<TextBlock>(page.View), t => Shown(t) && t.Text.StartsWith("Sync error: ", StringComparison.Ordinal));
            Assert.True(Right(stats, page.View) <= MinPageWidth + 0.5);
        });
    }
}
