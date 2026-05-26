# Model Download & Management UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a "Models" settings tab for browsing, downloading, and managing ONNX language models from a curated list, plus adding custom models from HuggingFace.

**Architecture:** `ModelRegistryService` merges a bundled `models.json` manifest with user-added custom models stored in `KeyboardSettings.CustomModels`. The `ModelDownloadService` gains tokenizer URL derivation and a filename-flexible download overload. `SettingsWindow` gains a Models tab with pages for browsing/downloading and managing models. `EventCoordinator` receives model services via a setter and uses them for model path resolution.

**Tech Stack:** Existing stack — `Microsoft.ML.OnnxRuntime.DirectML`, `System.Text.Json`, `CommunityToolkit.Mvvm`, `HttpClient`. No new NuGet packages.

---

### Task 1: ModelEntry Extensions, ModelRegistry, and KeyboardSettings CustomModels

**Files:**
- Modify: `src/EyeCtrl.Core/Prediction/ModelManifest.cs`
- Create: `src/EyeCtrl.Core/Prediction/ModelRegistry.cs`
- Modify: `src/EyeCtrl.Core/Settings/KeyboardSettings.cs`

- [ ] **Step 1: Add displayName, tokenizerUrl, tokenizerSha256 to ModelEntry**

```csharp
// src/EyeCtrl.Core/Prediction/ModelManifest.cs — replace entire file

namespace EyeCtrl.Core.Prediction;

using System.Text.Json.Serialization;

public class ModelManifest
{
    [JsonPropertyName("models")]
    public Dictionary<string, ModelEntry> Models { get; set; } = [];
}

public class ModelEntry
{
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("tokenizerUrl")]
    public string TokenizerUrl { get; set; } = "";

    [JsonPropertyName("tokenizerSha256")]
    public string TokenizerSha256 { get; set; } = "";
}
```

- [ ] **Step 2: Run tests to verify existing tests still pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~ModelManifestTests" -v`
Expected: PASS — the existing test's JSON uses the 4 old fields; `displayName` and `tokenizerUrl` being missing from the test JSON will just deserialize as `""` (default), which is fine.

- [ ] **Step 3: Create ModelRegistryEntry data model (used at runtime, not serialized)**

```csharp
// src/EyeCtrl.Core/Prediction/ModelRegistry.cs

namespace EyeCtrl.Core.Prediction;

public enum ModelSource
{
    Bundled,
    Custom
}

// Runtime-only entry — built by ModelRegistryService. Not persisted as JSON.
public class ModelRegistryEntry
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    public string TokenizerUrl { get; set; } = "";
    public string TokenizerSha256 { get; set; } = "";
    public ModelSource Source { get; set; }

    public bool IsDownloaded { get; set; }
    public bool IsActive { get; set; }
}
```

- [ ] **Step 4: Add CustomModels list to KeyboardSettings**

```csharp
// src/EyeCtrl.Core/Settings/KeyboardSettings.cs — add after the PreferGPU property (line 30)

[ObservableProperty]
private List<CustomModelEntry> _customModels = [];

// Add this POCO at the bottom of the file, inside the namespace:
public class CustomModelEntry
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    public string TokenizerUrl { get; set; } = "";
    public string TokenizerSha256 { get; set; } = "";
}
```

Also remove the static `ValidMLModelSizes` array from KeyboardSettings (it will be replaced by runtime validation via ModelRegistryService). Remove line 33:

```csharp
// Delete this line:
private static readonly string[] ValidMLModelSizes = { "135M", "360M", "1.5B", "Off" };
```

And update the `Validate()` method to remove the MLModelSize validation (remove line 42):

```csharp
// In Validate(), remove this line:
ValidationHelper.ValidateStringEnum(MLModelSize, ValidMLModelSizes, nameof(MLModelSize)),
```

The updated `Validate()` should be:

```csharp
public IReadOnlyList<string> Validate()
{
    return ValidationHelper.CollectErrors(
        ValidationHelper.ValidateStringEnum(ActiveLayout, ValidLayouts, nameof(ActiveLayout)),
        ValidationHelper.ValidateRange(KeySizeMultiplier, 0.5, 2.0, nameof(KeySizeMultiplier)),
        ValidationHelper.ValidateStringEnum(ScreenEdge, ValidScreenEdges, nameof(ScreenEdge)),
        ValidationHelper.ValidateRange(DecayHalfLifeDays, 1, 365, nameof(DecayHalfLifeDays))
    );
}
```

- [ ] **Step 5: Verify build compiles**

Run: `dotnet build src/EyeCtrl.Core`
Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Core/Prediction/ModelManifest.cs src/EyeCtrl.Core/Prediction/ModelRegistry.cs src/EyeCtrl.Core/Settings/KeyboardSettings.cs
git commit -m "feat: add ModelEntry displayName/tokenizerUrl, ModelRegistryEntry, and KeyboardSettings CustomModels"
```

---

### Task 2: ModelRegistryService

**Files:**
- Create: `src/EyeCtrl.Core/Prediction/ModelRegistryService.cs`

- [ ] **Step 1: Create ModelRegistryService with manifest merging, CRUD, debounce, and sanitization**

```csharp
// src/EyeCtrl.Core/Prediction/ModelRegistryService.cs

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

        // Clean up orphaned temp files from crashes
        CleanOrphanedTempFiles();

        // Build merged registry
        BuildRegistry(settings.Keyboard);

        // Set active model from settings
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
        //    (GetModelFileName requires the registry to already be populated, which it isn't yet)
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
        // Fallback: treat as custom model ID
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

                // Delete files
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

    // Filename sanitization per spec rules
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

        // Collapse multiple consecutive underscores
        while (result.Contains("__"))
            result = result.Replace("__", "_");

        if (string.IsNullOrEmpty(result) || result.All(c => c == '.'))
            return "custom_model";

        return result;
    }

    // Collision check for custom model filenames.
    // NOTE: Spec also requires same-URL reuse (if existing file has the same URL,
    // overwrite it instead of creating _2). Deferred — current impl just checks
    // filename existence, not URL matches.
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
```

- [ ] **Step 2: Verify build compiles**

Run: `dotnet build src/EyeCtrl.Core`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.Core/Prediction/ModelRegistryService.cs
git commit -m "feat: add ModelRegistryService with manifest merging, CRUD, debounce, and sanitization"
```

