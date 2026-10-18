using System.IO;

namespace IconGen;

internal static class Program
{
    /// <summary>The frames of the .ico, smallest first.</summary>
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 256];

    /// <summary>Usage: IconGen [output directory]. Defaults to src/RhythmSync.App/Assets in the repository.</summary>
    [STAThread]
    private static int Main(string[] args)
    {
        string outputDirectory = args.Length > 0 ? Path.GetFullPath(args[0]) : DefaultOutputDirectory();
        Directory.CreateDirectory(outputDirectory);

        var frames = new List<(int Size, byte[] Png)>();
        foreach (int size in Sizes)
            frames.Add((size, LogoRenderer.RenderPng(size)));

        string icoPath = Path.Combine(outputDirectory, "RhythmSync.ico");
        File.WriteAllBytes(icoPath, IcoWriter.Write(frames));

        string pngPath = Path.Combine(outputDirectory, "RhythmSync-256.png");
        File.WriteAllBytes(pngPath, frames[^1].Png);

        Console.WriteLine($"Wrote {icoPath} ({new FileInfo(icoPath).Length} bytes), frames: {string.Join(", ", Sizes)}");
        Console.WriteLine($"Wrote {pngPath}");
        return 0;
    }

    private static string DefaultOutputDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "RhythmSync.sln")))
                return Path.Combine(dir.FullName, "src", "RhythmSync.App", "Assets");

        throw new InvalidOperationException("RhythmSync.sln was not found above the tool; pass the output directory as an argument.");
    }
}
