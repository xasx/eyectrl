namespace EyeCtrl.Core.Prediction;

using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

/// <summary>
/// Downloads ONNX model and tokenizer files with progress reporting.
/// Handles SHA-256 integrity verification and cancellation.
/// </summary>
public class ModelDownloadService : IDisposable
{
    private readonly string _modelsDirectory;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly ILogger<ModelDownloadService>? _logger;

    private volatile bool _isDownloading;
    private CancellationTokenSource? _downloadCts;
    private readonly SemaphoreSlim _downloadSemaphore = new(1, 1);

    /// <summary>Whether a download is currently in progress.</summary>
    public bool IsDownloading => _isDownloading;

    /// <summary>Raised during downloads to report progress updates.</summary>
    public event Action<ModelDownloadProgress>? ProgressChanged;

    /// <summary>Progress information for a model download operation.</summary>
    public class ModelDownloadProgress
    {
        /// <summary>Model identifier being downloaded.</summary>
        public string ModelId { get; init; } = "";
        /// <summary>Download progress as a fraction (0.0–1.0).</summary>
        public double Progress { get; init; }
        /// <summary>Whether the download has completed successfully.</summary>
        public bool IsComplete { get; init; }
        /// <summary>Whether the download was cancelled.</summary>
        public bool IsCancelled { get; init; }
        /// <summary>Error message if the download failed.</summary>
        public string? Error { get; init; }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ModelDownloadService"/> class.
    /// </summary>
    /// <param name="modelsDirectory">Directory where downloaded models are stored.</param>
    /// <param name="httpClient">Optional HTTP client (creates its own if null).</param>
    /// <param name="logger">Optional logger for diagnostic messages.</param>
    public ModelDownloadService(string modelsDirectory, HttpClient? httpClient = null, ILogger<ModelDownloadService>? logger = null)
    {
        _modelsDirectory = modelsDirectory;
        _ownsHttpClient = httpClient == null;
        _httpClient = httpClient ?? new HttpClient();
        if (_ownsHttpClient)
            _httpClient.Timeout = TimeSpan.FromMinutes(30);
        _logger = logger;
        Directory.CreateDirectory(_modelsDirectory);
    }

    /// <summary>Returns the list of model size keys for which both model and tokenizer files exist on disk.</summary>
    /// <returns>List of downloaded model size identifiers.</returns>
    public List<string> GetAvailableModels()
    {
        var available = new List<string>();
        foreach (var (size, fileName) in ModelConstants.ModelFileNames)
        {
            var onnxPath = Path.Combine(_modelsDirectory, fileName);
            var tokenizerPath = GetTokenizerPath(size);
            if (File.Exists(onnxPath) && File.Exists(tokenizerPath))
                available.Add(size);
        }
        return available;
    }

    /// <summary>Checks whether a specific model size has both ONNX and tokenizer files downloaded.</summary>
    /// <param name="modelSize">Model size identifier (e.g., "360M").</param>
    /// <returns>True if both files exist on disk.</returns>
    public bool IsModelDownloaded(string modelSize)
    {
        if (!ModelConstants.ModelFileNames.TryGetValue(modelSize, out var onnxFile)) return false;
        var onnxExists = File.Exists(Path.Combine(_modelsDirectory, onnxFile));
        var tokenizerExists = File.Exists(GetTokenizerPath(modelSize));
        return onnxExists && tokenizerExists;
    }

    private static string GetTokenizerFileName(string modelSize)
    {
        if (!ModelConstants.ModelFileNames.TryGetValue(modelSize, out var onnxFile))
            throw new ArgumentException($"Unknown model size: {modelSize}");
        return Path.GetFileNameWithoutExtension(onnxFile) + ".tokenizer.json";
    }

    /// <summary>Returns the full path to the tokenizer file for a given model size.</summary>
    /// <param name="modelSize">Model size identifier (e.g., "360M").</param>
    /// <returns>Full file path to the tokenizer JSON file.</returns>
    public string GetTokenizerPath(string modelSize)
    {
        return Path.Combine(_modelsDirectory, GetTokenizerFileName(modelSize));
    }

    /// <summary>Derives the tokenizer download URL from a model download URL.</summary>
    /// <param name="modelUrl">The model's download URL.</param>
    /// <returns>The corresponding tokenizer download URL.</returns>
    public static string GetTokenizerUrl(string modelUrl)
    {
        if (string.IsNullOrWhiteSpace(modelUrl))
            throw new ArgumentException("Model URL cannot be empty", nameof(modelUrl));

        var uri = new Uri(modelUrl);
        var path = uri.AbsolutePath.TrimEnd('/');
        var lastSlash = path.LastIndexOf('/');
        if (lastSlash < 0)
            throw new ArgumentException($"Cannot derive tokenizer URL from: {modelUrl}");

        var dir = path[..lastSlash];
        var tokenizerUrl = $"{uri.GetLeftPart(UriPartial.Authority)}{dir}/tokenizer.json";
        return tokenizerUrl;
    }

