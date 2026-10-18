using System.Buffers.Binary;
using System.Windows;
using System.Windows.Media;
using RhythmSync.App.Services;
using RhythmSync.App.Tests.Ui;

namespace RhythmSync.App.Tests;

/// <summary>
/// The RhythmSync logo ships inside the app as a WPF resource (Assets/RhythmSync.ico). It is read through the pack URI
/// on the UI test dispatcher, which hosts a plain Application; nothing here creates the real App, a tray icon or a window.
/// </summary>
[Collection("Ui")]
public sealed class AppIconTests
{
    private static readonly int[] ExpectedFrames = [16, 20, 24, 32, 40, 48, 64, 256];

    private static byte[] ReadIco()
    {
        byte[]? bytes = null;
        UiDispatcher.Run(() =>
        {
            using var stream = AppIcon.OpenIcoStream();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            bytes = copy.ToArray();
        });
        return bytes!;
    }

    /// <summary>Reads the ICONDIR/ICONDIRENTRY rows: (width, height, bits per pixel, payload size, payload offset).</summary>
    private static List<(int Width, int Height, int Bpp, int Size, int Offset)> ReadDirectory(byte[] ico)
    {
        Assert.True(ico.Length >= 6, "The file is shorter than an ICONDIR.");
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(0)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2)));   // 1 = icon (2 would be a cursor)
        int count = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4));

        var frames = new List<(int, int, int, int, int)>();
        for (int i = 0; i < count; i++)
        {
            var entry = ico.AsSpan(6 + 16 * i, 16);
            int width = entry[0] == 0 ? 256 : entry[0];     // 0 stands for 256
            int height = entry[1] == 0 ? 256 : entry[1];
            Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(entry[4..]));    // planes
            frames.Add((width, height,
                BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[12..])));
        }
        return frames;
    }

    [Fact]
    public void The_embedded_ico_resource_exists_and_parses()
    {
        var ico = ReadIco();

        var frames = ReadDirectory(ico);
        Assert.NotEmpty(frames);
        foreach (var (_, _, _, size, offset) in frames)
        {
            Assert.True(offset >= 6 + 16 * frames.Count && offset + size <= ico.Length, "A frame lies outside the file.");
            // Every payload is a PNG (the ICO frames are PNG-compressed).
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, ico.AsSpan(offset, 4).ToArray());
        }
    }

    [Fact]
    public void The_ico_lists_the_frames_16_20_24_32_40_48_64_and_256_all_32_bit()
    {
        var frames = ReadDirectory(ReadIco());

        Assert.Equal(ExpectedFrames, frames.Select(f => f.Width).ToArray());
        Assert.All(frames, f =>
        {
            Assert.Equal(f.Width, f.Height);
            Assert.Equal(32, f.Bpp);
        });
    }

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    public void Load_returns_an_icon_of_the_requested_size(int size)
    {
        UiDispatcher.Run(() =>
        {
            using var icon = AppIcon.Load(new System.Drawing.Size(size, size));

            Assert.Equal(new System.Drawing.Size(size, size), icon.Size);
        });
    }

    [Fact]
    public void The_window_icon_is_a_decoded_image_source()
    {
        UiDispatcher.Run(() =>
        {
            ImageSource icon = AppIcon.WindowIcon;

            Assert.NotNull(icon);
            Assert.True(icon.Width > 0 && icon.Height > 0);
            Assert.True(icon.IsFrozen, "The shared window icon must be usable from any window.");
        });
    }
}
