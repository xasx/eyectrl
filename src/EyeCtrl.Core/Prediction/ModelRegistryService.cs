namespace EyeCtrl.Core.Prediction;

using System.Text.Json;
using Microsoft.Extensions.Logging;
using EyeCtrl.Core.Settings;

public class ModelRegistryService : IDisposable
{
    public const int DebounceDelayMs = 2000;

    private readonly string _modelsDirectory;
    private readonly ModelManifest _bundledManifest;
    private readonly ILogger<ModelRegistryService>? _logger;

    private readonly Dictionary<string, ModelRegistryEntry> _registry = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private string? _activeModelId;
    private bool _disposed;

    public IReadOnlyDictionary<string, ModelRegistryEntry> Registry
    {
        get { lock (_lock) return new Dictionary<string, ModelRegistryEntry>(_registry, StringComparer.OrdinalIgnoreCase); }
    }

    public string? ActiveModelId
    {
        get => _activeModelId;
        set
        {
            lock (_lock)
            {
                foreach (var entry in _registry.Values)
                    entry.IsActive = entry.Id == value;
                _activeModelId = value;
            }
        }
    }

    public IReadOnlyList<ModelRegistryEntry> DownloadedModels
    {
        get
        {
            lock (_lock)
                return _registry.Values.Where(e => e.IsDownloaded && !e.IsActive).ToList();
        }
    }

    public IReadOnlyList<ModelRegistryEntry> SelectableModels
    {
        get
        {
            lock (_lock)
                return _registry.Values.Where(e => e.IsDownloaded).ToList();
        }
    }

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
            var onnxPath = Path.Combine(_modelsDirectory, $"{custom.Id}.onnx");
            var tokenizerPath = Path.Combine(_modelsDirectory, $"{custom.Id}.tokenizer.json");
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
        return modelId switch
        {
            "135M" => "smollm2-135m.onnx",
            "360M" => "smollm2-360m.onnx",
            "0.5B" => "qwen2.5-0.5b.onnx",
            "1.5B" => "qwen2.5-1.5b.onnx",
            "3.8B" => "phi-3.5-mini.onnx",
            _ => $"{modelId.ToLowerInvariant()}.onnx",
        };
    }

    public string GetModelPath(string modelId)
    {
        return Path.Combine(_modelsDirectory, GetModelFileName(modelId));
    }

    public string GetTokenizerPath(string modelId)
    {
        return Path.Combine(_modelsDirectory, GetTokenizerFileName(modelId));
    }

    public bool IsModelDownloaded(string modelId)
    {
        return File.Exists(GetModelPath(modelId)) && File.Exists(GetTokenizerPath(modelId));
    }

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

    public void Dispose()
    {
        _disposed = true;
    }
}
