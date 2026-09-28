namespace EyeCtrl.IconBuilder;

using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // A1 determinism: rasterize in software so no GPU/driver variance can leak into bytes.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        string? pngDir = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--png-dir" && i + 1 < args.Length)
                pngDir = args[++i];
        }
        if (pngDir is null)
        {
            Console.WriteLine("nothing to do: pass --png-dir <dir> to render the six frames as PNGs");
            return 1;
        }

        var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        Directory.CreateDirectory(pngDir);

        foreach (var plan in FramePlan.All)
        {
            var bgra = FrameRenderer.RenderBgra(plan);
            var png = FrameRenderer.EncodePng(plan.Size, bgra);
            var path = Path.Combine(pngDir, $"{plan.Size}.png");
            File.WriteAllBytes(path, png);
            Console.WriteLine($"wrote {path} ({png.Length} bytes)");
        }
        return 0;
    }

    /// <summary>Walks up from <paramref name="start"/> to the directory holding EyeCtrl.slnx.</summary>
    public static string FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EyeCtrl.slnx")))
            dir = dir.Parent!;
        if (dir is null)
            throw new InvalidOperationException("EyeCtrl.slnx not found above " + start);
        return dir.FullName;
    }
}
