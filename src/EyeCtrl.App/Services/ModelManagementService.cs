namespace EyeCtrl.App.Services;

using System.IO;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using Microsoft.Extensions.Logging;

/// <summary>
/// Orchestrates model download workflows and custom model management.
/// Extracts business logic from the settings UI into a testable service.
/// </summary>
public class ModelManagementService
{
    private readonly ModelRegistryService _registryService;
    private readonly ModelDownloadService _downloadService;
    private readonly EyeCtrlSettings _settings;
    private readonly ILogger<ModelManagementService>? _logger;

    // One download workflow (model + tokenizer) at a time. Held for the whole
    // workflow so IsDownloading can't flicker false between the two transfers and
    // a second workflow can't interleave in that gap.
    private readonly SemaphoreSlim _workflowGate = new(1, 1);

    public event Action<ModelDownloadResult>? DownloadCompleted;

    public ModelManagementService(
        ModelRegistryService registryService,
        ModelDownloadService downloadService,
        EyeCtrlSettings settings,
        ILogger<ModelManagementService>? logger = null)
    {
        _registryService = registryService;
        _downloadService = downloadService;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Whether a download workflow is in flight.
    /// <para>
    /// <c>virtual</c> as a testing seam. <c>ModelsPageSource.DownloadCustomFromClipboard</c> reads
    /// this before showing its confirm modal, so a test that cannot control it also cannot reach
    /// anything past that guard — including the download itself. See
    /// <see cref="DownloadCustomModelAsync"/> for why the seam exists at all.
    /// </para>
    /// </summary>
    public virtual bool IsDownloading => _workflowGate.CurrentCount == 0 || _downloadService.IsDownloading;

    /// <summary>
    /// Downloads a bundled model and its tokenizer atomically.
    /// If the tokenizer download fails, the model file is removed.
    /// </summary>
    public async Task<ModelDownloadResult> DownloadBundledModelAsync(
        string modelId, string url, string sha256,
        string tokenizerUrl, string tokenizerSha256,
        CancellationToken cancellationToken = default)
    {
        if (!await _workflowGate.WaitAsync(0, cancellationToken))
            return ModelDownloadResult.Busy();

        try
        {
            var outputFileName = _registryService.GetModelFileName(modelId);

            var downloaded = await _downloadService.DownloadModelAsync(
                modelId, outputFileName, url, sha256, cancellationToken);

            if (!downloaded)
            {
                var result = ModelDownloadResult.Failed("Download failed. Check the URL and try again.");
                DownloadCompleted?.Invoke(result);
                return result;
            }

            if (!string.IsNullOrEmpty(tokenizerUrl))
            {
                var tokenizerFile = outputFileName.Replace(".onnx", ".tokenizer.json");
                var tokenizerOk = await _downloadService.DownloadTokenizerAsync(
                    modelId, tokenizerFile, tokenizerUrl, tokenizerSha256, cancellationToken);

                if (!tokenizerOk)
                {
                    var modelPath = _registryService.GetModelPath(modelId);
                    if (File.Exists(modelPath)) File.Delete(modelPath);
                    var result = ModelDownloadResult.Failed("Tokenizer download failed. Model removed.");
                    DownloadCompleted?.Invoke(result);
                    return result;
                }
            }

            var success = ModelDownloadResult.Success(modelId);
            DownloadCompleted?.Invoke(success);
            return success;
        }
        finally
        {
            _workflowGate.Release();
        }
    }

    /// <summary>
    /// Downloads a custom model from a URL, registers it, and optionally sets it as active.
    /// <para>
    /// <c>virtual</c> purely as a testing seam, and worth justifying because a production API is
    /// widened for a test. This method converts every failure it <i>expects</i> — bad URL, busy
    /// workflow gate, name derivation — into a <see cref="ModelDownloadResult"/> rather than
    /// throwing. That makes its caller's <c>catch</c> unreachable from outside: there is no input
    /// that provokes a throw, so the one branch that reports an <i>unexpected</i> failure to the
    /// user could never be exercised. Overriding this to throw is the only way to cover it, and
    /// that branch is what stands between a failed download and a user who dwelled a button and is
    /// told nothing.
    /// </para>
    /// </summary>
    public virtual async Task<ModelDownloadResult> DownloadCustomModelAsync(
        string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith("https://"))
            return ModelDownloadResult.Failed("Provide a valid HTTPS URL (e.g., HuggingFace model URL).");

        if (!await _workflowGate.WaitAsync(0, cancellationToken))
            return ModelDownloadResult.Busy();

        try
        {
            string displayName;
            string tokenizerUrl;
            string finalName;

            try
            {
                displayName = DeriveModelName(url);
                tokenizerUrl = ModelDownloadService.GetTokenizerUrl(url);
                var sanitizedName = ModelRegistryService.SanitizeFileName(displayName);
                finalName = _registryService.ResolveUniqueFileName(sanitizedName);
            }
            catch (Exception ex)
            {
                return ModelDownloadResult.Failed($"Invalid URL: {ex.Message}");
            }

            var downloaded = await _downloadService.DownloadModelAsync(
                finalName, $"{finalName}.onnx", url, "", cancellationToken);

            if (!downloaded)
            {
                var result = ModelDownloadResult.Failed("Download failed. Check the URL and try again.");
                DownloadCompleted?.Invoke(result);
                return result;
            }

            var tokenizerOk = await _downloadService.DownloadTokenizerAsync(
                finalName, $"{finalName}.tokenizer.json", tokenizerUrl, "", cancellationToken);

            if (!tokenizerOk)
            {
                // Roll back the orphaned model file so a retry isn't pushed to a "_2" name
                // and the directory doesn't accumulate untracked files. Mirrors the bundled path.
                var modelPath = _registryService.GetModelPath(finalName);
                if (File.Exists(modelPath)) File.Delete(modelPath);
                var result = ModelDownloadResult.Failed("Tokenizer download failed. Model removed.");
                DownloadCompleted?.Invoke(result);
                return result;
            }

            long sizeBytes = 0;
            try { sizeBytes = new FileInfo(_registryService.GetModelPath(finalName)).Length; }
            catch (Exception ex) { _logger?.LogWarning(ex, "Could not read size of downloaded model {Name}", finalName); }

            _registryService.AddCustomModel(finalName, displayName, url, "", sizeBytes,
                tokenizerUrl, "", _settings.Keyboard);

            if (string.IsNullOrEmpty(_registryService.ActiveModelId) || _registryService.ActiveModelId == "Off")
            {
                _settings.Keyboard.MLModelSize = finalName;
                _registryService.ActiveModelId = finalName;
            }

            var success = ModelDownloadResult.Success(finalName);
            DownloadCompleted?.Invoke(success);
            return success;
        }
        finally
        {
            _workflowGate.Release();
        }
    }

