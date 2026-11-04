namespace EyeCtrl.Setup.Install;

using EyeCtrl.Setup.Abstractions;

/// <summary>
/// The files a published Setup needs beside its exe. Setup is a self-contained single-file WPF
/// app, and self-extraction is forbidden on purpose (plan §4), so WPF's natives stay loose.
/// S6c D1 measured the consequence: a run copy holding only the exe crashes on the first window
/// (0xC000041D, <c>DllNotFoundException</c> in <c>HwndSubclass</c>), leaving the owner with no
/// EyeCtrl at all after a rollback. Every companion is therefore copied and re-verified.
/// </summary>
public static class SetupPayload
{
    /// <summary>The WPF natives a self-contained single-file publish leaves beside the exe.</summary>
    public static readonly IReadOnlyList<string> RequiredCompanions =
    [
        "D3DCompiler_47_cor3.dll",
        "PenImc_cor3.dll",
        "PresentationNative_cor3.dll",
        "vcruntime140_cor3.dll",
        "wpfgfx_cor3.dll",
    ];

    /// <summary>The naming rule the WPF runtime uses for its loose natives.</summary>
    public const string NativeSuffix = "_cor3.dll";

    /// <summary>
    /// Every <c>*_cor3.dll</c> beside <paramref name="sourceSetupExe"/>. A future WPF native that
    /// follows the same rule is carried without another code change; the pinned list above is the
    /// ratchet that makes a rename fail headless instead of on the owner's desktop.
    /// </summary>
    public static IReadOnlyList<string> FindCompanions(
        IFileSystem fileSystem, string sourceSetupExe)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(sourceSetupExe));
        if (string.IsNullOrEmpty(folder))
            return [];

        return fileSystem.EnumerateFiles(folder, recursive: false)
            .Where(path => Path.GetFileName(path).EndsWith(
                NativeSuffix, StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetFileName(path)!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The first pinned companion missing from <paramref name="present"/>, or null.</summary>
    public static string? MissingRequired(IReadOnlyList<string> present) =>
        RequiredCompanions.FirstOrDefault(required =>
            !present.Contains(required, StringComparer.OrdinalIgnoreCase));
}
