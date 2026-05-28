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

    public bool IsDownloading => _downloadService.IsDownloading;

    /// <summary>
    /// Downloads a bundled model and its tokenizer atomically.
    /// If the tokenizer download fails, the model file is removed.
    /// </summary>
    public async Task<ModelDownloadResult> DownloadBundledModelAsync(
        string modelId, string url, string sha256,
        string tokenizerUrl, string tokenizerSha256,
        CancellationToken cancellationToken = default)
    {
        if (_downloadService.IsDownloading)
            return ModelDownloadResult.Busy();

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

    /// <summary>
    /// Downloads a custom model from a URL, registers it, and optionally sets it as active.
    /// </summary>
    public async Task<ModelDownloadResult> DownloadCustomModelAsync(
        string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith("https://"))
            return ModelDownloadResult.Failed("Provide a valid HTTPS URL (e.g., HuggingFace model URL).");

        if (_downloadService.IsDownloading)
            return ModelDownloadResult.Busy();

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
            var result = ModelDownloadResult.Failed("Tokenizer download failed. Check the URL and try again.");
            DownloadCompleted?.Invoke(result);
            return result;
        }

        _registryService.AddCustomModel(finalName, displayName, url, "", 0,
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