---

### Task 3: ModelDownloadService Enhancements and ModelConstants Removal

**Files:**
- Modify: `src/EyeCtrl.Core/Prediction/ModelDownloadService.cs`
- Modify: `src/EyeCtrl.Core/Prediction/ModelConstants.cs`

- [ ] **Step 1: Add GetTokenizerUrl to ModelDownloadService**

In `ModelDownloadService.cs`, add this method after `GetTokenizerPath`:

```csharp
// Add after GetTokenizerPath method (line 56)

public static string GetTokenizerUrl(string modelUrl)
{
    if (string.IsNullOrWhiteSpace(modelUrl))
        throw new ArgumentException("Model URL cannot be empty", nameof(modelUrl));

    // Strip query parameters
    var uri = new Uri(modelUrl);
    var path = uri.AbsolutePath.TrimEnd('/');
    var lastSlash = path.LastIndexOf('/');
    if (lastSlash < 0)
        throw new ArgumentException($"Cannot derive tokenizer URL from: {modelUrl}");

    var dir = path[..lastSlash];
    var tokenizerUrl = $"{uri.GetLeftPart(UriPartial.Authority)}{dir}/tokenizer.json";

    // Preserve query if it existed (unlikely for static files)
    if (!string.IsNullOrEmpty(uri.Query))
        tokenizerUrl += uri.Query;

    return tokenizerUrl;
}
```

- [ ] **Step 2: Add download progress state to ModelDownloadService**

Add these fields after `_logger` declaration (line 12):

```csharp
private volatile bool _isDownloading;
private CancellationTokenSource? _downloadCts;

public bool IsDownloading => _isDownloading;

public event Action<ModelDownloadProgress>? ProgressChanged;

public class ModelDownloadProgress
{
    public string ModelId { get; init; } = "";
    public double Progress { get; init; }
    public bool IsComplete { get; init; }
    public bool IsCancelled { get; init; }
    public string? Error { get; init; }
}
```

- [ ] **Step 3: Update DownloadModelAsync to populate progress state**

Replace the existing `DownloadModelAsync` signature to add `modelId` and wire progress events, and add `CancelDownload`:

Add these methods to `ModelDownloadService`:

```csharp
public void CancelDownload()
{
    _downloadCts?.Cancel();
}

public async Task<bool> DownloadModelAsync(
    string modelId,
    string outputFileName,
    string url,
    string expectedSha256,
    CancellationToken cancellationToken = default)
{
    var outputPath = Path.Combine(_modelsDirectory, outputFileName);
    var tempPath = outputPath + ".download";

    _isDownloading = true;
    _downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

    try
    {
        var progress = ProgressChanged;
        progress?.Invoke(new ModelDownloadProgress { ModelId = modelId, Progress = 0 });

        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, _downloadCts.Token);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        using var contentStream = await response.Content.ReadAsStreamAsync(_downloadCts.Token);
        using var fileStream = File.Create(tempPath);

        var buffer = new byte[8192];
        long totalRead = 0;
        int bytesRead;

        using var sha256 = System.Security.Cryptography.SHA256.Create();
        using (var cs = new CryptoStream(contentStream, sha256, CryptoStreamMode.Read, leaveOpen: true))
        {
            while ((bytesRead = await cs.ReadAsync(buffer, _downloadCts.Token)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), _downloadCts.Token);
                totalRead += bytesRead;
                if (totalBytes > 0)
                    progress?.Invoke(new ModelDownloadProgress { ModelId = modelId, Progress = (double)totalRead / totalBytes });
            }
        }
        var computedHash = Convert.ToHexStringLower(sha256.Hash!);

        if (!string.Equals(computedHash, expectedSha256, System.StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogError("Hash mismatch for model {Id}: expected {Expected}, got {Actual}", modelId, expectedSha256, computedHash);
            File.Delete(tempPath);
            progress?.Invoke(new ModelDownloadProgress { ModelId = modelId, IsComplete = true, Error = "Hash mismatch" });
            return false;
        }

        File.Move(tempPath, outputPath, overwrite: true);
        _logger?.LogInformation("Model {Id} downloaded successfully ({Bytes} bytes)", modelId, totalRead);
        progress?.Invoke(new ModelDownloadProgress { ModelId = modelId, Progress = 1.0, IsComplete = true });
        return true;
    }
    catch (OperationCanceledException)
    {
        _logger?.LogInformation("Model download cancelled: {Id}", modelId);
        if (File.Exists(tempPath)) File.Delete(tempPath);
        ProgressChanged?.Invoke(new ModelDownloadProgress { ModelId = modelId, IsCancelled = true });
        throw;
    }
    catch (Exception ex)
    {
        _logger?.LogError(ex, "Failed to download model {Id}", modelId);
        if (File.Exists(tempPath)) File.Delete(tempPath);
        ProgressChanged?.Invoke(new ModelDownloadProgress { ModelId = modelId, Error = ex.Message });
        return false;
    }
    finally
    {
        _isDownloading = false;
        _downloadCts?.Dispose();
        _downloadCts = null;
    }
}

// NEW: flexible overload that accepts an explicit output filename (for custom models).
// The existing DownloadTokenizerAsync(string modelSize, string url, string sha256, ...)
// is kept for backward compat with bundled models.
public async Task<bool> DownloadTokenizerAsync(
    string modelId,
    string outputFileName,
    string url,
    string expectedSha256,
    CancellationToken cancellationToken = default)
{
    var outputPath = Path.Combine(_modelsDirectory, outputFileName);
    var tempPath = outputPath + ".download";

    _logger?.LogInformation("Downloading tokenizer {Id} from {Url} to {Path}", modelId, url, outputPath);

    try
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var fileStream = File.Create(tempPath);

        var buffer = new byte[8192];
        int bytesRead;

        using var sha256 = System.Security.Cryptography.SHA256.Create();
        using (var cs = new CryptoStream(contentStream, sha256, CryptoStreamMode.Read, leaveOpen: true))
        {
            while ((bytesRead = await cs.ReadAsync(buffer, cancellationToken)) > 0)
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
        }
        var computedHash = Convert.ToHexStringLower(sha256.Hash!);

        if (!string.IsNullOrEmpty(expectedSha256) &&
            !string.Equals(computedHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogError("Hash mismatch for tokenizer {Id}", modelId);
            File.Delete(tempPath);
            return false;
        }

        File.Move(tempPath, outputPath, overwrite: true);
        _logger?.LogInformation("Tokenizer {Id} downloaded successfully", modelId);
        return true;
    }
    catch (OperationCanceledException)
    {
        if (File.Exists(tempPath)) File.Delete(tempPath);
        throw;
    }
    catch (Exception ex)
    {
        _logger?.LogError(ex, "Failed to download tokenizer {Id}", modelId);
        if (File.Exists(tempPath)) File.Delete(tempPath);
        return false;
    }
}
```

