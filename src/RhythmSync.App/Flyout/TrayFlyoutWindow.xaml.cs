using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using RhythmSync.App.Controls;

namespace RhythmSync.App.Flyout;

/// <summary>
/// The tray flyout window (spec §5): borderless, topmost and as tall as its content up to the working area. The visible
/// panel is 320 px wide; the transparent window is 336 px, <see cref="ShadowMargin"/> wider on each side for the shadow.
/// The tray icon's left click calls <see cref="ToggleFromTray"/>; a click elsewhere (deactivation), Esc, or one of the
/// view model's actions hides it again. It is created once and hidden, never closed, while the app runs.
/// </summary>
public partial class TrayFlyoutWindow : Window
{
    /// <summary>Transparent room around the panel for its shadow.</summary>
    internal const double ShadowMargin = 8;

    /// <summary>The gap between the visible panel and the working area's edges.</summary>
    private const double PanelGap = 12;

    /// <summary>How far the panel rises as it fades in.</summary>
    private const double SlideOffset = 12;

    private (Rect Work, Rect Screen)? _area;
    private TrayFlyoutViewModel? _vm;

    public TrayFlyoutWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Deactivated += OnDeactivated;
        SizeChanged += (_, _) => { if (IsVisible) Place(); };   // rows come and go while it is open: stay on the taskbar
    }

    /// <summary>Open or closed, and the guard against the deactivating click reopening it.</summary>
    internal FlyoutToggle Toggle { get; } = new();

    /// <summary>False while the app runs: closing (Alt+F4) only hides the flyout. Set when the app quits.</summary>
    public bool AllowClose { get; set; }

    /// <summary>The tray icon was left-clicked: open the flyout on the monitor under the cursor, or close it.</summary>
    public void ToggleFromTray()
    {
        if (!Toggle.OnTrayClick(DateTime.UtcNow))
        {
            Hide();
            return;
        }

        try
        {
            _area = CursorMonitor();
            Place();
            Show();
            Activate();
            PlayEntrance();
        }
        catch
        {
            Toggle.OnClosedByAction();   // not open after all: the next click tries again
            throw;
        }
    }

    /// <summary>
    /// The tray icon was double-clicked: close the flyout (its first click may have opened it) before the main window
    /// toggles, so the topmost flyout never stays open over the window. The next tray click opens it again at once.
    /// </summary>
    public void CloseFromTray() => CloseByAction();

    /// <summary>Esc closes the flyout. Returns whether <paramref name="key"/> was handled.</summary>
    internal bool CloseOnKey(Key key)
    {
        if (key != Key.Escape) return false;
        CloseByAction();
        return true;
    }

    /// <summary>Slides the panel up 12 px while fading it in, unless Windows animations are off.</summary>
    internal void PlayEntrance()
    {
        Root.BeginAnimation(OpacityProperty, null);
        Slide.BeginAnimation(TranslateTransform.YProperty, null);
        Root.Opacity = 1;
        Slide.Y = 0;
        if (!StatusChip.AnimationsEnabled()) return;

        var duration = TryFindResource("FlyoutSlideDuration") is Duration d ? d : new Duration(TimeSpan.FromMilliseconds(150));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(SlideOffset, 0, duration) { EasingFunction = ease });
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (CloseOnKey(e.Key)) e.Handled = true;
        base.OnPreviewKeyDown(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            CloseByAction();
        }
        base.OnClosing(e);
    }

    private void CloseByAction()
    {
        Toggle.OnClosedByAction();
        Hide();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        Toggle.OnDeactivated(DateTime.UtcNow);
        Hide();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null) _vm.CloseRequested -= CloseByAction;
        _vm = e.NewValue as TrayFlyoutViewModel;
        if (_vm is not null) _vm.CloseRequested += CloseByAction;
    }

    /// <summary>
    /// The working area and bounds of the monitor under the cursor (the primary one if there is none), in DIPs. WinForms
    /// reports screen pixels; they are converted with this window's DPI, which needs the window's handle.
    /// </summary>
    private (Rect Work, Rect Screen) CursorMonitor()
    {
        new WindowInteropHelper(this).EnsureHandle();
        var dpi = VisualTreeHelper.GetDpi(this);
        var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position) ?? System.Windows.Forms.Screen.PrimaryScreen;
        if (screen is null)
            return (SystemParameters.WorkArea, new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight));
        return (ToDips(screen.WorkingArea, dpi), ToDips(screen.Bounds, dpi));
    }

    private static Rect ToDips(System.Drawing.Rectangle pixels, DpiScale dpi)
        => new(pixels.X / dpi.DpiScaleX, pixels.Y / dpi.DpiScaleY, pixels.Width / dpi.DpiScaleX, pixels.Height / dpi.DpiScaleY);

    /// <summary>Caps the height to the working area and moves the window into the corner by the taskbar.</summary>
    private void Place()
    {
        if (_area is not { } area) return;

        // The window is ShadowMargin larger than the panel on every side, so the window keeps that much less of a gap.
        const double margin = PanelGap - ShadowMargin;
        MaxHeight = FlyoutPlacement.Compute(area.Work, area.Screen, new Size(Width, double.PositiveInfinity), margin).Height;

        // Before the first Show the window has no size yet: measure its content at the width it will have.
        double height = IsVisible ? ActualHeight : MeasuredHeight(MaxHeight);
        var (topLeft, _) = FlyoutPlacement.Compute(area.Work, area.Screen, new Size(Width, height), margin);
        Left = topLeft.X;
        Top = topLeft.Y;
    }

    private double MeasuredHeight(double maxHeight)
    {
        // Flush pending layout first: a change below Root (a row added while hidden) only marks that element dirty, and
        // Root would otherwise answer from its cached size.
        Root.UpdateLayout();
        Root.Measure(new Size(Width, maxHeight));
        return Root.DesiredSize.Height;
    }
}
