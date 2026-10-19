namespace EyeCtrl.LM.Tests;

using EyeCtrl.Core.Prediction;
using EyeCtrl.LM;
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

    [Fact]
    public void Dispose_WithOwnsTokenizerFalse_LeavesInjectedTokenizerAlive()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lm_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var tokenizerPath = Path.Combine(tempDir, "tokenizer.json");
            TestTokenizerJson.Write(tokenizerPath);
            using var tokenizer = new LMTokenizer(tokenizerPath);

            var predictor = new SmallLMPredictor(tempDir, modelSize: "135M", preferGpu: false,
                NullLogger<SmallLMPredictor>.Instance, tokenizer: tokenizer, modelPath: null,
                ownsTokenizer: false);
            predictor.Dispose();

            Check.ThatCode(() => tokenizer.Encode("hello")).DoesNotThrow();
        }
        finally { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
    }

    [Fact]
    public void Dispose_DefaultOwnership_DisposesInjectedTokenizer()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lm_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var tokenizerPath = Path.Combine(tempDir, "tokenizer.json");
            TestTokenizerJson.Write(tokenizerPath);
            var tokenizer = new LMTokenizer(tokenizerPath);

            var predictor = new SmallLMPredictor(tempDir, modelSize: "135M", preferGpu: false,
                NullLogger<SmallLMPredictor>.Instance, tokenizer: tokenizer);
            predictor.Dispose();

            Check.ThatCode(() => tokenizer.Encode("hello")).Throws<ObjectDisposedException>();
        }
        finally { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
    }

    [Fact]
    public void PostProcess_MultiWordGeneration_EmitsOnlyFirstWord()
    {
        var results = SmallLMPredictor.PostProcess("world peace now", prefix: null, avgProb: 0.5);
        Check.That(results).HasSize(1);
        Check.That(results[0].Word).IsEqualTo("world");
    }

    [Fact]
    public void PostProcess_WithPrefix_AppendsTheContinuationToWhatWasTyped()
    {
        var results = SmallLMPredictor.PostProcess("terdaten", prefix: "Wet", avgProb: 0.5);
        Check.That(results).HasSize(1);
        Check.That(results[0].Word).IsEqualTo("Wetterdaten");
    }

    [Fact]
    public void PostProcess_ContinuationStartingWithWhitespace_EmitsNothing()
    {
        // "we" is already a complete word; next-word prediction is out of scope.
        var results = SmallLMPredictor.PostProcess(" are", prefix: "we", avgProb: 0.5);
        Check.That(results).IsEmpty();
    }

    [Fact]
    public void PostProcess_NonWordContinuation_EmitsNothing()
    {
        var results = SmallLMPredictor.PostProcess("::shutter(QWidget", prefix: "h", avgProb: 0.5);
        Check.That(results).IsEmpty();
    }

    [Fact]
    public void PostProcess_GermanContinuation_IsAppendedVerbatim()
    {
        var results = SmallLMPredictor.PostProcess("chtest gern", prefix: "mö", avgProb: 0.5);
        Check.That(results).HasSize(1);
        Check.That(results[0].Word).IsEqualTo("möchtest");
    }

    [Fact]
    public void PostProcess_TrailingPunctuation_IsCut()
    {
        var results = SmallLMPredictor.PostProcess("ter.", prefix: "Wet", avgProb: 0.5);
        Check.That(results).HasSize(1);
        Check.That(results[0].Word).IsEqualTo("Wetter");
    }

    [Fact]
    public void PostProcess_EmptyPrefix_EmitsTheFirstCleanedWord()
    {
        var results = SmallLMPredictor.PostProcess("Hello, world", prefix: null, avgProb: 0.5);
        Check.That(results).HasSize(1);
        Check.That(results[0].Word).IsEqualTo("Hello");
    }

    [Fact]
    public void PostProcess_WhitespaceOnly_EmitsNothing()
    {
        Check.That(SmallLMPredictor.PostProcess("   ", prefix: null, avgProb: 0.5)).IsEmpty();
    }

    [Fact]
    public void LastTokenLogits_ReadsTheFinalPositionRow()
    {
        // logits arrive as [batch=1, sequence, vocab]; only the final position predicts
        // the next token. The vocab width is the model's, which is NOT the tokenizer's
        // vocab count - Qwen2.5 ships 151936 logits against a 151665-entry tokenizer.
        float[] logits = [10, 11, 12, /**/ 20, 21, 22, /**/ 30, 31, 32];

        var last = SmallLMPredictor.LastTokenLogits(logits, sequenceLength: 3, vocabSize: 3);

        Check.That(last).ContainsExactly(30f, 31f, 32f);
    }

    [Fact]
    public void Dispose_WithNoInflightInference_CompletesImmediately()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lm_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var predictor = new SmallLMPredictor(tempDir, modelSize: "135M", preferGpu: false,
                NullLogger<SmallLMPredictor>.Instance, tokenizer: null);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            predictor.Dispose();
            sw.Stop();
            Check.That(sw.ElapsedMilliseconds).IsStrictlyLessThan(1000);
        }
        finally { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
    }
}
