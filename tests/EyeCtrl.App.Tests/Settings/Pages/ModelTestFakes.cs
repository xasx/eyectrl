namespace EyeCtrl.App.Tests.Settings.Pages;

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.LM.Tests;

/// <summary>
/// Builds real <see cref="ModelRegistryService"/>/<see cref="ModelManagementService"/> instances for
/// <see cref="ModelsPageSourceTests"/> against a unique temp dir, and cleans it up. The construction
/// shape mirrors <c>ModelManagementServiceTests.BuildServices</c> (manifest + download service ctor
/// args); the page source reads the public surface of these services, so fakes that reimplement it
/// would only restate the production behaviour and drift. A unique temp dir per scope avoids the
/// machine-global path contention <c>AGENTS.md</c> warns about (parallel test runs, sibling
/// worktrees).
/// </summary>
internal static class ModelTestFakes
{
    /// <summary>Creates a fresh scope owning a unique temp dir. Dispose it to delete the dir.</summary>
    public static ModelTestScope Create() => ModelTestScope.Create();
}

/// <summary>
/// Owns one unique temp directory for a single test and the model services bound to it, so the
/// directory is deletable when the test ends. The previous static helpers created a dir per call
/// and never deleted any of them (a new leak every run), and <c>NoOpManagement()</c> built a second
/// unrelated registry on top of it. One scope = one dir = one cleanup.
/// </summary>
internal sealed class ModelTestScope : IDisposable
{
    private bool _disposed;

    private ModelTestScope(string dir) => Dir = dir;

    /// <summary>A unique temp dir created for this scope; deleted on <see cref="Dispose"/>.</summary>
    public string Dir { get; }