- [ ] **Step 4: Replace ModelConstants with registry-backed lookup**

Replace the entire `ModelConstants.cs`:

```csharp
// src/EyeCtrl.Core/Prediction/ModelConstants.cs

namespace EyeCtrl.Core.Prediction;

public static class ModelConstants
{
    // This dictionary is now a fallback for situations where ModelRegistryService
    // is not available. The primary source of truth is ModelRegistryService.GetModelFileName().
    // Kept for backward compat with SmallLMPredictor and ModelDownloadService legacy paths.
    public static IReadOnlyDictionary<string, string> ModelFileNames
    {
        get
        {
            return new Dictionary<string, string>
            {
                ["135M"] = "smollm2-135m.onnx",
                ["360M"] = "smollm2-360m.onnx",
                ["0.5B"] = "qwen2.5-0.5b.onnx",
                ["1.5B"] = "qwen2.5-1.5b.onnx",
                ["3.8B"] = "phi-3.5-mini.onnx",
            };
        }
    }
}
```

- [ ] **Step 5: Verify build compiles**

Run: `dotnet build src/EyeCtrl.Core`
Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Core/Prediction/ModelDownloadService.cs src/EyeCtrl.Core/Prediction/ModelConstants.cs
git commit -m "feat: add GetTokenizerUrl, download progress, and extend ModelConstants for new models"
```

---

### Task 4: ShowKeyboardRequestMessage and HideKeyboardRequestMessage

**Files:**
- Create: `src/EyeCtrl.ViewModels/Messages/KeyboardRequestMessages.cs`

- [ ] **Step 1: Create new message types**

```csharp
// src/EyeCtrl.ViewModels/Messages/KeyboardRequestMessages.cs

namespace EyeCtrl.ViewModels.Messages;

/// <summary>
/// Sent from settings window when a text field is activated (user dwells on it).
/// Carries the recipient window handle so EventCoordinator can route key events.
/// </summary>
public record ShowKeyboardRequestMessage
{
    public IntPtr TargetHandle { get; init; }
}

/// <summary>
/// Sent from settings window when the modal closes or text field loses focus.
/// </summary>
public record HideKeyboardRequestMessage;
```

- [ ] **Step 2: Verify build compiles**

Run: `dotnet build src/EyeCtrl.ViewModels`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.ViewModels/Messages/KeyboardRequestMessages.cs
git commit -m "feat: add ShowKeyboardRequestMessage and HideKeyboardRequestMessage"
```

---

### Task 5: models.json Manifest + AppBootstrapper DI

