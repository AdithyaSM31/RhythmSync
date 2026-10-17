namespace RhythmSync.App.Services;

/// <summary>Notification-area icon with Show/Hide, a status line and Quit. Create and use on the UI thread.</summary>
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
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left) toggleWindow();
        };
    }

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
