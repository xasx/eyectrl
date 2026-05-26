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
            Assert.Empty(available);
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

            Assert.Contains("135M", available);
            Assert.Contains("360M", available);
            Assert.DoesNotContain("1.5B", available);
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

            Assert.True(service.IsModelDownloaded("1.5B"));
            Assert.False(service.IsModelDownloaded("360M"));
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

            Assert.EndsWith("smollm2-360m.onnx", path);
            Assert.StartsWith(tempDir, path);
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

            Assert.EndsWith("smollm2-360m.tokenizer.json", path);
            Assert.StartsWith(tempDir, path);
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

            Assert.False(service.IsModelDownloaded("135M"));
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

            Assert.DoesNotContain("135M", available);
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

            Assert.True(service.IsModelDownloaded("1.5B"));
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
}