**Files:**
- Create: `src/EyeCtrl.App/Resources/models.json`
- Modify: `src/EyeCtrl.App/EyeCtrl.App.csproj`
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs`

- [ ] **Step 1: Create bundled models.json manifest**

Note: SHA256 hashes are empty placeholders. The `DownloadTokenizerAsync` overload skips hash validation when the expected hash is empty. Actual hashes should be filled in once model files are available.

```json
{
    "models": {
        "135M": {
            "displayName": "SmolLM2 135M",
            "url": "https://huggingface.co/HuggingFaceTB/SmolLM2-135M-Instruct/resolve/main/onnx/model.onnx",
            "sha256": "",
            "sizeBytes": 73400320,
            "tokenizerUrl": "https://huggingface.co/HuggingFaceTB/SmolLM2-135M-Instruct/resolve/main/onnx/tokenizer.json",
            "tokenizerSha256": ""
        },
        "360M": {
            "displayName": "SmolLM2 360M",
            "url": "https://huggingface.co/HuggingFaceTB/SmolLM2-360M-Instruct/resolve/main/onnx/model.onnx",
            "sha256": "",
            "sizeBytes": 188743680,
            "tokenizerUrl": "https://huggingface.co/HuggingFaceTB/SmolLM2-360M-Instruct/resolve/main/onnx/tokenizer.json",
            "tokenizerSha256": ""
        },
        "0.5B": {
            "displayName": "Qwen2.5 0.5B",
            "url": "https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct/resolve/main/onnx/model.onnx",
            "sha256": "",
            "sizeBytes": 262144000,
            "tokenizerUrl": "https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct/resolve/main/onnx/tokenizer.json",
            "tokenizerSha256": ""
        },
        "1.5B": {
            "displayName": "Qwen2.5 1.5B",
            "url": "https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct/resolve/main/onnx/model.onnx",
            "sha256": "",
            "sizeBytes": 838860800,
            "tokenizerUrl": "https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct/resolve/main/onnx/tokenizer.json",
            "tokenizerSha256": ""
        },
        "3.8B": {
            "displayName": "Phi-3.5-mini",
            "url": "https://huggingface.co/microsoft/Phi-3.5-mini-instruct-onnx/resolve/main/cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4/model.onnx",
            "sha256": "",
            "sizeBytes": 2147483648,
            "tokenizerUrl": "https://huggingface.co/microsoft/Phi-3.5-mini-instruct-onnx/resolve/main/cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4/tokenizer.json",
            "tokenizerSha256": ""
        }
    }
}
```

- [ ] **Step 2: Add models.json to csproj as content**

Add this `ItemGroup` to `src/EyeCtrl.App/EyeCtrl.App.csproj` (after the existing emoji content group):

```xml
<Content Include="Resources\models.json">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  <CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>
  <ExcludeFromSingleFile>true</ExcludeFromSingleFile>
</Content>
```

- [ ] **Step 3: Register ModelRegistryService and HttpClient in AppBootstrapper**

In `AppBootstrapper.cs`, add these registrations before `// Core engine` (line 68):

```csharp
// Model download services
services.AddSingleton<HttpClient>(_ => new HttpClient { Timeout = TimeSpan.FromMinutes(30) });
services.AddSingleton(sp =>
{
    var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    var modelsDir = Path.Combine(appData, "EyeCtrl", "models");
    var settings = sp.GetRequiredService<EyeCtrlSettings>();

    // Load bundled manifest
    var manifestPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "models.json");
    ModelManifest manifest;
    if (File.Exists(manifestPath))
    {
        var json = File.ReadAllText(manifestPath);
        manifest = JsonSerializer.Deserialize<ModelManifest>(json) ?? new ModelManifest();
    }
    else
    {
        manifest = new ModelManifest();
    }

    return new ModelRegistryService(
        modelsDir,
        manifest,
        settings,
        sp.GetRequiredService<ILogger<ModelRegistryService>>());
});
services.AddSingleton(sp => new ModelDownloadService(
    Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EyeCtrl", "models"),
    sp.GetRequiredService<HttpClient>(),
    sp.GetRequiredService<ILogger<ModelDownloadService>>()));
```

Add missing using for `System.Text.Json` at the top of AppBootstrapper.cs:

```csharp
using System.Text.Json;
```

- [ ] **Step 4: Verify build compiles**

Run: `dotnet build src/EyeCtrl.App`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Resources/models.json src/EyeCtrl.App/EyeCtrl.App.csproj src/EyeCtrl.App/Services/AppBootstrapper.cs
git commit -m "feat: add bundled models.json manifest and register model services in DI"
```

---

### Task 6: SettingsWindow Models Tab UI (BuildModelsPages)

**Files:**
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs`

- [ ] **Step 1: Add Models tab to _tabDefs and wire BuildModelsPages**

In `SettingsWindow.xaml.cs`, add Models to `_tabDefs` array (after the Keyboard entry, line 88):

```csharp
("Models",    "⬡", "Models",      BuildModelsPages),
```

Add the `BuildModelsPages` method. Also add a new color constant for delete buttons after line 43:

```csharp
private static readonly Color DeleteBase = Color.FromArgb(200, 200, 130, 40); // amber/orange for safety
```

Add the `BuildModelsPages` method after `BuildThemePages` (before `BuildGeneralPages`):

