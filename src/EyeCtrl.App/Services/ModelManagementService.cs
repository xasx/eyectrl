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

    /// <summary>
    /// Raised (throttled to ~2x/second, plus once per terminal state) while a download workflow
    /// is in flight, so a UI displaying <see cref="CurrentDownloadId"/> can refresh without
    /// being driven at network-read rate. Raised on the download's worker thread — marshal.
    /// </summary>
    public event Action? DownloadProgressChanged;

    /// <summary>
    /// Which model a download is currently transferring, or null when idle. Per-file: the model
    /// transfer completes before the tokenizer one starts, and the id stays set for both.
    /// internal set: headless page tests inject a mid-download state directly.
    /// </summary>
    public string? CurrentDownloadId { get; internal set; }

    /// <summary>Progress of the current transfer as a 0..1 fraction.</summary>
    public double CurrentDownloadProgress { get; internal set; }

    private DateTime _lastProgressRaiseUtc = DateTime.MinValue;

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
        _downloadService.ProgressChanged += OnDownloadProgress;
    }

    // A multi-hundred-MB download with no visible state reads as "the button did nothing" —
    // the 2026-08-17 round-3 finding ("models do not download… there is no progress"). The
    // per-file IsComplete deliberately does NOT clear CurrentDownloadId: the tokenizer phase
    // follows immediately, and the workflow is only over when Complete() runs.
    private void OnDownloadProgress(ModelDownloadService.ModelDownloadProgress p)
    {
        CurrentDownloadId = p.ModelId;
        CurrentDownloadProgress = p.Progress;

        var terminal = p.IsComplete || p.IsCancelled || p.Error is not null;
        if (!terminal && DateTime.UtcNow - _lastProgressRaiseUtc < TimeSpan.FromMilliseconds(500))
            return;
        _lastProgressRaiseUtc = DateTime.UtcNow;
        DownloadProgressChanged?.Invoke();
    }

    /// <summary>Every terminal path funnels through here: announce, then clear the in-flight state.</summary>
    private void Complete(ModelDownloadResult result)
    {
        DownloadCompleted?.Invoke(result);
        CurrentDownloadId = null;
        CurrentDownloadProgress = 0;
        _lastProgressRaiseUtc = DateTime.MinValue;
        DownloadProgressChanged?.Invoke();
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
                Complete(result);
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
                    Complete(result);
                    return result;
                }
            }

            ActivateDownloadedModelIfNothingRealIsActive(modelId);

            var success = ModelDownloadResult.Success(modelId);
            Complete(success);
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
                Complete(result);
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
                Complete(result);
                return result;
            }

            long sizeBytes = 0;
            try { sizeBytes = new FileInfo(_registryService.GetModelPath(finalName)).Length; }
            catch (Exception ex) { _logger?.LogWarning(ex, "Could not read size of downloaded model {Name}", finalName); }

            _registryService.AddCustomModel(finalName, displayName, url, "", sizeBytes,
                tokenizerUrl, "", _settings.Keyboard);

            ActivateDownloadedModelIfNothingRealIsActive(finalName);

            var success = ModelDownloadResult.Success(finalName);
            Complete(success);
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
    /// Makes a just-downloaded model active unless a real, downloaded model already is.
    /// <para>
    /// A completed download is a deliberate act aimed at <paramref name="modelId"/>, so that is
    /// the model to activate when nothing usable is active. "Nothing usable" includes the case
    /// this defect is made of: <see cref="ModelRegistryService"/> seeds <c>ActiveModelId</c> from
    /// <c>Keyboard.MLModelSize</c>, so the stored default <c>"360M"</c> — with no 360M file on
    /// disk — is neither empty nor <c>"Off"</c>, and the old inline equivalent of this check
    /// (<c>IsNullOrEmpty || == "Off"</c>) therefore never fired. The predicate is the FILE, never
    /// the id's spelling. Both spellings of "no active model" are checked because the seed stores
    /// <c>"Off"</c> verbatim while <see cref="SetActiveModel"/> normalizes it to null — do not
    /// "simplify" either away.
    /// </para>
    /// <para>
    /// Deliberately per-download, never at startup. A startup repair would turn this dormant
    /// defect live on the first launch of an affected install — a 1.67 GB ONNX model loading and
    /// the prediction bar changing on the user's only text-input surface, with the loading
    /// indicator and GPU preference both defaulting on. That is the ticket-7 hazard (a fix that
    /// silently changes a daily surface), so it is a decision, not an omission. No migration is
    /// needed for the same reason: nothing else reads whether an id is downloaded, so this does
    /// not change what any stored value means — a download is simply the first moment the repair
    /// is earned, and until then a "360M" install behaves exactly as it does today.
    /// </para>
    /// </summary>
    private void ActivateDownloadedModelIfNothingRealIsActive(string modelId)
    {
        var active = _registryService.ActiveModelId;
        var activeIsOnDisk = !string.IsNullOrEmpty(active)
            && !string.Equals(active, "Off", StringComparison.OrdinalIgnoreCase)
            && _registryService.IsModelDownloaded(active);
        if (!activeIsOnDisk)
            SetActiveModel(modelId);
    }

    /// <summary>
    /// Sets the active model. The single writer of <c>Keyboard.MLModelSize</c> — the UI, both
    /// download paths and startup reconciliation all route through here so the setting and the
    /// registry's <see cref="ModelRegistryService.ActiveModelId"/> cannot disagree.
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
