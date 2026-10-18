using System.Windows;
using System.Windows.Controls;

namespace RhythmSync.App.Shell;

/// <summary>The custom title bar: app name plus minimize, maximize/restore and close for the window that hosts it.</summary>
public partial class TitleBar : UserControl
{
    public TitleBar()
    {
        InitializeComponent();
    }

    /// <summary>The maximize/restore button, for <see cref="WindowChromeHelper.Attach"/> (snap layouts).</summary>
    public Button MaximizeButton => MaximizeButtonPart;

    private void OnMinimize(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window) SystemCommands.MinimizeWindow(window);
    }

    private void OnMaximizeOrRestore(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not { } window) return;
        if (window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(window);
        else SystemCommands.MaximizeWindow(window);
    }

    /// <summary>Closing goes through the window's OnClosing, which hides it to the tray while the app runs.</summary>
    private void OnClose(object sender, RoutedEventArgs e) => Window.GetWindow(this)?.Close();
}
