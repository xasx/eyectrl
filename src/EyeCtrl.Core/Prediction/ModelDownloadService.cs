namespace EyeCtrl.Core.Prediction;

using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

public class ModelDownloadService : IDisposable
{
    private readonly string _modelsDirectory;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly ILogger<ModelDownloadService>? _logger;

    private volatile bool _isDownloading;
    private CancellationTokenSource? _downloadCts;
    private readonly SemaphoreSlim _downloadSemaphore = new(1, 1);

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

    public string GetTokenizerPath(string modelSize)
    {
        return Path.Combine(_modelsDirectory, GetTokenizerFileName(modelSize));
    }

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

    public string GetModelPath(string modelSize)
    {
        if (!ModelConstants.ModelFileNames.TryGetValue(modelSize, out var fileName))
            throw new ArgumentException($"Unknown model size: {modelSize}");
        return Path.Combine(_modelsDirectory, fileName);
    }

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

        if (!await _downloadSemaphore.WaitAsync(0, cancellationToken))
        {
            ProgressChanged?.Invoke(new ModelDownloadProgress { ModelId = modelId, Error = "Another download is already in progress" });
            return false;
        }

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

            var buffer = new byte[8192];
            long totalRead = 0;
            int bytesRead;

            using var sha256 = System.Security.Cryptography.SHA256.Create();
            using (var fileStream = File.Create(tempPath))
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

            if (!string.IsNullOrEmpty(expectedSha256) &&
                !string.Equals(computedHash, expectedSha256, System.StringComparison.OrdinalIgnoreCase))
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
            _downloadSemaphore.Release();
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient?.Dispose();
    }

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
