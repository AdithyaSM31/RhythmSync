namespace RhythmSync.App.Services;

/// <summary>
/// Notification-area icon. Right-click opens the menu (Show/Hide, a status line, Quit); a left click raises
/// <see cref="LeftClick"/> (the tray flyout); a left double-click raises <see cref="LeftDoubleClick"/> (close the flyout,
/// then show or hide the window). Create and use on the UI thread.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int MaxTooltipLength = 63;   // NotifyIcon.Text limit

    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Windows.Forms.ToolStripMenuItem _status;

    public TrayIcon(Action toggleWindow, Action quit)
    {
        ArgumentNullException.ThrowIfNull(toggleWindow);
        ArgumentNullException.ThrowIfNull(quit);

        _status = new System.Windows.Forms.ToolStripMenuItem("RhythmSync") { Enabled = false };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Show/Hide", null, (_, _) => toggleWindow());
        menu.Items.Add(_status);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => quit());

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,   // shared system icon, not disposed here
            Text = "RhythmSync",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left) LeftClick?.Invoke();
        };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left) LeftDoubleClick?.Invoke();
        };
    }

    /// <summary>Raised on the UI thread when the icon is clicked with the left button.</summary>
    public event Action? LeftClick;

    /// <summary>Raised on the UI thread when the icon is double-clicked with the left button (after the first click's <see cref="LeftClick"/>).</summary>
    public event Action? LeftDoubleClick;

    public void SetStatus(string text)
    {
        _status.Text = text;
        string tip = "RhythmSync - " + text;
        _icon.Text = tip.Length <= MaxTooltipLength ? tip : tip[..MaxTooltipLength];
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
