using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Serilog;

namespace RhythmSync.App.Shell;

/// <summary>
/// Native touches for the custom title bar (spec §3): the maximize button answers <c>WM_NCHITTEST</c> with
/// <c>HTMAXBUTTON</c> so Windows 11 shows its snap layouts over it, and the window asks DWM for a dark frame.
/// Because Windows then owns the mouse over that button, its hover and click arrive as non-client messages, which
/// are forwarded to the WPF button here. Every native failure is logged and ignored.
/// </summary>
public static class WindowChromeHelper
{
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_NCMOUSEMOVE = 0x00A0;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int WM_NCLBUTTONUP = 0x00A2;
    private const int WM_NCLBUTTONDBLCLK = 0x00A3;
    private const int WM_NCMOUSELEAVE = 0x02A2;
    private const int HTMAXBUTTON = 9;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    /// <summary>True while the pointer is over the button as a non-client <c>HTMAXBUTTON</c> (WPF's IsMouseOver stays false then).</summary>
    public static readonly DependencyProperty IsNonClientHoverProperty = DependencyProperty.RegisterAttached(
        "IsNonClientHover", typeof(bool), typeof(WindowChromeHelper), new FrameworkPropertyMetadata(false));

    public static bool GetIsNonClientHover(DependencyObject element) => (bool)element.GetValue(IsNonClientHoverProperty);

    public static void SetIsNonClientHover(DependencyObject element, bool value) => element.SetValue(IsNonClientHoverProperty, value);

    /// <summary>Hooks the window procedure once the window has a handle, and turns on the dark DWM frame.</summary>
    public static void Attach(Window window, FrameworkElement maximizeButton)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(maximizeButton);

        window.SourceInitialized += (_, _) =>
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                UseDarkFrame(hwnd);
                var tracker = new MaxButtonClickTracker();
                HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr _, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
                    => WndProc(maximizeButton, tracker, msg, wParam, lParam, ref handled));
            }
            catch (Exception e)
            {
                Log.Warning(e, "Could not hook the window chrome");
            }
        };
    }

    /// <summary>True when <paramref name="cursor"/> lies inside <paramref name="buttonScreenRect"/>; the right and bottom edges belong to the neighbours.</summary>
    public static bool HitTest(Rect buttonScreenRect, Point cursor)
        => !buttonScreenRect.IsEmpty
           && cursor.X >= buttonScreenRect.Left && cursor.X < buttonScreenRect.Right
           && cursor.Y >= buttonScreenRect.Top && cursor.Y < buttonScreenRect.Bottom;

    /// <summary>The screen position packed into a mouse message's lParam (signed 16-bit x and y, in physical pixels).</summary>
    public static Point CursorFromLParam(IntPtr lParam)
    {
        long value = lParam.ToInt64();
        return new Point((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
    }

    private static IntPtr WndProc(FrameworkElement button, MaxButtonClickTracker tracker, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        try
        {
            switch (msg)
            {
                case WM_NCHITTEST:
                    if (IsOver(button, CursorFromLParam(lParam)))
                    {
                        handled = true;
                        return new IntPtr(HTMAXBUTTON);
                    }
                    SetIsNonClientHover(button, false);
                    tracker.OnLeave();
                    break;
                case WM_NCMOUSEMOVE:
                    SetIsNonClientHover(button, wParam.ToInt64() == HTMAXBUTTON);
                    break;
                case WM_NCMOUSELEAVE:
                    SetIsNonClientHover(button, false);
                    tracker.OnLeave();
                    break;
                case WM_NCLBUTTONDOWN when wParam.ToInt64() == HTMAXBUTTON:
                    handled = true;   // no system tracking loop: the click happens on release, like a WPF button
                    tracker.OnDown();
                    break;
                case WM_NCLBUTTONDBLCLK when wParam.ToInt64() == HTMAXBUTTON:
                    handled = true;   // not a press: the first click of the pair already toggled the window
                    tracker.OnDoubleClick();
                    break;
                case WM_NCLBUTTONUP when wParam.ToInt64() == HTMAXBUTTON:
                    handled = true;
                    SetIsNonClientHover(button, false);
                    // Only a release that ends a press on the button is a click, never one after a drag from elsewhere.
                    if (tracker.OnUp() && button.IsEnabled) button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
                    break;
            }
        }
        catch (Exception e)
        {
            Log.Warning(e, "Title bar message {Message} failed", msg);
        }
        return IntPtr.Zero;
    }

    private static bool IsOver(FrameworkElement button, Point cursor)
    {
        if (!button.IsVisible || PresentationSource.FromVisual(button) is null) return false;
        var dpi = VisualTreeHelper.GetDpi(button);
        var topLeft = button.PointToScreen(new Point(0, 0));   // already in physical pixels
        var rect = new Rect(topLeft, new Size(button.ActualWidth * dpi.DpiScaleX, button.ActualHeight * dpi.DpiScaleY));
        return HitTest(rect, cursor);
    }

    private static void UseDarkFrame(IntPtr hwnd)
    {
        try
        {
            int on = 1;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));   // fails on older Windows: ignored
        }
        catch (Exception e)
        {
            Log.Debug(e, "Dark window frame not available");
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
