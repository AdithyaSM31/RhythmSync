using System.ComponentModel;
using System.Windows;
using RhythmSync.App.Shell;

namespace RhythmSync.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        WindowChromeHelper.Attach(this, TitleBarPart.MaximizeButton);   // snap layouts and the dark frame, once there is a handle
    }

    /// <summary>False while the app runs: closing the window only hides it to the tray. Set when the app quits.</summary>
    public bool AllowClose { get; set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    public void ToggleVisibility()
    {
        if (IsVisible) Hide();
        else ShowAndActivate();
    }

    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
}
