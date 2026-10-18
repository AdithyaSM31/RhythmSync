using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using RhythmSync.App.Services;
using RhythmSync.App.Tests.Ui;
using RhythmSync.App.ViewModels;
using RhythmSync.App.Views;
using RhythmSync.Core.Config;

namespace RhythmSync.App.Tests;

/// <summary>A SettingsViewModel over a fake startup registration and a SettingsStore in a temp directory (never the registry or %AppData%).</summary>
internal sealed class SettingsFixture : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rhythmsync-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsPersister _persister;

    public SettingsFixture()
    {
        var store = new SettingsStore(_dir);
        _persister = new SettingsPersister(store, store.Load() with { DeviceName = "Kitchen Laptop" }, TimeSpan.FromHours(1));
        Startup = new SettingsViewModelTests.FakeStartup();
        Vm = new SettingsViewModel(_persister, Startup, "1.2.3", Opened.Add, Path.Combine(_dir, "data"));
    }

    public SettingsViewModelTests.FakeStartup Startup { get; }
    public List<string> Opened { get; } = new();
    public SettingsViewModel Vm { get; }

    public void Dispose()
    {
        _persister.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}

/// <summary>The Settings page lays out with the real theme, binds without errors and fits the window's minimum content size.</summary>
[Collection("Ui")]
public class SettingsSmokeTests
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

    private static bool Shown(DependencyObject element)
    {
        for (DependencyObject? e = element; e is not null; e = VisualTreeHelper.GetParent(e))
            if (e is UIElement { Visibility: not Visibility.Visible })
                return false;
        return true;
    }

    private static bool HasText(DependencyObject root, string text) => Descendants<TextBlock>(root).Any(t => t.Text == text && Shown(t));

    private static T Named<T>(DependencyObject root, string automationName) where T : DependencyObject =>
        Assert.Single(Descendants<T>(root), e => AutomationProperties.GetName(e) == automationName);

    [Fact]
    public void Settings_page_renders_without_errors()
    {
        UiDispatcher.Run(() =>
        {
            using var recorder = new BindingErrorRecorder();
            using var fixture = new SettingsFixture();
            var view = new SettingsView { DataContext = fixture.Vm };

            UiDispatcher.Render(view, 960, 640);

            foreach (var text in new[]
            {
                "Settings", "This PC's name", "Shown to the host and other PCs", "Start with Windows", "Launch RhythmSync when you sign in",
                "Start minimized", "Open straight to the tray", "About", "RhythmSync 1.2.3",
            })
                Assert.True(HasText(view, text), $"'{text}' is not shown");

            Assert.Equal("Kitchen Laptop", Named<TextBox>(view, "This PC's name").Text);
            Assert.False(Named<CheckBox>(view, "Start with Windows").IsChecked);
            Assert.False(Named<CheckBox>(view, "Start minimized").IsChecked);
            Assert.Equal("Open log folder", Assert.IsType<string>(Named<Button>(view, "Open log folder").Content));
            Assert.Equal("Open settings folder", Assert.IsType<string>(Named<Button>(view, "Open settings folder").Content));
            Assert.False(HasText(view, SettingsViewModel.NameError));
            Assert.False(HasText(view, SettingsViewModel.StartupError));
            Assert.Empty(recorder.Errors);
        });
    }

    [Fact]
    public void Settings_page_fits_the_minimum_content_size()
    {
        UiDispatcher.Run(() =>
        {
            using var fixture = new SettingsFixture();
            fixture.Vm.DeviceName = "";
            fixture.Vm.CommitNameCommand.Execute(null);   // the longest state: an error line under the name
            var view = new SettingsView { DataContext = fixture.Vm };

            UiDispatcher.Render(view, 512, 440);

            Assert.True(HasText(view, SettingsViewModel.NameError));
            Assert.All(Descendants<TextBox>(view).Cast<FrameworkElement>().Concat(Descendants<CheckBox>(view)).Concat(Descendants<Button>(view)),
                c => Assert.True(Within(view, c, 512), $"{c.GetType().Name} sticks out of the 512 px page"));
        });
    }

    private static bool Within(FrameworkElement page, FrameworkElement child, double width)
    {
        var box = child.TransformToAncestor(page).TransformBounds(new Rect(0, 0, child.ActualWidth, child.ActualHeight));
        return box.Left >= -0.5 && box.Right <= width + 0.5;
    }

    [Fact]
    public void Controls_write_through_to_the_view_model()
    {
        UiDispatcher.Run(() =>
        {
            using var fixture = new SettingsFixture();
            var view = new SettingsView { DataContext = fixture.Vm };
            UiDispatcher.Render(view, 960, 640);

            Named<TextBox>(view, "This PC's name").Text = "  Den ";
            Named<CheckBox>(view, "Start minimized").IsChecked = true;
            Named<CheckBox>(view, "Start with Windows").IsChecked = true;

            Assert.Equal("  Den ", fixture.Vm.DeviceName);   // edited text; saved on Enter or lost focus
            Assert.True(fixture.Vm.StartMinimized);
            Assert.True(fixture.Startup.IsEnabled);

            Named<Button>(view, "Open log folder").Command.Execute(null);
            Assert.EndsWith("logs", Assert.Single(fixture.Opened));
        });
    }

    [Fact]
    public void A_failed_startup_change_flips_the_toggle_back_and_shows_the_error()
    {
        UiDispatcher.Run(() =>
        {
            using var recorder = new BindingErrorRecorder();
            using var fixture = new SettingsFixture();
            fixture.Startup.Failure = new UnauthorizedAccessException("denied");
            var view = new SettingsView { DataContext = fixture.Vm };
            UiDispatcher.Render(view, 960, 640);
            var toggle = Named<CheckBox>(view, "Start with Windows");

            toggle.IsChecked = true;
            UiDispatcher.Render(view, 960, 640);

            Assert.False(toggle.IsChecked);
            Assert.True(HasText(view, SettingsViewModel.StartupError));
            Assert.Empty(recorder.Errors);
        });
    }

    [Fact]
    public void Enter_in_the_name_box_saves_the_name()
    {
        UiDispatcher.Run(() =>
        {
            using var fixture = new SettingsFixture();
            var view = new SettingsView { DataContext = fixture.Vm };
            UiDispatcher.Render(view, 960, 640);
            var box = Named<TextBox>(view, "This PC's name");

            box.Text = "  Den ";
            var enter = box.InputBindings.OfType<System.Windows.Input.KeyBinding>().Single(k => k.Key == System.Windows.Input.Key.Enter);

            enter.Command.Execute(null);

            Assert.Equal("Den", fixture.Vm.DeviceName);
            Assert.Null(fixture.Vm.NameErrorText);
        });
    }
}