```csharp
private List<List<Action>> BuildModelsPages()
{
    var r = DpiHelper.LogicalToPhysicalX(ControlBtnRadius);
    var cat = DwellCategory.Control;

    // Page 1: Model management
    var page1 = new List<Action>();

    // Active model combo — cycles only through downloaded models
    var downloadedModels = _registryService.SelectableModels;
    if (downloadedModels.Count == 0)
    {
        page1.Add(() => Add(new DwellLabel("No models downloaded. Download one below.")));
    }
    else
    {
        var modelIds = downloadedModels.Select(m => m.DisplayName).ToList();
        var activeDisplay = downloadedModels.FirstOrDefault(m => m.Id == _registryService.ActiveModelId)?.DisplayName ?? "Off";
        page1.Add(() => Add(new DwellComboBox(_viewModel, "md_active", "Active Model",
            modelIds.Prepend("Off").ToList(),
            () => activeDisplay,
            v =>
            {
                var selected = downloadedModels.FirstOrDefault(m => m.DisplayName == v);
                _settings.Keyboard.MLModelSize = selected?.Id ?? "Off";
                _registryService.ActiveModelId = selected?.Id;
                ApplyAndSave();
            },
            cat, r, _dwellMs)));
    }

    // Separator line
    page1.Add(() => Add(new DwellLabel("\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500")));

    // List all models with download status
    var allEntries = _registryService.Registry.Values.OrderBy(e => e.SizeBytes).ToList();
    foreach (var entry in allEntries)
    {
        var entryId = entry.Id;
        var displayName = entry.DisplayName;
        var sizeText = FormatSize(entry.SizeBytes);

        if (entry.IsDownloaded)
        {
            page1.Add(() => Add(new DwellLabel($"  {displayName}  {sizeText}  \u2713 Downloaded")));
        }
        else
        {
            page1.Add(() => Add(new DwellButton(_viewModel, $"md_dl_{entryId}", $"{displayName}  {sizeText}  [Download]",
                () => ShowModal($"Download {displayName} ({sizeText})?", () =>
                {
                    // Fire-and-forget download with progress
                    _ = DownloadModelAsync(entryId, entry.Url, entry.Sha256,
                        entry.TokenizerUrl, entry.TokenizerSha256,
                        _registryService.GetModelFileName(entryId));
                }),
                BtnBase, cat, r, _dwellMs, width: 400, height: 60)));
        }
    }

    // Separator
    page1.Add(() => Add(new DwellLabel("\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500")));

    // Add Custom Model button — opens two-text-field modal
    page1.Add(() => Add(new DwellButton(_viewModel, "md_add", "\uff0b Add Custom Model",
        () => ShowCustomModelModal(),
        BtnBase, cat, r, _dwellMs, width: 300, height: 60)));

    // Manage models button (only if there are downloadable models that aren't active)
    var manageableModels = _registryService.DownloadedModels;
    if (manageableModels.Count > 0)
    {
        page1.Add(() => Add(new DwellButton(_viewModel, "md_manage", "Manage models...",
            () => _viewModel.Settings.NextPage(),
            BtnBase, cat, r, _dwellMs, width: 300, height: 60)));
    }

    // Page 2: Manage downloaded models (delete page)
    var page2 = new List<Action>();
    if (manageableModels.Count > 0)
    {
        foreach (var entry in manageableModels)
        {
            var entryId = entry.Id;
            var displayName = entry.DisplayName;
            var sizeText = FormatSize(entry.SizeBytes);
            page2.Add(() => Add(new DwellButton(_viewModel, $"md_del_{entryId}", $"{displayName}  {sizeText}  \uD83D\uDDD1 Delete",
                () => ShowModal($"Delete {displayName}? This cannot be undone.", () =>
                {
                    if (entry.Source == ModelSource.Custom)
                        _registryService.RemoveCustomModel(entryId, _settings.Keyboard);
                    else
                        _registryService.DeleteModelFiles(entryId);
                    ApplyAndSave();
                    RebuildContent();
                }),
                DeleteBase, cat, r, _dwellMs, width: 400, height: 60)));
        }

        // Back button
        page2.Add(() => Add(new DwellButton(_viewModel, "md_back", "Back to models",
            () => _viewModel.Settings.PreviousPage(),
            BtnBase, cat, r, _dwellMs, width: 300, height: 60)));
    }

    return page2.Count > 0 ? [page1, page2] : [page1];
}

private static string FormatSize(long bytes)
{
    if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
    if (bytes >= 1_048_576) return $"{bytes / 1_048_576:F0} MB";
    if (bytes >= 1024) return $"{bytes / 1024:F0} KB";
    return $"{bytes} B";
}

// Custom model modal — shown when user dwells "+ Add Custom Model"
private string _customModelName = "";
private string _customModelUrl = "";

// Shared download helper — queues download of both model and tokenizer
private async Task DownloadModelAsync(string modelId, string url, string sha256,
    string tokenizerUrl, string tokenizerSha256, string outputFileName)
{
    try
    {
        var downloaded = await _downloadService.DownloadModelAsync(
            modelId, outputFileName, url, sha256, CancellationToken.None);
        if (downloaded && !string.IsNullOrEmpty(tokenizerUrl))
        {
            var tokenizerFile = outputFileName.Replace(".onnx", ".tokenizer.json");
            await _downloadService.DownloadTokenizerAsync(
                modelId, tokenizerFile, tokenizerUrl, tokenizerSha256, CancellationToken.None);
        }
        Dispatcher.Invoke(() => RebuildContent());
    }
    catch (Exception ex)
    {
        Dispatcher.Invoke(() => RebuildContent());
    }
}

private void ShowCustomModelModal()
{
    _customModelName = "";
    _customModelUrl = "";

    ShowModal("Add Custom Model\n\nDisplay Name: [dwell to edit]\nRepo URL: [dwell to edit]\n\n\uD83D\uDCCB Paste from URL",
        () => DownloadCustomModel());
}

// Invoked from the simple ShowModal flow above; real implementation would
// use a multi-field modal with keyboard integration (see spec for full flow).
// For now, the user pastes a HuggingFace URL and the name is auto-derived.
private void DownloadCustomModel()
{
    try
    {
        var url = Clipboard.GetText(TextDataFormat.Text)?.Trim() ?? "";
        // Try to read URL — derivation is best-effort
        if (string.IsNullOrEmpty(url) || !url.StartsWith("https://"))
        {
            ShowError("Paste a valid HTTPS URL (e.g., HuggingFace model URL).");
            return;
        }

        // Derive display name from URL path
        var displayName = DeriveModelName(url);
        var tokenizerUrl = ModelDownloadService.GetTokenizerUrl(url);
        var sanitizedName = ModelRegistryService.SanitizeFileName(displayName);
        var finalName = _registryService.ResolveUniqueFileName(sanitizedName);

        // Download model
        _ = Task.Run(async () =>
        {
            var downloaded = await _downloadService.DownloadModelAsync(
                finalName, $"{finalName}.onnx", url, "");
            if (downloaded)
            {
                await _downloadService.DownloadTokenizerAsync(
                    finalName, tokenizerUrl, "");
                Dispatcher.Invoke(() =>
                {
                    _registryService.AddCustomModel(finalName, displayName, url, "", 0,
                        tokenizerUrl, "", _settings.Keyboard);
                    _settings.Keyboard.MLModelSize = finalName;
                    ApplyAndSave();
                    RebuildContent();
                });
            }
            else
            {
                Dispatcher.Invoke(() => ShowError("Download failed. Check the URL and try again."));
            }
        });
    }
    catch (Exception ex)
    {
        ShowError($"Invalid URL: {ex.Message}");
    }
}

private static string DeriveModelName(string url)
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

private void ShowError(string message)
{
    // Use the existing modal for error display
    ShowModal(message, () => { });
}
```

