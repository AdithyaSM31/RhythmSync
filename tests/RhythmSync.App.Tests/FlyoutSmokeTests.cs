using System.Net;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RhythmSync.App.Controls;
using RhythmSync.App.Flyout;
using RhythmSync.App.Tests.Ui;
using RhythmSync.App.ViewModels;
using RhythmSync.Core.Clock;
using RhythmSync.Core.Host;
using RhythmSync.Core.Protocol;
using RhythmSync.Platform.Windows;

namespace RhythmSync.App.Tests;

/// <summary>The tray flyout (never shown) lays out every mode with the real theme and no binding errors.</summary>
[Collection("Ui")]
public sealed class FlyoutSmokeTests
{
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    /// <summary>Whether the element and all its ancestors up to the window are Visible (the window itself is never shown).</summary>
    private static bool Shown(DependencyObject element)
    {
        for (DependencyObject? e = element; e is not null and not Window; e = VisualTreeHelper.GetParent(e))
            if (e is UIElement { Visibility: not Visibility.Visible })
                return false;
        return true;
    }

    private static List<string> ShownTexts(DependencyObject root)
        => Descendants<TextBlock>(root).Where(t => Shown(t) && t.Text.Length > 0).Select(t => t.Text).ToList();

    private static List<Button> ShownButtons(DependencyObject root)
        => Descendants<Button>(root).Where(Shown).ToList();

    private static void AssertNoBlackText(DependencyObject root)
    {
        var black = Descendants<TextBlock>(root)
            .Where(t => Shown(t) && t.Text.Length > 0 && t.Foreground is SolidColorBrush { Color: { A: 255, R: 0, G: 0, B: 0 } })
            .Select(t => t.Text).ToList();
        Assert.True(black.Count == 0, "black text on the dark flyout: " + string.Join(", ", black));
    }