    /// <summary>
    /// Deletes a model from the registry and disk.
    /// </summary>
    public void DeleteModel(string modelId)
    {
        var registry = _registryService.Registry;
        if (registry.TryGetValue(modelId, out var entry) && entry.Source == ModelSource.Custom)
            _registryService.RemoveCustomModel(modelId, _settings.Keyboard);
        else
            _registryService.DeleteModelFiles(modelId);
    }

    /// <summary>
    /// Sets the active model.
    /// </summary>
    public void SetActiveModel(string? modelId)
    {
        var selectedId = modelId == "Off" ? null : modelId;
        _settings.Keyboard.MLModelSize = selectedId ?? "Off";
        _registryService.ActiveModelId = selectedId;
    }

    public static string DeriveModelName(string url)
    {
        try
        {
            var uri = new Uri(url);
            var path = uri.AbsolutePath.TrimEnd('/');
            var resolveIdx = path.IndexOf("/resolve/", StringComparison.OrdinalIgnoreCase);
            var effectivePath = resolveIdx >= 0 ? path[..resolveIdx] : path;
            var segments = effectivePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2)
                return segments[^1];
            return segments.Length == 1 ? segments[0] : "CustomModel";
        }
        catch
        {
            return "CustomModel";
        }
    }
}

/// <summary>
/// Result of a model download operation.
/// </summary>
public class ModelDownloadResult
{
    public bool IsSuccess { get; init; }
    public bool IsBusy { get; init; }
    public string? Error { get; init; }
    public string? ModelId { get; init; }

    public static ModelDownloadResult Success(string modelId) => new() { IsSuccess = true, ModelId = modelId };
    public static ModelDownloadResult Failed(string error) => new() { Error = error };
    public static ModelDownloadResult Busy() => new() { IsBusy = true, Error = "Another download is already in progress." };
}
