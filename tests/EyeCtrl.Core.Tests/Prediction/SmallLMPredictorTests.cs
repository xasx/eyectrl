namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;
using Microsoft.Extensions.Logging.Abstractions;

public class SmallLMPredictorTests
{
    [Fact]
    public void Constructor_NoModelFiles_IsNotAvailable()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lm_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var predictor = new SmallLMPredictor(tempDir, modelSize: "135M", preferGpu: false, NullLogger<SmallLMPredictor>.Instance, tokenizer: null);
            Check.That(predictor.IsAvailable).IsFalse();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Constructor_InvalidModelPath_IsNotAvailable()
    {
        var predictor = new SmallLMPredictor(@"C:\nonexistent\path", modelSize: "360M", preferGpu: false, NullLogger<SmallLMPredictor>.Instance, tokenizer: null);
        Check.That(predictor.IsAvailable).IsFalse();
    }

    [Fact]
    public void Constructor_ModelSizeOff_IsNotAvailable()
    {
        var predictor = new SmallLMPredictor(@"C:\nonexistent", modelSize: "Off", preferGpu: false, NullLogger<SmallLMPredictor>.Instance, tokenizer: null);
        Check.That(predictor.IsAvailable).IsFalse();
    }

    [Fact]
    public void Predict_WhenNotAvailable_ReturnsNull()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lm_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var predictor = new SmallLMPredictor(tempDir, modelSize: "135M", preferGpu: false, NullLogger<SmallLMPredictor>.Instance, tokenizer: null);
            predictor.Predict("hello world", "wo");
            var results = predictor.TryGetResults();
            Check.That(results).IsNull();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lm_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            using var predictor = new SmallLMPredictor(tempDir, modelSize: "135M", preferGpu: false, NullLogger<SmallLMPredictor>.Instance, tokenizer: null);
            predictor.Dispose();
            predictor.Dispose();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
