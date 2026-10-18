using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IconGen;

/// <summary>
/// Draws the RhythmSync "sync wave" logo on its 128 x 128 design grid (the same artwork as Assets/RhythmSync.svg)
/// and rasterises it at any size. The frames at 24 px and below are simplified for legibility: no echo wave and a
/// thicker stroke, and at 16 px the tile loses its 2 px border as well.
/// </summary>
internal static class LogoRenderer
{
    private const double Grid = 128;
    private const string WavePath = "M24 64 C 34 34, 46 34, 54 64 S 74 94, 84 64 S 98 40, 104 52";

    private static readonly Color Violet = Color.FromRgb(0x8B, 0x7C, 0xFF);
    private static readonly Color Teal = Color.FromRgb(0x4F, 0xD1, 0xC5);

    /// <summary>Renders the logo as a size x size PNG with a transparent background around the tile.</summary>
    public static byte[] RenderPng(int size)
    {
        var bitmap = Render(size);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public static BitmapSource Render(int size)
    {
        bool small = size <= 24;
        bool borderless = size <= 16;

        var gradient = new LinearGradientBrush(Violet, Teal, new Point(24, 64), new Point(104, 64))
        {
            MappingMode = BrushMappingMode.Absolute,
        };
        gradient.Freeze();

        var tileFill = Frozen(new SolidColorBrush(Color.FromRgb(0x0E, 0x0F, 0x13)));
        Pen? tileBorder = borderless ? null : Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x23, 0x27, 0x33))), 2));
        var wave = Geometry.Parse(WavePath);
        wave.Freeze();

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / Grid, size / Grid));
            dc.DrawRoundedRectangle(tileFill, tileBorder, new Rect(6, 6, 116, 116), 30, 30);

            if (!small)
            {
                dc.PushTransform(new TranslateTransform(0, 6));
                dc.PushOpacity(0.35);
                dc.DrawGeometry(null, WavePen(gradient, 7), wave);
                dc.Pop();   // opacity
                dc.Pop();   // translate
            }

            dc.DrawGeometry(null, WavePen(gradient, small ? 13 : 10), wave);
            dc.Pop();       // scale
        }

        var target = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static Pen WavePen(Brush brush, double thickness) => Frozen(new Pen(brush, thickness)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round,
    });

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