    private static Guid Id(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    private static ConnectedClient Pc(int n, string name, SyncState state = SyncState.InSync)
        => new(Id(n), name, new IPEndPoint(IPAddress.Loopback, 5000 + n), true,
            new HeartbeatStats(0.1, 0, 42, 0.18, 0.21, state), 0.8f, 10);

    private sealed class Rig
    {
        public readonly ShellTests.FakeRuntime Runtime = new();
        public readonly ShellTests.FakeControl Control = new();
        public readonly ClientViewModel Client;
        public readonly TrayFlyoutViewModel Vm;
        public readonly TrayFlyoutWindow Window;
        public int Opened;

        public Rig()
        {
            Client = ShellTests.Client(Control);
            Vm = new TrayFlyoutViewModel(new HostTabViewModel(Runtime, "My PC"), Client, () => Opened++);
            Window = new TrayFlyoutWindow { DataContext = Vm };
        }

        public HostViewModel Host(params ConnectedClient[] clients)
        {
            var host = new HostViewModel(new ShellTests.FakeHost(), () => HostAudioStatus.Ok, _ => null, (_, _) => { }, "4729", "Living Room");
            host.UpdateClients(clients);
            Runtime.Host(host);
            return host;
        }

        public void Join()
        {
            Client.RefreshHosts();
            Client.SelectedHost = Client.Hosts[0];
            Client.Code = "4729";
            Client.JoinCommand.ExecuteAsync(null).GetAwaiter().GetResult();   // the fakes complete synchronously
            Control.Become(SyncState.InSync);
        }

        /// <summary>An unshown window is Collapsed, so the test lays out its template's root instead (see MainWindowSmokeTests).</summary>
        public void Layout(double height = 500)
        {
            Window.ApplyTemplate();
            UiDispatcher.Render((FrameworkElement)VisualTreeHelper.GetChild(Window, 0), Window.Width, height);
        }
    }

    [Fact]
    public void Window_is_a_borderless_topmost_tool_window()
    {
        UiDispatcher.Run(() =>
        {
            var rig = new Rig();
            var window = rig.Window;
            rig.Layout();

            Assert.Equal(WindowStyle.None, window.WindowStyle);
            Assert.True(window.AllowsTransparency);
            Assert.True(window.Topmost);
            Assert.False(window.ShowInTaskbar);
            // Ruling U9: the visible panel is 320 px; the window adds the 8 px shadow margin on each side.
            Assert.Equal(336, window.Width);
            Assert.Equal(TrayFlyoutWindow.ShadowMargin, window.Root.Margin.Left);
            Assert.Equal(320, window.Surface.ActualWidth);
            Assert.Equal(SizeToContent.Height, window.SizeToContent);
            Assert.Equal(Application.Current.FindResource("FontUi"), window.FontFamily);
            Assert.Equal(Application.Current.FindResource("TextBrush"), window.Foreground);
            Assert.Equal(TimeSpan.FromMilliseconds(150), ((Duration)Application.Current.FindResource("FlyoutSlideDuration")).TimeSpan);
        });
    }

    [Fact]
    public void Flyout_renders_each_mode_without_errors()
    {
        UiDispatcher.Run(() =>
        {
            using var recorder = new BindingErrorRecorder();
            var rig = new Rig();

            // Idle
            rig.Layout();
            var texts = ShownTexts(rig.Window);
            Assert.Contains("RhythmSync", texts);
            Assert.Contains(TrayFlyoutViewModel.IdleText, texts);
            var chip = Assert.Single(Descendants<StatusChip>(rig.Window), Shown);
            Assert.Equal((StatusKind.Connecting, "Not connected"), (chip.Kind, chip.Text));   // the grey chip
            var open = Assert.Single(ShownButtons(rig.Window));
            Assert.Equal("Open RhythmSync", open.Content);
            Assert.Same(Application.Current.FindResource("PrimaryButton"), open.Style);
            AssertNoBlackText(rig.Window);

            // Hosting, two PCs
            rig.Host(Pc(1, "Kitchen Laptop"), Pc(2, "Study PC", SyncState.Degraded));
            rig.Layout();
            texts = ShownTexts(rig.Window);
            Assert.Contains("Living Room", texts);
            Assert.Contains("Kitchen Laptop", texts);
            Assert.Contains("Study PC", texts);
            Assert.Contains("0.21 ms", texts);
            Assert.Contains("80%", texts);
            Assert.DoesNotContain(TrayFlyoutViewModel.WaitingText, texts);
            Assert.Equal("4729", string.Concat(Descendants<ItemsControl>(rig.Window)
                .Single(i => AutomationProperties.GetName(i) == "Pairing code 4729").Items.Cast<char>()));
            chip = Assert.Single(Descendants<StatusChip>(rig.Window), Shown);
            Assert.Equal((StatusKind.Hosting, "Hosting"), (chip.Kind, chip.Text));
            Assert.Equal(2, Descendants<Slider>(rig.Window).Count(Shown));
            Assert.Contains(Descendants<Slider>(rig.Window), s => AutomationProperties.GetName(s) == "Volume of Kitchen Laptop" && s.Value == 0.8f);
            var buttons = ShownButtons(rig.Window);
            Assert.Equal(["Stop hosting", "Open RhythmSync"], buttons.Select(b => b.Content));
            Assert.Same(Application.Current.FindResource("DangerButton"), buttons[0].Style);
            Assert.Same(Application.Current.FindResource("SecondaryButton"), buttons[1].Style);
            var tabOrder = Descendants<Control>(rig.Window).Where(c => Shown(c) && c is Slider or Button).ToList();
            Assert.True(tabOrder.FindLastIndex(c => c is Slider) < tabOrder.FindIndex(c => c is Button), "the sliders come before the buttons, as on screen");
            AssertNoBlackText(rig.Window);

            // Hosting, nobody yet
            rig.Runtime.StopAsync();
            rig.Host();
            rig.Layout();
            Assert.Contains(TrayFlyoutViewModel.WaitingText, ShownTexts(rig.Window));
            Assert.DoesNotContain(Descendants<Slider>(rig.Window), s => Shown(s));

            // Joined
            rig.Runtime.StopAsync();
            rig.Join();
            rig.Layout();
            texts = ShownTexts(rig.Window);
            Assert.Contains("Living Room", texts);
            Assert.Contains(texts, t => t.Contains("Sync error: ") && t.Contains(" · Drift correction: "));
            Assert.Contains(texts, t => t.Contains("Volume: 100%") && t.Contains("Offset: 0 ms") && t.Contains(ClientViewModel.SetOnHostText));
            chip = Assert.Single(Descendants<StatusChip>(rig.Window), Shown);
            Assert.Equal((StatusKind.InSync, "In sync"), (chip.Kind, chip.Text));
            Assert.Equal(["Leave", "Open RhythmSync"], ShownButtons(rig.Window).Select(b => b.Content));
            Assert.All(ShownButtons(rig.Window), b => Assert.Same(Application.Current.FindResource("SecondaryButton"), b.Style));
            AssertNoBlackText(rig.Window);

            Assert.Empty(recorder.Errors);
        });
    }

    [Fact]
    public void Flyout_with_eight_clients_scrolls_within_max_height()
    {
        UiDispatcher.Run(() =>
        {
            using var recorder = new BindingErrorRecorder();
            var rig = new Rig();
            rig.Host(Enumerable.Range(1, 8).Select(n => Pc(n, $"PC {n}")).ToArray());
            rig.Window.MaxHeight = 500;

            rig.Layout(rig.Window.MaxHeight);

            var scroller = Assert.Single(Descendants<ScrollViewer>(rig.Window), Shown);
            Assert.True(scroller.ScrollableHeight > 0, "eight rows should scroll inside 500 px");
            Assert.True(scroller.ActualHeight < 500);
            Assert.All(ShownButtons(rig.Window), b => Assert.True(b.ActualHeight > 0, $"{b.Content} was squeezed out"));
            Assert.Empty(recorder.Errors);
        });
    }

    [Fact]
    public void Escape_and_the_actions_close_the_flyout()
    {
        UiDispatcher.Run(() =>
        {
            var rig = new Rig();
            var toggle = rig.Window.Toggle;

            toggle.OnTrayClick(DateTime.UtcNow);
            Assert.False(rig.Window.CloseOnKey(Key.Enter));
            Assert.True(toggle.IsOpen);
            Assert.True(rig.Window.CloseOnKey(Key.Escape));
            Assert.False(toggle.IsOpen);

            toggle.OnTrayClick(DateTime.UtcNow);
            rig.Vm.OpenCommand.Execute(null);
            Assert.Equal(1, rig.Opened);
            Assert.False(toggle.IsOpen);
        });
    }

    [Fact]
    public void A_tray_double_click_closes_the_flyout_so_the_next_click_opens_it()
    {
        UiDispatcher.Run(() =>
        {
            var rig = new Rig();
            var toggle = rig.Window.Toggle;
            Assert.True(toggle.OnTrayClick(DateTime.UtcNow));   // the double-click's first click opened it

            rig.Window.CloseFromTray();   // the double-click itself, before the main window toggles

            Assert.False(toggle.IsOpen);
            Assert.Equal(Visibility.Hidden, rig.Window.Visibility);
            Assert.True(toggle.OnTrayClick(DateTime.UtcNow), "a closed-by-action flyout opens on the next click at once");
        });
    }

    [Fact]
    public void Entrance_slides_and_fades_only_when_animations_are_on()
    {
        UiDispatcher.Run(() =>
        {
            var previous = StatusChip.AnimationsEnabled;
            try
            {
                var rig = new Rig();
                rig.Layout();

                StatusChip.AnimationsEnabled = () => false;
                rig.Window.PlayEntrance();
                Assert.False(rig.Window.Root.HasAnimatedProperties);
                Assert.Equal(1, rig.Window.Root.Opacity);
                Assert.Equal(0, rig.Window.Slide.Y);

                StatusChip.AnimationsEnabled = () => true;
                rig.Window.PlayEntrance();
                Assert.True(rig.Window.Root.HasAnimatedProperties);
                Assert.True(rig.Window.Slide.HasAnimatedProperties);
            }
            finally
            {
                StatusChip.AnimationsEnabled = previous;
            }
        });
    }
}