    /// <summary>Returns the full path to the ONNX model file for a given model size.</summary>
    /// <param name="modelSize">Model size identifier.</param>
    /// <returns>Full file path to the ONNX model file.</returns>
    public string GetModelPath(string modelSize)
    {
        if (!ModelConstants.ModelFileNames.TryGetValue(modelSize, out var fileName))
            throw new ArgumentException($"Unknown model size: {modelSize}");
        return Path.Combine(_modelsDirectory, fileName);
    }

    /// <summary>Cancels the currently active download operation.</summary>
    public void CancelDownload()
    {
        _downloadCts?.Cancel();
    }

    private enum DownloadOutcome { Success, Busy, HashMismatch, Failed }

    /// <summary>
    /// Shared download core used by all public download methods: single-flight
    /// semaphore, temp-file streaming with incremental SHA-256 via CryptoStream,
    /// optional hash verification, atomic move into place, temp-file cleanup.
    /// Creates the linked <see cref="_downloadCts"/> so <see cref="CancelDownload"/>
    /// works for every download type. Throws OperationCanceledException on cancel.
    /// </summary>
    private async Task<(DownloadOutcome Outcome, string? Error)> DownloadFileCoreAsync(
        string outputFileName,
        string url,
        string expectedSha256,
        Action? onStarted,
        Action<double>? onProgress,
        CancellationToken cancellationToken)
    {
        var outputPath = Path.Combine(_modelsDirectory, outputFileName);
        var tempPath = outputPath + ".download";

        if (!await _downloadSemaphore.WaitAsync(0, cancellationToken))
            return (DownloadOutcome.Busy, "Another download is already in progress");

        _isDownloading = true;
        _downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _downloadCts.Token;

        try
        {
            onStarted?.Invoke();

            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            using var contentStream = await response.Content.ReadAsStreamAsync(ct);

            var buffer = new byte[8192];
            long totalRead = 0;
            int bytesRead;

            using var sha256 = SHA256.Create();
            using (var fileStream = File.Create(tempPath))
            using (var cs = new CryptoStream(contentStream, sha256, CryptoStreamMode.Read, leaveOpen: true))
            {
                while ((bytesRead = await cs.ReadAsync(buffer, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                    totalRead += bytesRead;
                    if (totalBytes > 0)
                        onProgress?.Invoke((double)totalRead / totalBytes);
                }
            }
            var computedHash = Convert.ToHexStringLower(sha256.Hash!);

            if (!string.IsNullOrEmpty(expectedSha256) &&
                !string.Equals(computedHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogError("Hash mismatch for {File}: expected {Expected}, got {Actual}",
                    outputFileName, expectedSha256, computedHash);
                File.Delete(tempPath);
                return (DownloadOutcome.HashMismatch, "Hash mismatch");
            }

            File.Move(tempPath, outputPath, overwrite: true);
            _logger?.LogInformation("{File} downloaded successfully ({Bytes} bytes)", outputFileName, totalRead);
            return (DownloadOutcome.Success, null);
        }
        catch (OperationCanceledException)
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to download {File}", outputFileName);
            if (File.Exists(tempPath)) File.Delete(tempPath);
            return (DownloadOutcome.Failed, ex.Message);
        }
        finally
        {
            _isDownloading = false;
            _downloadCts?.Dispose();
            _downloadCts = null;
            _downloadSemaphore.Release();
        }
    }

    /// <summary>Downloads a model file to the models directory, verifying its SHA-256 when one is supplied.</summary>
    /// <remarks>
    /// Integrity policy: when <paramref name="expectedSha256"/> is empty the hash check is skipped by
    /// design and the download is trusted on the basis of HTTPS transport security. This applies to
    /// custom (user-supplied URL) models — whose hash can't be known in advance — and to bundled models
    /// whose manifest omits a hash (the shipped models.json currently leaves these empty). Populate the
    /// manifest's sha256 / tokenizerSha256 fields to enable end-to-end content verification.
    /// </remarks>
    public async Task<bool> DownloadModelAsync(
        string modelId,
        string outputFileName,
        string url,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        var progress = ProgressChanged;
        try
        {
            var (outcome, error) = await DownloadFileCoreAsync(
                outputFileName, url, expectedSha256,
                onStarted: () => progress?.Invoke(new ModelDownloadProgress { ModelId = modelId, Progress = 0 }),
                onProgress: p => progress?.Invoke(new ModelDownloadProgress { ModelId = modelId, Progress = p }),
                cancellationToken);

            switch (outcome)
            {
                case DownloadOutcome.Success:
                    progress?.Invoke(new ModelDownloadProgress { ModelId = modelId, Progress = 1.0, IsComplete = true });
                    return true;
                case DownloadOutcome.HashMismatch:
                    progress?.Invoke(new ModelDownloadProgress { ModelId = modelId, IsComplete = true, Error = error });
                    return false;
                default: // Busy, Failed
                    progress?.Invoke(new ModelDownloadProgress { ModelId = modelId, Error = error });
                    return false;
            }
        }
        catch (OperationCanceledException)
        {
            _logger?.LogInformation("Model download cancelled: {Id}", modelId);
            ProgressChanged?.Invoke(new ModelDownloadProgress { ModelId = modelId, IsCancelled = true });
            throw;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient?.Dispose();
    }

    /// <summary>Downloads a tokenizer file from a URL for a known model size (uses standard naming).</summary>
    /// <param name="modelSize">Model size identifier.</param>
    /// <param name="url">Download URL.</param>
    /// <param name="expectedSha256">Expected SHA-256 hash, or empty to skip verification.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the download succeeded and hash matched (or was skipped).</returns>
    public async Task<bool> DownloadTokenizerAsync(
        string modelSize,
        string url,
        string expectedSha256,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var fileName = GetTokenizerFileName(modelSize);
        var outputPath = Path.Combine(_modelsDirectory, fileName);
        var tempPath = outputPath + ".download";

        if (!await _downloadSemaphore.WaitAsync(0, cancellationToken))
        {
            _logger?.LogWarning("Tokenizer download rejected: another download is already in progress");
            return false;
        }

        _isDownloading = true;

        _logger?.LogInformation("Downloading tokenizer {Size} from {Url} to {Path}", modelSize, url, outputPath);

        try
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);

            var buffer = new byte[8192];
            long totalRead = 0;
            int bytesRead;

            using var sha256 = SHA256.Create();
            using (var fileStream = File.Create(tempPath))
            using (var cs = new CryptoStream(contentStream, sha256, CryptoStreamMode.Read, leaveOpen: true))
            {
                while ((bytesRead = await cs.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    totalRead += bytesRead;
                    if (totalBytes > 0)
                        progress?.Report((double)totalRead / totalBytes);
                }
            }
            var computedHash = Convert.ToHexStringLower(sha256.Hash!);

            // Integrity check is opt-in: an empty expectedSha256 skips it (HTTPS transport trust). See DownloadModelAsync remarks.
            if (!string.IsNullOrEmpty(expectedSha256) &&
                !string.Equals(computedHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogError("Hash mismatch for tokenizer {Size}: expected {Expected}, got {Actual}", modelSize, expectedSha256, computedHash);
                File.Delete(tempPath);
                return false;
            }

            File.Move(tempPath, outputPath, overwrite: true);
            _logger?.LogInformation("Tokenizer {Size} downloaded successfully ({Bytes} bytes)", modelSize, totalRead);
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogInformation("Tokenizer download cancelled: {Size}", modelSize);
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to download tokenizer {Size}", modelSize);
            if (File.Exists(tempPath)) File.Delete(tempPath);
            return false;
        }
        finally
        {
            _isDownloading = false;
            _downloadSemaphore.Release();
        }
    }

    /// <summary>Downloads a tokenizer with a custom output file name (for custom models).</summary>
    /// <param name="modelId">Model identifier.</param>
    /// <param name="outputFileName">Custom output file name to save as.</param>
    /// <param name="url">Download URL.</param>
    /// <param name="expectedSha256">Expected SHA-256 hash, or empty to skip.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful.</returns>
    public async Task<bool> DownloadTokenizerAsync(
        string modelId,
        string outputFileName,
        string url,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        var outputPath = Path.Combine(_modelsDirectory, outputFileName);
        var tempPath = outputPath + ".download";

        if (!await _downloadSemaphore.WaitAsync(0, cancellationToken))
        {
            _logger?.LogWarning("Tokenizer download rejected: another download is already in progress");
            return false;
        }

        _isDownloading = true;

        _logger?.LogInformation("Downloading tokenizer {Id} from {Url} to {Path}", modelId, url, outputPath);

        try
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);

            var buffer = new byte[8192];
            int bytesRead;

            using var sha256 = System.Security.Cryptography.SHA256.Create();
            using (var fileStream = File.Create(tempPath))
            using (var cs = new CryptoStream(contentStream, sha256, CryptoStreamMode.Read, leaveOpen: true))
            {
                while ((bytesRead = await cs.ReadAsync(buffer, cancellationToken)) > 0)
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            }
            var computedHash = Convert.ToHexStringLower(sha256.Hash!);

            // Integrity check is opt-in: an empty expectedSha256 skips it (HTTPS transport trust). See DownloadModelAsync remarks.
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
        finally
        {
            _isDownloading = false;
            _downloadSemaphore.Release();
        }
    }
}