Add these field declarations to SettingsWindow right after `private readonly SettingsService _settingsService;` (after line 50):

```csharp
private readonly ModelRegistryService _registryService;
private readonly ModelDownloadService _downloadService;
```

Update the SettingsWindow constructor to accept these services:

```csharp
public SettingsWindow(
    SettingsWindowViewModel viewModel,
    EyeCtrlSettings settings,
    SettingsService settingsService,
    ModelRegistryService registryService,
    ModelDownloadService downloadService)
{
    InitializeComponent();

    _viewModel = viewModel;
    _settings = settings;
    _settingsService = settingsService;
    _registryService = registryService;
    _downloadService = downloadService;
    _dwellMs = settings.Dwell.ToolbarButtonDwellMs;
    // ... rest stays the same
```

Add using to the file:

```csharp
using EyeCtrl.Core.Prediction;
```

- [ ] **Step 2: Handle download concurrency — disable buttons during active download**

In `BuildModelsPages`, wrap download buttons with an `IsDownloading` check. The download button already dispatches to the shared `DownloadModelAsync` helper. Add the guard:

```csharp
// In the download button creation loop, replace the button label and action:
var isDownloading = _downloadService.IsDownloading;
var buttonLabel = isDownloading ? $"{displayName}  {sizeText}  [Waiting...]" : $"{displayName}  {sizeText}  [Download]";
var buttonAction = isDownloading
    ? () => { } // no-op — another download is in progress
    : () => ShowModal($"Download {displayName} ({sizeText})?", () =>
        _ = DownloadModelAsync(entryId, entry.Url, entry.Sha256,
            entry.TokenizerUrl, entry.TokenizerSha256,
            _registryService.GetModelFileName(entryId)));
```

- [ ] **Step 3: Verify build compiles**

Run: `dotnet build src/EyeCtrl.App`
Expected: Build succeeded after fixing constructor call in EventCoordinator.

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.App/Views/SettingsWindow.xaml.cs
git commit -m "feat: add Models tab UI with download, delete, and model management pages"
```

---

### Task 7: EventCoordinator Wiring

**Files:**
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs`

- [ ] **Step 1: Add SetModelServices method and use registry in LoadKeyboard**

Add fields to EventCoordinator (after `_appDataDir` on line 43):

```csharp
private ModelRegistryService? _modelRegistry;
private ModelDownloadService? _downloadService;
```

Add `SetModelServices` method (after field declarations):

```csharp
public void SetModelServices(ModelRegistryService modelRegistry, ModelDownloadService modelDownload)
{
    _modelRegistry = modelRegistry;
    _downloadService = modelDownload;
}
```

In `LoadKeyboard`, replace the `SmallLMPredictor` creation block (lines 740-770) with registry-backed resolution:

```csharp
// Create LM predictor if ML settings are enabled
SmallLMPredictor? lmPredictor = null;
if (settings.Keyboard.MLSuggestionsEnabled && _modelRegistry != null)
{
    var modelsDir = Path.Combine(_appDataDir!, "models");
    var modelSize = settings.Keyboard.MLModelSize;

    if (!string.IsNullOrEmpty(modelSize) && modelSize != "Off")
    {
        LMTokenizer? tokenizer = null;
        var tokenizerPath = _modelRegistry.GetTokenizerPath(modelSize);

        try
        {
            if (File.Exists(tokenizerPath))
                tokenizer = new LMTokenizer(tokenizerPath);
            else
                Log.Warning("Tokenizer file not found at {Path}; LM suggestions disabled", tokenizerPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load tokenizer from {Path}; LM suggestions disabled", tokenizerPath);
        }

        lmPredictor = new SmallLMPredictor(
            modelsDir,
            modelSize,
            settings.Keyboard.PreferGPU,
            tokenizer: tokenizer);
    }
}
```

- [ ] **Step 2: Register ShowKeyboardRequestMessage and HideKeyboardRequestMessage handlers**

In `EventCoordinator.Initialize()`, add these message handlers alongside the existing ones (around line 199):

```csharp
_context.Messenger.Register<EventCoordinator, ShowKeyboardRequestMessage>(this, (_, msg) =>
{
    if (_context?.OverlayManager.Keyboard == null) return;
    _keyboardVisible = true;
    _context.OverlayManager.ShowKeyboard();
    Log.Debug("Keyboard shown for text input (target handle: {Handle})", msg.TargetHandle);
});

_context.Messenger.Register<EventCoordinator, HideKeyboardRequestMessage>(this, (_, _) =>
{
    if (_context?.OverlayManager.Keyboard == null) return;
    _keyboardVisible = false;
    _context.OverlayManager.HideKeyboard();
    Log.Debug("Keyboard hidden");
});
```

The `using EyeCtrl.ViewModels.Messages;` import is already present in EventCoordinator.cs.

- [ ] **Step 3: Update ShowSettingsWindow to pass model services**

In `ShowSettingsWindow()`, update the SettingsWindow constructor call (around line 481):

