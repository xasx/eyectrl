namespace EyeCtrl.Core.Prediction;

using Microsoft.Extensions.Logging;
using EyeCtrl.Core.Settings;

/// <summary>
/// Manages the registry of available language models (bundled and custom),
/// tracking download status and the currently active model.
/// </summary>
public class ModelRegistryService
{
    private readonly string _modelsDirectory;
    private readonly ModelManifest _bundledManifest;
    private readonly ILogger<ModelRegistryService>? _logger;

    private readonly Dictionary<string, ModelRegistryEntry> _registry = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private string? _activeModelId;

    /// <summary>Thread-safe snapshot of all registered model entries.</summary>
    public IReadOnlyDictionary<string, ModelRegistryEntry> Registry
    {
        get { lock (_lock) return new Dictionary<string, ModelRegistryEntry>(_registry, StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>Gets or sets the currently active model ID. Setting deactivates all other models.</summary>
    public string? ActiveModelId
    {
        get => _activeModelId;
        set
        {
            lock (_lock)
            {
                foreach (var entry in _registry.Values)
                    entry.IsActive = string.Equals(entry.Id, value, StringComparison.OrdinalIgnoreCase);
                _activeModelId = value;
            }
        }
    }

    /// <summary>All downloaded models except the currently active one.</summary>
    public IReadOnlyList<ModelRegistryEntry> DownloadedModels
    {
        get
        {
            lock (_lock)
            {
                RefreshIsDownloaded(_registry.Values);
                return _registry.Values.Where(e => e.IsDownloaded && !e.IsActive).ToList();
            }
        }
    }

    /// <summary>All downloaded models that can be selected (including the active one).</summary>
    public IReadOnlyList<ModelRegistryEntry> SelectableModels
    {
        get
        {
            lock (_lock)
            {
                RefreshIsDownloaded(_registry.Values);
                return _registry.Values.Where(e => e.IsDownloaded).ToList();
            }
        }
    }

    private void RefreshIsDownloaded(IEnumerable<ModelRegistryEntry> entries)
    {
        foreach (var entry in entries)
        {
            var onnxPath = Path.Combine(_modelsDirectory, GetModelFileNameInternal(entry));
            var tokenizerPath = Path.Combine(_modelsDirectory, GetTokenizerFileNameInternal(entry));
            entry.IsDownloaded = File.Exists(onnxPath) && File.Exists(tokenizerPath);
        }
    }

    private string GetModelFileNameInternal(ModelRegistryEntry entry)
    {
        if (entry.Source == ModelSource.Bundled)
            return GetBundledModelFileName(entry.Id);
        return $"{SanitizeFileName(entry.Id)}.onnx";
    }

    private string GetTokenizerFileNameInternal(ModelRegistryEntry entry)
    {
        if (entry.Source == ModelSource.Bundled)
            return GetBundledModelFileName(entry.Id).Replace(".onnx", ".tokenizer.json");
        return $"{SanitizeFileName(entry.Id)}.tokenizer.json";
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ModelRegistryService"/> class,
    /// scanning the models directory and building the registry from bundled and custom models.
    /// </summary>
    /// <param name="modelsDirectory">Directory containing model files.</param>
    /// <param name="bundledManifest">The built-in model manifest.</param>
    /// <param name="settings">Application settings (keyboard model size config).</param>
    /// <param name="logger">Optional logger.</param>
    public ModelRegistryService(
        string modelsDirectory,
        ModelManifest bundledManifest,
        EyeCtrlSettings settings,
        ILogger<ModelRegistryService>? logger = null)
    {
        _modelsDirectory = modelsDirectory;
        _bundledManifest = bundledManifest;
        _logger = logger;
        Directory.CreateDirectory(_modelsDirectory);

        CleanOrphanedTempFiles();
        BuildRegistry(settings.Keyboard);
        ActiveModelId = settings.Keyboard.MLModelSize;
    }

    private void CleanOrphanedTempFiles()
    {
        try
        {
            foreach (var file in Directory.GetFiles(_modelsDirectory, "*.download"))
            {
                File.Delete(file);
                _logger?.LogInformation("Cleaned orphaned temp file: {File}", file);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to clean orphaned temp files");
        }
    }

    private void BuildRegistry(KeyboardSettings keyboardSettings)
    {
        // 1. Add all bundled models — use GetBundledModelFileName directly
        //    (GetModelFileName requires the registry to already be populated)
        foreach (var (id, entry) in _bundledManifest.Models)
        {
            var modelFile = GetBundledModelFileName(id);
            var tokenizerFile = modelFile.Replace(".onnx", ".tokenizer.json");
            var onnxPath = Path.Combine(_modelsDirectory, modelFile);
            var tokenizerPath = Path.Combine(_modelsDirectory, tokenizerFile);
            var isDownloaded = File.Exists(onnxPath) && File.Exists(tokenizerPath);

            _registry[id] = new ModelRegistryEntry
            {
                Id = id,
                DisplayName = string.IsNullOrEmpty(entry.DisplayName) ? id : entry.DisplayName,
                Url = entry.Url,
                Sha256 = entry.Sha256,
                SizeBytes = entry.SizeBytes,
                TokenizerUrl = entry.TokenizerUrl,
                TokenizerSha256 = entry.TokenizerSha256,
                Source = ModelSource.Bundled,
                IsDownloaded = isDownloaded,
            };
        }

        // 2. Add custom models from keyboard settings
        foreach (var custom in keyboardSettings.CustomModels)
        {
            var sanitizedId = SanitizeFileName(custom.Id);
            var onnxPath = Path.Combine(_modelsDirectory, $"{sanitizedId}.onnx");
            var tokenizerPath = Path.Combine(_modelsDirectory, $"{sanitizedId}.tokenizer.json");
            var isDownloaded = File.Exists(onnxPath) && File.Exists(tokenizerPath);

            _registry[custom.Id] = new ModelRegistryEntry
            {
                Id = custom.Id,
                DisplayName = custom.DisplayName,
                Url = custom.Url,
                Sha256 = custom.Sha256,
                SizeBytes = custom.SizeBytes,
                TokenizerUrl = custom.TokenizerUrl,
                TokenizerSha256 = custom.TokenizerSha256,
                Source = ModelSource.Custom,
                IsDownloaded = isDownloaded,
            };
        }
    }

    /// <summary>Returns the ONNX model file name for a given model ID.</summary>
    /// <param name="modelId">Model identifier.</param>
    /// <returns>The ONNX file name.</returns>
    public string GetModelFileName(string modelId)
    {
        if (_registry.TryGetValue(modelId, out var entry))
        {
            if (entry.Source == ModelSource.Bundled)
                return GetBundledModelFileName(modelId);
            return $"{SanitizeFileName(modelId)}.onnx";
        }
        return $"{SanitizeFileName(modelId)}.onnx";
    }

    /// <summary>Returns the tokenizer file name for a given model ID.</summary>
    /// <param name="modelId">Model identifier.</param>
    /// <returns>The tokenizer file name.</returns>
    public string GetTokenizerFileName(string modelId)
    {
        if (_registry.TryGetValue(modelId, out var entry))
        {
            if (entry.Source == ModelSource.Bundled)
                return GetBundledModelFileName(modelId).Replace(".onnx", ".tokenizer.json");
            return $"{SanitizeFileName(modelId)}.tokenizer.json";
        }
        return $"{SanitizeFileName(modelId)}.tokenizer.json";
    }

    private static string GetBundledModelFileName(string modelId)
    {
        return ModelConstants.ModelFileNames.TryGetValue(modelId, out var fileName)
            ? fileName
            : $"{modelId.ToLowerInvariant()}.onnx";
    }

    /// <summary>Returns the full path to the ONNX model file for a given model ID.</summary>
    /// <param name="modelId">Model identifier.</param>
    public string GetModelPath(string modelId)
    {
        return Path.Combine(_modelsDirectory, GetModelFileName(modelId));
    }

    /// <summary>Returns the full path to the tokenizer file for a given model ID.</summary>
    /// <param name="modelId">Model identifier.</param>
    public string GetTokenizerPath(string modelId)
    {
        return Path.Combine(_modelsDirectory, GetTokenizerFileName(modelId));
    }

    /// <summary>
    /// The registry's expected SHA-256 for the model file, or null when none is recorded (custom
    /// models may ship without one by design). Used by the load path to verify integrity AT LOAD
    /// time — the download path checks the same value, but a file planted directly on disk (AV
    /// quarantine replacement, disk corruption, tampering) bypasses the download check entirely.
    /// </summary>
    public string? GetModelSha256(string modelId) =>
        _registry.TryGetValue(modelId, out var entry) && !string.IsNullOrEmpty(entry.Sha256)
            ? entry.Sha256
            : null;

    /// <summary>As <see cref="GetModelSha256"/>, for the tokenizer file.</summary>
    public string? GetTokenizerSha256(string modelId) =>
        _registry.TryGetValue(modelId, out var entry) && !string.IsNullOrEmpty(entry.TokenizerSha256)
            ? entry.TokenizerSha256
            : null;

    /// <summary>Checks whether both model and tokenizer files exist on disk for a model ID.</summary>
    /// <param name="modelId">Model identifier.</param>
    /// <returns>True if both files exist.</returns>
    public bool IsModelDownloaded(string modelId)
    {
        return File.Exists(GetModelPath(modelId)) && File.Exists(GetTokenizerPath(modelId));
    }

    /// <summary>Adds a custom model to the registry and persists it in keyboard settings.</summary>
    /// <param name="id">Model identifier.</param>
    /// <param name="displayName">Human-readable name.</param>
    /// <param name="url">Download URL for the ONNX file.</param>
    /// <param name="sha256">SHA-256 hash for integrity.</param>
    /// <param name="sizeBytes">File size in bytes.</param>
    /// <param name="tokenizerUrl">Download URL for the tokenizer.</param>
    /// <param name="tokenizerSha256">SHA-256 hash for the tokenizer.</param>
    /// <param name="keyboardSettings">Keyboard settings to update with the new model entry.</param>
    public void AddCustomModel(string id, string displayName, string url, string sha256,
        long sizeBytes, string tokenizerUrl, string tokenizerSha256, KeyboardSettings keyboardSettings)
    {
        lock (_lock)
        {
            _registry[id] = new ModelRegistryEntry
            {
                Id = id,
                DisplayName = displayName,
                Url = url,
                Sha256 = sha256,
                SizeBytes = sizeBytes,
                TokenizerUrl = tokenizerUrl,
                TokenizerSha256 = tokenizerSha256,
                Source = ModelSource.Custom,
                IsDownloaded = true,
            };

            keyboardSettings.CustomModels.Add(new CustomModelEntry
            {
                Id = id,
                DisplayName = displayName,
                Url = url,
                Sha256 = sha256,
                SizeBytes = sizeBytes,
                TokenizerUrl = tokenizerUrl,
                TokenizerSha256 = tokenizerSha256,
            });
        }
    }

    /// <summary>Removes a custom model from the registry, keyboard settings, and deletes its files.</summary>
    /// <param name="modelId">Model identifier.</param>
    /// <param name="keyboardSettings">Keyboard settings to update.</param>
    public void RemoveCustomModel(string modelId, KeyboardSettings keyboardSettings)
    {
        lock (_lock)
        {
            if (_registry.TryGetValue(modelId, out var entry) && entry.Source == ModelSource.Custom)
            {
                _registry.Remove(modelId);
                var custom = keyboardSettings.CustomModels.FirstOrDefault(c => c.Id == modelId);
                if (custom != null)
                    keyboardSettings.CustomModels.Remove(custom);

                var onnx = GetModelPath(modelId);
                var tokenizer = GetTokenizerPath(modelId);
                if (File.Exists(onnx)) File.Delete(onnx);
                if (File.Exists(tokenizer)) File.Delete(tokenizer);
            }
        }
    }

    /// <summary>Deletes the model and tokenizer files for a model ID without removing it from the registry.</summary>
    /// <param name="modelId">Model identifier.</param>
    public void DeleteModelFiles(string modelId)
    {
        lock (_lock)
        {
            var onnx = GetModelPath(modelId);
            var tokenizer = GetTokenizerPath(modelId);
            if (File.Exists(onnx)) File.Delete(onnx);
            if (File.Exists(tokenizer)) File.Delete(tokenizer);

            if (_registry.TryGetValue(modelId, out var entry))
                entry.IsDownloaded = false;
        }
    }

    /// <summary>Sanitizes a string for use as a file name (alphanumeric, dots, dashes, underscores only).</summary>
    /// <param name="name">Raw name to sanitize.</param>
    /// <returns>A safe file name string.</returns>
    public static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "custom_model";

        var sanitized = new System.Text.StringBuilder();
        char? lastChar = null;
        for (int i = 0; i < Math.Min(name.Length, 128); i++)
        {
            var c = name[i];
            if (char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-')
            {
                sanitized.Append(c);
                lastChar = c;
            }
            else
            {
                if (lastChar != '_')
                {
                    sanitized.Append('_');
                    lastChar = '_';
                }
            }
        }

        var result = sanitized.ToString().Trim('_', '.').Trim();
        if (result.Length > 64)
            result = result[..64].TrimEnd('.', '_');

        while (result.Contains("__"))
            result = result.Replace("__", "_");

        if (string.IsNullOrEmpty(result) || result.All(c => c == '.'))
            return "custom_model";

        return result;
    }

    /// <summary>Resolves a unique file name within the models directory, appending a suffix if the name already exists.</summary>
    /// <param name="sanitizedName">Pre-sanitized base name.</param>
    /// <returns>A unique file name without extension.</returns>
    public string ResolveUniqueFileName(string sanitizedName)
    {
        var baseName = sanitizedName;
        var candidatePath = Path.Combine(_modelsDirectory, $"{baseName}.onnx");
        if (!File.Exists(candidatePath))
            return baseName;

        for (int i = 2; i < 1000; i++)
        {
            var name = $"{baseName}_{i}";
            var path = Path.Combine(_modelsDirectory, $"{name}.onnx");
            if (!File.Exists(path))
                return name;
        }

        throw new InvalidOperationException($"Could not find unique filename for {sanitizedName} after 1000 attempts");
    }

}