    /// <summary>Creates a scope with a fresh unique temp dir.</summary>
    internal static ModelTestScope Create()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eyectrl_models_test_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return new ModelTestScope(dir);
    }

    /// <summary>
    /// A registry with an empty manifest and no model files on disk: <c>SelectableModels</c>,
    /// <c>DownloadedModels</c> empty; the page renders the "no models" empty state and the
    /// add-custom button only.
    /// </summary>
    public ModelRegistryService EmptyRegistry() =>
        BuildRegistry(new ModelManifest(), new EyeCtrlSettings());

    /// <summary>
    /// A <see cref="ModelManagementService"/> wired to <paramref name="registry"/> (or this scope's
    /// <see cref="EmptyRegistry"/> when omitted) with a no-network <see cref="ModelDownloadService"/>.
    /// Used by tests that exercise <c>Build()</c> only; the download service is never invoked
    /// because the tests never trigger a download.
    /// </summary>
    public ModelManagementService NoOpManagement(ModelRegistryService? registry = null) =>
        new(registry ?? EmptyRegistry(), new ModelDownloadService(Dir), new EyeCtrlSettings());

    /// <summary>
    /// A registry + management pair sharing this scope's dir, with one bundled model marked as
    /// downloaded (both <c>.onnx</c> and <c>.tokenizer.json</c> files dropped onto disk so
    /// <c>IsModelDownloaded</c>/<c>RefreshIsDownloaded</c> see them). Drives the page's
    /// downloaded-state branch: active-model combo, manage button, and the delete page.
    /// </summary>
    public (ModelRegistryService registry, ModelManagementService mgmt) WithDownloadedBundled(
        string id = "135M")
    {
        var manifest = new ModelManifest
        {
            Models =
            {
                [id] = new ModelEntry
                {
                    DisplayName = "Test " + id,
                    Url = "https://example.invalid/model.onnx",
                    Sha256 = "",
                    SizeBytes = 2_048,
                    TokenizerUrl = "https://example.invalid/tokenizer.json",
                    TokenizerSha256 = "",
                },
            },
        };
        var settings = new EyeCtrlSettings();

        // Bundled file names come from ModelConstants.ModelFileNames (e.g. 135M → smollm2-135m.onnx);
        // fall back to {lower(id)}.onnx for any id the constant table does not list.
        var onnx = ModelConstants.ModelFileNames.TryGetValue(id, out var named)
            ? named
            : $"{id.ToLowerInvariant()}.onnx";
        File.WriteAllBytes(Path.Combine(Dir, onnx), new byte[1]);
        File.WriteAllBytes(Path.Combine(Dir, onnx.Replace(".onnx", ".tokenizer.json")), new byte[1]);

        var registry = BuildRegistry(manifest, settings);
        var mgmt = new ModelManagementService(registry, new ModelDownloadService(Dir), settings);
        return (registry, mgmt);
    }

    /// <summary>
    /// One bundled model on disk whose manifest carries REAL SHA-256 hashes of the exact bytes
    /// written — including a REAL, parseable tokenizer (TestTokenizerJson), so the stack's
    /// tokenizer gate passes and only the ONNX bytes are fake. The 1-byte fake model cannot
    /// initialize ONNX, so the predictor still ends up unavailable — the deterministic state
    /// the Failed tests assert.
    /// </summary>
    public (ModelRegistryService registry, ModelManagementService mgmt)
        WithHashedDownloadedBundled(string id = "135M")
    {
        var modelBytes = new byte[] { 0x01 };
        var tokenizerPath = Path.Combine(Dir, "tokenizer-fixture.json");
        TestTokenizerJson.Write(tokenizerPath);
        var tokBytes = File.ReadAllBytes(tokenizerPath);

        var manifest = new ModelManifest
        {
            Models =
            {
                [id] = new ModelEntry
                {
                    DisplayName = "Test " + id,
                    Url = "https://example.invalid/model.onnx",
                    Sha256 = Sha256Hex(modelBytes),
                    SizeBytes = modelBytes.Length,
                    TokenizerUrl = "https://example.invalid/tokenizer.json",
                    TokenizerSha256 = Sha256Hex(tokBytes),
                },
            },
        };
        var settings = new EyeCtrlSettings();
        var onnx = ModelConstants.ModelFileNames.TryGetValue(id, out var named)
            ? named
            : $"{id.ToLowerInvariant()}.onnx";
        File.WriteAllBytes(Path.Combine(Dir, onnx), modelBytes);
        File.WriteAllBytes(Path.Combine(Dir, onnx.Replace(".onnx", ".tokenizer.json")), tokBytes);

        var registry = BuildRegistry(manifest, settings);
        var mgmt = new ModelManagementService(registry, new ModelDownloadService(Dir), settings);
        return (registry, mgmt);
    }

    /// <summary>Lowercase hex SHA-256 of the given bytes — what a manifest entry must carry
    /// for the stack's hash gate to pass those bytes.</summary>
    public static string Sha256Hex(byte[] bytes)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexStringLower(sha.ComputeHash(bytes));
    }

    /// <summary>
    /// Management whose custom download throws, for the one branch that reports an unexpected
    /// failure to the user. <see cref="ModelManagementService.DownloadCustomModelAsync"/> turns
    /// every failure it expects into a <c>Failed</c> result, so no input provokes a throw — this
    /// override is the only way to reach that branch.
    /// </summary>
    public ThrowingManagement ManagementThatThrows(string message = "boom") =>
        new(EmptyRegistry(), new ModelDownloadService(Dir), new EyeCtrlSettings(), message);

    /// <summary>
    /// Management that reports a download already in flight, for the busy guard.
    /// <see cref="ModelManagementService.IsDownloading"/> is otherwise driven by a private
    /// semaphore a test cannot hold.
    /// </summary>
    public BusyManagement ManagementThatIsBusy() =>
        new(EmptyRegistry(), new ModelDownloadService(Dir), new EyeCtrlSettings());

    private ModelRegistryService BuildRegistry(ModelManifest manifest, EyeCtrlSettings settings) =>
        new(Dir, manifest, settings);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Best-effort: a leftover dir is annoying but not fatal, and a failed delete (e.g. a file
        // handle still open) must not fail the test.
        try { Directory.Delete(Dir, recursive: true); }
        catch { /* ignored */ }
    }
}

/// <summary>Throws from <see cref="ModelManagementService.DownloadCustomModelAsync"/>.</summary>
internal sealed class ThrowingManagement(
    ModelRegistryService registry,
    ModelDownloadService download,
    EyeCtrlSettings settings,
    string message)
    : ModelManagementService(registry, download, settings)
{
    /// <summary>Always false, so the caller's busy guard does not short-circuit before the throw.</summary>
    public override bool IsDownloading => false;

    public override Task<ModelDownloadResult> DownloadCustomModelAsync(
        string url, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(message);
}

/// <summary>Reports a download already in flight.</summary>
internal sealed class BusyManagement(
    ModelRegistryService registry,
    ModelDownloadService download,
    EyeCtrlSettings settings)
    : ModelManagementService(registry, download, settings)
{
    public override bool IsDownloading => true;
}
