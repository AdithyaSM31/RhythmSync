using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
using RhythmSync.App.Controls;
using RhythmSync.App.Tests.Ui;
using RhythmSync.App.ViewModels;
using RhythmSync.App.Views;
using Page = RhythmSync.App.ViewModels.Page;

namespace RhythmSync.App.Tests;

/// <summary>The shell (MainWindow, never shown) lays out every page with the real theme and no binding errors.</summary>
[Collection("Ui")]
public class MainWindowSmokeTests
{
    private const string HostingNotice = "This PC is hosting – stop hosting to join another host.";

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    private static T Named<T>(DependencyObject root, string automationName) where T : DependencyObject =>
        Assert.Single(Descendants<T>(root), e => AutomationProperties.GetName(e) == automationName);

    private static (MainWindow Window, MainViewModel Vm, ShellTests.FakeRuntime Runtime) Make()
    {
        var runtime = new ShellTests.FakeRuntime();
        var vm = new MainViewModel(ShellTests.Client(), host: new HostTabViewModel(runtime, "My PC"));
        var window = new MainWindow { DataContext = vm };
        return (window, vm, runtime);
    }

    /// <summary>
    /// An unshown window is Collapsed, so measuring it lays out nothing. The test applies the window's template and lays
    /// out the template's root instead: everything stays under the window, so bindings to the Window still resolve.
    /// </summary>
    private static void Layout(MainWindow window)
    {
        window.ApplyTemplate();
        UiDispatcher.Render((FrameworkElement)VisualTreeHelper.GetChild(window, 0), 960, 640);
    }

    private static ContentPresenter ShownPage(MainWindow window) =>
        Assert.Single(Descendants<ContentPresenter>(window), p => p.Content is MainViewModel);

    [Fact]
    public void MainWindow_renders_each_page_without_errors()
    {
        UiDispatcher.Run(() =>
        {
            using var recorder = new BindingErrorRecorder();
            var (window, vm, _) = Make();

            foreach (var page in Enum.GetValues<Page>())
            {
                vm.SelectedPage = page;
                Layout(window);

                var shown = ShownPage(window);
                switch (page)
                {
                    case Page.Host: Assert.Single(Descendants<HostTabView>(shown)); break;
                    case Page.Join: Assert.Single(Descendants<ClientView>(shown)); break;
                    case Page.Settings:
                        Assert.Contains(Descendants<TextBlock>(shown), t => t.Text == "Settings");
                        break;
                }
                Assert.True(Named<RadioButton>(window, page.ToString()).IsChecked, $"{page} nav item is not selected");
            }

            Assert.Empty(recorder.Errors);
        });
    }

    [Fact]
    public void Window_uses_the_studio_chrome_and_size()
    {
        UiDispatcher.Run(() =>
        {
            var (window, _, _) = Make();
            Layout(window);

            Assert.Equal(Application.Current.FindResource("BgBrush"), window.Background);
            Assert.Equal((960d, 640d, 760d, 520d), (window.Width, window.Height, window.MinWidth, window.MinHeight));

            var chrome = WindowChrome.GetWindowChrome(window);
            Assert.NotNull(chrome);
            Assert.Equal(32, chrome.CaptionHeight);
            Assert.Equal(new Thickness(6), chrome.ResizeBorderThickness);
            Assert.False(chrome.UseAeroCaptionButtons);

            foreach (var name in new[] { "Minimize", "Maximize", "Close" })
            {
                var button = Named<Button>(window, name);
                Assert.Equal(46, button.ActualWidth);
                Assert.True(WindowChrome.GetIsHitTestVisibleInChrome(button), $"{name} is not clickable in the caption");
            }
            Assert.Contains(Descendants<TextBlock>(window), t => t.Text == "RhythmSync");
        });
    }

    [Fact]
    public void Maximize_turns_into_restore_while_maximized()
    {
        UiDispatcher.Run(() =>
        {
            var (window, _, _) = Make();
            Layout(window);
            var maximize = Named<Button>(window, "Maximize");
            Assert.Contains(Descendants<TextBlock>(maximize), t => t.Text == "");

            window.WindowState = WindowState.Maximized;
            Layout(window);

            Assert.Equal("Restore", AutomationProperties.GetName(maximize));
            Assert.Contains(Descendants<TextBlock>(maximize), t => t.Text == "");
        });
    }

    [Fact]
    public void Nav_items_select_the_page()
    {
        UiDispatcher.Run(() =>
        {
            var (window, vm, _) = Make();
            Layout(window);
            Assert.Equal(Page.Host, vm.SelectedPage);

            Named<RadioButton>(window, "Settings").IsChecked = true;
            Assert.Equal(Page.Settings, vm.SelectedPage);

            Named<RadioButton>(window, "Join").IsChecked = true;
            Assert.Equal(Page.Join, vm.SelectedPage);
            Assert.False(Named<RadioButton>(window, "Settings").IsChecked);
        });
    }

    [Fact]
    public void Footer_card_shows_the_footer_text()
    {
        UiDispatcher.Run(() =>
        {
            var (window, vm, runtime) = Make();
            Layout(window);
            Assert.Contains(Descendants<TextBlock>(window), t => t.Text == "Not connected");

            runtime.Host();
            Layout(window);

            Assert.Equal(MainViewModel.FooterHostingWaiting, vm.FooterText);
            Assert.Contains(Descendants<TextBlock>(window), t => t.Text == MainViewModel.FooterHostingWaiting);
        });
    }

    [Fact]
    public void Join_page_shows_the_banners_and_is_disabled_while_hosting()
    {
        UiDispatcher.Run(() =>
        {
            using var recorder = new BindingErrorRecorder();
            var (window, vm, runtime) = Make();
            vm.SelectedPage = Page.Join;
            Layout(window);

            var banners = Descendants<Banner>(ShownPage(window)).ToList();
            Assert.Equal(2, banners.Count);
            Assert.All(banners, b => Assert.Equal(Visibility.Collapsed, b.Visibility));
            Assert.True(Assert.Single(Descendants<ClientView>(window)).IsEnabled);

            vm.SetPublicNetwork(true);
            runtime.Host();
            Layout(window);

            var network = Assert.Single(banners, b => b.Message == MainViewModel.PublicNetworkText);
            var hosting = Assert.Single(banners, b => b.Message == HostingNotice);
            Assert.Equal(Visibility.Visible, network.Visibility);
            Assert.Equal(Visibility.Visible, hosting.Visibility);
            Assert.Equal(BannerSeverity.Warning, network.Severity);
            Assert.Equal(BannerSeverity.Info, hosting.Severity);
            Assert.False(network.HasAction);
            Assert.False(hosting.HasAction);
            Assert.False(Assert.Single(Descendants<ClientView>(window)).IsEnabled);

            Assert.Empty(recorder.Errors);
        });
    }
}
