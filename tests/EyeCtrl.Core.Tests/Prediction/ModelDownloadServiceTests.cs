namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;

public class ModelDownloadServiceTests
{
    [Fact]
    public void GetAvailableModels_NoModelsDirectory_ReturnsEmpty()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"model_test_{Guid.NewGuid()}");
        try
        {
            var service = new ModelDownloadService(tempDir);
            var available = service.GetAvailableModels();
            Check.That(available).IsEmpty();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetAvailableModels_WithValidFiles_ReturnsModelSizes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"model_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "smollm2-135m.onnx"), "dummy");
            File.WriteAllText(Path.Combine(tempDir, "smollm2-135m.tokenizer.json"), "dummy");
            File.WriteAllText(Path.Combine(tempDir, "smollm2-360m.onnx"), "dummy");
            File.WriteAllText(Path.Combine(tempDir, "smollm2-360m.tokenizer.json"), "dummy");

            var service = new ModelDownloadService(tempDir);
            var available = service.GetAvailableModels();

            Check.That(available).Contains("135M");
            Check.That(available).Contains("360M");
            Check.That(available.Contains("1.5B")).IsFalse();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void IsModelDownloaded_ExistingFile_ReturnsTrue()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"model_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "qwen2.5-1.5b.onnx"), "dummy");
            File.WriteAllText(Path.Combine(tempDir, "qwen2.5-1.5b.tokenizer.json"), "dummy");
            var service = new ModelDownloadService(tempDir);

            Check.That(service.IsModelDownloaded("1.5B")).IsTrue();
            Check.That(service.IsModelDownloaded("360M")).IsFalse();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetModelPath_ReturnsFullPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"model_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var service = new ModelDownloadService(tempDir);
            var path = service.GetModelPath("360M");

            Check.That(path).EndsWith("smollm2-360m.onnx");
            Check.That(path).StartsWith(tempDir);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetTokenizerPath_ReturnsFullPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"model_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var service = new ModelDownloadService(tempDir);
            var path = service.GetTokenizerPath("360M");

            Check.That(path).EndsWith("smollm2-360m.tokenizer.json");
            Check.That(path).StartsWith(tempDir);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void IsModelDownloaded_WithOnnxButNoTokenizer_ReturnsFalse()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"model_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "smollm2-135m.onnx"), "dummy");
            var service = new ModelDownloadService(tempDir);

            Check.That(service.IsModelDownloaded("135M")).IsFalse();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetAvailableModels_WithOnnxButNoTokenizer_ExcludesModel()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"model_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "smollm2-135m.onnx"), "dummy");
            var service = new ModelDownloadService(tempDir);

            var available = service.GetAvailableModels();

            Check.That(available.Contains("135M")).IsFalse();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void IsModelDownloaded_WithOnnxAndTokenizer_ReturnsTrue()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"model_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "qwen2.5-1.5b.onnx"), "dummy");
            File.WriteAllText(Path.Combine(tempDir, "qwen2.5-1.5b.tokenizer.json"), "dummy");
            var service = new ModelDownloadService(tempDir);

            Check.That(service.IsModelDownloaded("1.5B")).IsTrue();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetTokenizerUrl_StandardOnnxUrl_ReplacesLastSegment()
    {
        var url = "https://huggingface.co/org/repo/resolve/main/onnx/model.onnx";
        var result = ModelDownloadService.GetTokenizerUrl(url);
        Check.That(result).IsEqualTo("https://huggingface.co/org/repo/resolve/main/onnx/tokenizer.json");
    }

    [Fact]
    public void GetTokenizerUrl_UrlWithQueryParams_StripsThem()
    {
        var url = "https://huggingface.co/org/repo/resolve/main/model.onnx?download=true";
        var result = ModelDownloadService.GetTokenizerUrl(url);
        Check.That(result).IsEqualTo("https://huggingface.co/org/repo/resolve/main/tokenizer.json");
    }

    [Fact]
    public void GetTokenizerUrl_EmptyUrl_Throws()
    {
        Check.ThatCode(() => ModelDownloadService.GetTokenizerUrl("")).Throws<ArgumentException>();
    }

    [Fact]
    public void GetTokenizerUrl_RootPath_ReturnsTokenizer()
    {
        var url = "https://example.com/model.onnx";
        var result = ModelDownloadService.GetTokenizerUrl(url);
        Check.That(result).IsEqualTo("https://example.com/tokenizer.json");
    }

    [Fact]
    public void GetTokenizerUrl_NestedPath_ReplacesLastComponent()
    {
        var url = "https://example.com/a/b/c/model.onnx";
        var result = ModelDownloadService.GetTokenizerUrl(url);
        Check.That(result).IsEqualTo("https://example.com/a/b/c/tokenizer.json");
    }
}