```csharp
_settingsWindow = new SettingsWindow(
    settingsWindowVM,
    _context.Settings,
    _context.SettingsService,
    _modelRegistry!,
    _downloadService!);
```

- [ ] **Step 4: Verify build compiles**

Run: `dotnet build src/EyeCtrl.App`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Services/EventCoordinator.cs
git commit -m "feat: wire ModelRegistryService into EventCoordinator for LM loading"
```

---

### Task 8: App.xaml.cs Wiring

**Files:**
- Modify: `src/EyeCtrl.App/App.xaml.cs`

- [ ] **Step 1: Inject model services into EventCoordinator after creation**

Find where `EventCoordinator` is constructed in `App.xaml.cs` and add after construction:

```csharp
var modelRegistry = sp.GetRequiredService<ModelRegistryService>();
var modelDownload = sp.GetRequiredService<ModelDownloadService>();
eventCoordinator.SetModelServices(modelRegistry, modelDownload);
```

- [ ] **Step 2: Verify build compiles**

Run: `dotnet build src/EyeCtrl.App`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.App/App.xaml.cs
git commit -m "feat: wire ModelRegistryService into EventCoordinator from App.xaml.cs"
```

---

### Task 9: Tests

**Files:**
- Create: `tests/EyeCtrl.Core.Tests/Prediction/ModelRegistryServiceTests.cs`
- Modify: `tests/EyeCtrl.Core.Tests/Prediction/ModelDownloadServiceTests.cs`
- Modify: `tests/EyeCtrl.Core.Tests/Prediction/ModelManifestTests.cs`
- Create: `tests/EyeCtrl.ViewModels.Tests/ModelsTabTests.cs`

- [ ] **Step 1: Write ModelRegistryService unit tests**

```csharp
// tests/EyeCtrl.Core.Tests/Prediction/ModelRegistryServiceTests.cs

namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;

public class ModelRegistryServiceTests
{
    [Fact]
    public void Constructor_MergesBundledAndCustomModels()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"reg_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var manifest = new ModelManifest
            {
                Models = new()
                {
                    ["135M"] = new ModelEntry
                    {
                        DisplayName = "SmolLM2 135M",
                        Url = "https://example.com/135m.onnx",
                        Sha256 = "abc",
                        SizeBytes = 70_000_000,
                        TokenizerUrl = "https://example.com/tokenizer.json",
                        TokenizerSha256 = "def"
                    }
                }
            };
            var settings = new EyeCtrlSettings();
            settings.Keyboard.CustomModels.Add(new CustomModelEntry
            {
                Id = "MyModel",
                DisplayName = "My Custom Model",
                Url = "https://example.com/custom.onnx",
                Sha256 = "xyz",
                SizeBytes = 100_000_000,
                TokenizerUrl = "https://example.com/custom_tokenizer.json",
                TokenizerSha256 = "uvw"
            });

            var service = new ModelRegistryService(tempDir, manifest, settings);

            Assert.Equal(2, service.Registry.Count);
            Assert.True(service.Registry.ContainsKey("135M"));
            Assert.True(service.Registry.ContainsKey("MyModel"));
            Assert.Equal(ModelSource.Bundled, service.Registry["135M"].Source);
            Assert.Equal(ModelSource.Custom, service.Registry["MyModel"].Source);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetModelFileName_BundledModel_ReturnsMappedName()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"reg_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var manifest = new ModelManifest
            {
                Models = new()
                {
                    ["135M"] = new ModelEntry { DisplayName = "SmolLM2 135M", Url = "", TokenizerUrl = "" }
                }
            };
            var service = new ModelRegistryService(tempDir, manifest, new EyeCtrlSettings());

            var fileName = service.GetModelFileName("135M");
            Assert.Equal("smollm2-135m.onnx", fileName);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetModelFileName_CustomModel_ReturnsSanitizedName()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"reg_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var manifest = new ModelManifest();
            var settings = new EyeCtrlSettings();
            settings.Keyboard.CustomModels.Add(new CustomModelEntry
            {
                Id = "My Model! v2",
                DisplayName = "My Model",
                Url = "",
                TokenizerUrl = ""
            });

            var service = new ModelRegistryService(tempDir, manifest, settings);
            var fileName = service.GetModelFileName("My Model! v2");
            Assert.Equal("My_Model_v2.onnx", fileName);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SanitizeFileName_InvalidChars_ReplacesWithUnderscores()
    {
        var result = ModelRegistryService.SanitizeFileName("Hello! World? (test)");
        Assert.Equal("Hello_World_test", result);
    }

    [Fact]
    public void SanitizeFileName_CollapsesMultipleUnderscores()
    {
        var result = ModelRegistryService.SanitizeFileName("foo!!!bar");
        Assert.Equal("foo_bar", result);
    }

    [Fact]
    public void SanitizeFileName_TrimsLeadingTrailingUnderscores()
    {
        var result = ModelRegistryService.SanitizeFileName("!hello world!");
        Assert.Equal("hello_world", result);
    }

    [Fact]
    public void SanitizeFileName_EmptyInput_ReturnsFallback()
    {
        var result = ModelRegistryService.SanitizeFileName("!!!");
        Assert.Equal("custom_model", result);
    }

    [Fact]
    public void SanitizeFileName_MaxLengthEnforced()
    {
        var longName = new string('a', 128);
        var result = ModelRegistryService.SanitizeFileName(longName);
        Assert.True(result.Length <= 64);
    }

    [Fact]
    public void AddCustomModel_AddsToRegistryAndSettings()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"reg_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var manifest = new ModelManifest();
            var settings = new EyeCtrlSettings();
            var service = new ModelRegistryService(tempDir, manifest, settings);

            service.AddCustomModel("NewModel", "New Model", "https://example.com/model.onnx",
                "hash", 50000000, "https://example.com/tokenizer.json", "tokenizerHash", settings.Keyboard);

            Assert.True(service.Registry.ContainsKey("NewModel"));
            Assert.Single(settings.Keyboard.CustomModels);
            Assert.Equal("New Model", settings.Keyboard.CustomModels[0].DisplayName);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void RemoveCustomModel_RemovesFromRegistryAndSettings()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"reg_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var manifest = new ModelManifest();
            var settings = new EyeCtrlSettings();
            settings.Keyboard.CustomModels.Add(new CustomModelEntry
            {
                Id = "ToDelete",
                DisplayName = "Delete Me",
                Url = "",
                TokenizerUrl = ""
            });

            var service = new ModelRegistryService(tempDir, manifest, settings);
            Assert.True(service.Registry.ContainsKey("ToDelete"));

            service.RemoveCustomModel("ToDelete", settings.Keyboard);
            Assert.False(service.Registry.ContainsKey("ToDelete"));
            Assert.Empty(settings.Keyboard.CustomModels);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
```

