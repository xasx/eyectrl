namespace EyeCtrl.Setup.Install;

using EyeCtrl.Setup.Abstractions;

/// <summary>
/// The files a published Setup needs beside its exe. Setup is a self-contained single-file WPF
/// app, and self-extraction is forbidden on purpose (plan §4), so WPF's natives stay loose.
/// S6c D1 measured the consequence: a run copy holding only the exe crashes on the first window
/// (0xC000041D, <c>DllNotFoundException</c> in <c>HwndSubclass</c>), leaving the owner with no
/// EyeCtrl at all after a rollback.
/// <para>
/// The payload is exactly this pinned list, not a wildcard: a glob would also carry an unrelated
/// or future <c>*_cor3.dll</c> that <see cref="RunCopyManager.VerifyRunCopy"/> does not check
/// (review E1). A WPF rename is therefore a deliberate list edit, which the runtime ratchet test
/// in <c>RunCopyManagerTests</c> notices.
/// </para>
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

    /// <summary>The first pinned companion missing from <paramref name="sourceFolder"/>, or null.</summary>
    public static string? FirstMissing(IFileSystem fileSystem, string sourceFolder) =>
        RequiredCompanions.FirstOrDefault(companion =>
            !fileSystem.FileExists(Path.Combine(sourceFolder, companion)));
}
