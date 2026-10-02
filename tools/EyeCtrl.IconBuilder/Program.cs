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

        string? outPath = null;
        string? previewDir = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length)
                outPath = args[++i];
            else if (args[i] == "--preview" && i + 1 < args.Length)
                previewDir = args[++i];
        }

        var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        outPath ??= Path.Combine(repoRoot, "src", "EyeCtrl.App", "Assets", "eyectrl.ico");

        var ico = IcoWriter.Build(FramePlan.All);
        File.WriteAllBytes(outPath, ico);
        Console.WriteLine($"wrote {outPath} ({ico.Length} bytes)");

        if (previewDir is not null)
            PreviewWriter.Write(previewDir);
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