- [ ] **Step 2: Run Core tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~ModelRegistryServiceTests" -v`
Expected: PASS (10 tests)

- [ ] **Step 3: Write ModelDownloadService GetTokenizerUrl tests**

Add to existing `tests/EyeCtrl.Core.Tests/Prediction/ModelDownloadServiceTests.cs`:

```csharp
[Fact]
public void GetTokenizerUrl_StandardOnnxUrl_ReplacesLastSegment()
{
    var url = "https://huggingface.co/org/repo/resolve/main/onnx/model.onnx";
    var result = ModelDownloadService.GetTokenizerUrl(url);
    Assert.Equal("https://huggingface.co/org/repo/resolve/main/onnx/tokenizer.json", result);
}

[Fact]
public void GetTokenizerUrl_UrlWithQueryParams_StripsThem()
{
    var url = "https://huggingface.co/org/repo/resolve/main/model.onnx?download=true";
    var result = ModelDownloadService.GetTokenizerUrl(url);
    Assert.Equal("https://huggingface.co/org/repo/resolve/main/tokenizer.json", result);
}

[Fact]
public void GetTokenizerUrl_EmptyUrl_Throws()
{
    Assert.Throws<ArgumentException>(() => ModelDownloadService.GetTokenizerUrl(""));
}

[Fact]
public void GetTokenizerUrl_RootPath_ReturnsTokenizer()
{
    // A URL with a single path segment still works — the filename is replaced
    var url = "https://example.com/model.onnx";
    var result = ModelDownloadService.GetTokenizerUrl(url);
    Assert.Equal("https://example.com/tokenizer.json", result);
}

[Fact]
public void GetTokenizerUrl_NestedPath_ReplacesLastComponent()
{
    var url = "https://example.com/a/b/c/model.onnx";
    var result = ModelDownloadService.GetTokenizerUrl(url);
    Assert.Equal("https://example.com/a/b/c/tokenizer.json", result);
}
```

- [ ] **Step 4: Run Core download tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~ModelDownloadServiceTests" -v`
Expected: PASS (9 tests — 4 existing + 5 new)

- [ ] **Step 5: Write ModelManifest displayName test**

Add to `tests/EyeCtrl.Core.Tests/Prediction/ModelManifestTests.cs`:

```csharp
[Fact]
public void Deserialize_WithDisplayName_ParsesField()
{
    var json = """
    {
        "models": {
            "135M": {
                "displayName": "SmolLM2 135M",
                "url": "https://example.com/model.onnx",
                "sha256": "abc",
                "sizeBytes": 70000000,
                "tokenizerUrl": "https://example.com/tokenizer.json",
                "tokenizerSha256": "def"
            }
        }
    }
    """;

    var manifest = JsonSerializer.Deserialize<ModelManifest>(json);
    Assert.NotNull(manifest);
    var entry = manifest!.Models["135M"];
    Assert.Equal("SmolLM2 135M", entry.DisplayName);
    Assert.Equal("https://example.com/tokenizer.json", entry.TokenizerUrl);
    Assert.Equal("def", entry.TokenizerSha256);
}
```

- [ ] **Step 6: Run Core manifest tests**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~ModelManifestTests" -v`
Expected: PASS (2 tests)

- [ ] **Step 7: Run all Core tests**

Run: `dotnet test tests/EyeCtrl.Core.Tests -v`
Expected: All tests pass (no regressions).

- [ ] **Step 8: Commit**

```bash
git add tests/EyeCtrl.Core.Tests/Prediction/ModelRegistryServiceTests.cs tests/EyeCtrl.Core.Tests/Prediction/ModelDownloadServiceTests.cs tests/EyeCtrl.Core.Tests/Prediction/ModelManifestTests.cs
git commit -m "test: add ModelRegistryService, GetTokenizerUrl, and ModelManifest displayName tests"
```

---

### Task 10: Final Integration Verification

- [ ] **Step 1: Build entire solution**

Run: `dotnet build EyeCtrl.slnx`
Expected: Build succeeded with zero errors.

- [ ] **Step 2: Run all unit tests**

Run: `dotnet test tests/EyeCtrl.Core.Tests tests/EyeCtrl.Platform.Tests tests/EyeCtrl.ViewModels.Tests`
Expected: All tests pass.

- [ ] **Step 3: Run detect_changes to verify scope**

Use GitNexus: `gitnexus_detect_changes()` — verify only expected symbols are affected.

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.Core src/EyeCtrl.App src/EyeCtrl.ViewModels tests/EyeCtrl.Core.Tests
git commit -m "chore: final integration verification"
```
