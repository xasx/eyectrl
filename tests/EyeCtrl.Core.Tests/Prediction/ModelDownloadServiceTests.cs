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
            File.WriteAllText(Path.Combine(tempDir, "smollm2-360m.onnx"), "dummy");

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
}
