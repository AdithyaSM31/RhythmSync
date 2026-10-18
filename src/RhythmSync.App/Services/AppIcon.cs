using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RhythmSync.App.Services;

/// <summary>
/// The RhythmSync logo, from the multi-frame Assets/RhythmSync.ico that is compiled into the app as a WPF resource
/// (16 to 256 px; the exe icon is the same file). The tray icon needs a GDI icon, WPF windows an image source.
/// </summary>
public static class AppIcon
{
    /// <summary>Where the .ico lives; the same URI the XAML uses (MainWindow's Icon, the title bar's logo).</summary>
    public static Uri ResourceUri { get; } = new("pack://application:,,,/RhythmSync;component/Assets/RhythmSync.ico", UriKind.Absolute);

    private static readonly Lazy<ImageSource> Window = new(CreateWindowIcon);

    /// <summary>
    /// The logo as a WPF image source for <c>Window.Icon</c>. It is frozen, so any window may share it; Windows picks the
    /// frame that fits (taskbar, Alt+Tab, title bar) from the decoder's frames.
    /// </summary>
    public static ImageSource WindowIcon => Window.Value;

    /// <summary>
    /// Loads the frame of the logo that best fits <paramref name="size"/> as a GDI icon. The caller owns the icon and
    /// disposes it (a NotifyIcon does not).
    /// </summary>
    public static System.Drawing.Icon Load(System.Drawing.Size size)
    {
        using var stream = OpenIcoStream();
        return new System.Drawing.Icon(stream, size);
    }

    /// <summary>Opens the embedded .ico. Works in the app and under a plain Application in tests.</summary>
    internal static Stream OpenIcoStream()
    {
        var info = Application.GetResourceStream(ResourceUri)
                   ?? throw new InvalidOperationException($"The app icon resource was not found: {ResourceUri}");
        return info.Stream;
    }

    private static ImageSource CreateWindowIcon()
    {
        var frame = BitmapFrame.Create(ResourceUri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        frame.Freeze();
        return frame;
    }
}
