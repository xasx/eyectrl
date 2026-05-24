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
            var path = Path.Combine(_modelsDirectory, fileName);
            if (File.Exists(path))
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

    public string GetModelPath(string modelSize)
    {
        if (!ModelConstants.ModelFileNames.TryGetValue(modelSize, out var fileName))
            throw new ArgumentException($"Unknown model size: {modelSize}");
        return Path.Combine(_modelsDirectory, fileName);
    }

    public async Task<bool> DownloadModelAsync(
        string modelSize,
        string url,
        string expectedSha256,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!ModelConstants.ModelFileNames.TryGetValue(modelSize, out var fileName))
            throw new ArgumentException($"Unknown model size: {modelSize}");

        var outputPath = Path.Combine(_modelsDirectory, fileName);
        var tempPath = outputPath + ".download";

        _logger?.LogInformation("Downloading model {Size} from {Url} to {Path}", modelSize, url, outputPath);

        try
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var fileStream = File.Create(tempPath);

            var buffer = new byte[8192];
            long totalRead = 0;
            int bytesRead;

            using var sha256 = SHA256.Create();
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

            if (!string.Equals(computedHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogError("Hash mismatch for model {Size}: expected {Expected}, got {Actual}", modelSize, expectedSha256, computedHash);
                File.Delete(tempPath);
                return false;
            }

            File.Move(tempPath, outputPath, overwrite: true);
            _logger?.LogInformation("Model {Size} downloaded successfully ({Bytes} bytes)", modelSize, totalRead);
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogInformation("Model download cancelled: {Size}", modelSize);
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to download model {Size}", modelSize);
            if (File.Exists(tempPath)) File.Delete(tempPath);
            return false;
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

        _logger?.LogInformation("Downloading tokenizer {Size} from {Url} to {Path}", modelSize, url, outputPath);

        try
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var fileStream = File.Create(tempPath);

            var buffer = new byte[8192];
            long totalRead = 0;
            int bytesRead;

            using var sha256 = SHA256.Create();
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

            if (!string.Equals(computedHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
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
    }
}
