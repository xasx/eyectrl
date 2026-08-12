namespace EyeCtrl.App.Tests.Settings.Pages;

using System.IO;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;

/// <summary>
/// Builds real <see cref="ModelRegistryService"/>/<see cref="ModelManagementService"/> instances for
/// <see cref="ModelsPageSourceTests"/> against a unique temp dir per call. The construction shape
/// mirrors <c>ModelManagementServiceTests.BuildServices</c> (manifest + download service ctor args);
/// the page source reads the public surface of these services, so fakes that reimplement it would
/// only restate the production behaviour and drift. A unique temp dir per call avoids the
/// machine-global path contention <c>AGENTS.md</c> warns about (parallel test runs, sibling worktrees).
/// </summary>
internal static class ModelTestFakes
{
    /// <summary>
    /// A registry with an empty manifest and no model files on disk: <c>SelectableModels</c>,
    /// <c>DownloadedModels</c> empty; the page renders the "no models" empty state and the
    /// add-custom button only.
    /// </summary>
    public static ModelRegistryService EmptyRegistry() =>
        BuildRegistry(NewTempDir(), new ModelManifest(), new EyeCtrlSettings());

    /// <summary>
    /// A <see cref="ModelManagementService"/> wired to <paramref name="registry"/> (or a fresh
    /// <see cref="EmptyRegistry"/> when omitted) with a no-network <see cref="ModelDownloadService"/>.
    /// Used by tests that exercise <c>Build()</c> only; the download service is never invoked
    /// because the tests never trigger a download.
    /// </summary>
    public static ModelManagementService NoOpManagement(ModelRegistryService? registry = null)
    {
        registry ??= EmptyRegistry();
        var dir = Path.GetDirectoryName(registry.GetModelPath("probe"))!;
        return new ModelManagementService(registry, new ModelDownloadService(dir), new EyeCtrlSettings());
    }

    /// <summary>
    /// A registry + management pair sharing one temp dir, with one bundled model marked as
    /// downloaded (both <c>.onnx</c> and <c>.tokenizer.json</c> files dropped onto disk so
    /// <c>IsModelDownloaded</c>/<c>RefreshIsDownloaded</c> see them). Drives the page's
    /// downloaded-state branch: active-model combo, manage button, and the delete page.
    /// </summary>
    public static (ModelRegistryService registry, ModelManagementService mgmt) WithDownloadedBundled(
        string id = "135M")
    {
        var dir = NewTempDir();
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
        File.WriteAllBytes(Path.Combine(dir, onnx), new byte[1]);
        File.WriteAllBytes(Path.Combine(dir, onnx.Replace(".onnx", ".tokenizer.json")), new byte[1]);

        var registry = BuildRegistry(dir, manifest, settings);
        var mgmt = new ModelManagementService(registry, new ModelDownloadService(dir), settings);
        return (registry, mgmt);
    }

    private static ModelRegistryService BuildRegistry(string dir, ModelManifest manifest, EyeCtrlSettings settings) =>
        new(dir, manifest, settings);

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eyectrl_models_test_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }
}
