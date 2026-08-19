namespace EyeCtrl.LM.Tests;

using System.Text;
using EyeCtrl.LM;
using Microsoft.Extensions.Logging;

/// <summary>
/// End-to-end inference against every real downloaded export. Skips when none is present,
/// so CI and clean checkouts stay green; on a machine that has downloaded one through the
/// Models settings page, this is the only test that proves the feature actually predicts.
/// Unit tests cannot cover it: the failure mode being guarded is a mismatch between what the
/// predictor feeds and what the ONNX graph declares, which only a real graph can reveal.
/// </summary>
public class SmallLMPredictorModelTests
{
    [Fact]
    public void Predict_WithEveryRealDownloadedModel_ProducesASuggestion()
    {
        // Every model present, not just the first found. The shipped lineup splits on KV cache
        // element type - the fp16 exports declare Float16 while the q4f16 one declares Float -
        // so stopping at the first match can leave a whole branch of the feed unexercised.
        var models = FindLocalModels();
        Assert.SkipWhen(models.Count == 0, "No downloaded ONNX model found in %APPDATA%/EyeCtrl/models.");

        foreach (var (modelPath, tokenizerPath) in models)
            AssertProducesASuggestion(modelPath, tokenizerPath);
    }

    private static void AssertProducesASuggestion(string modelPath, string tokenizerPath)
    {
        var name = Path.GetFileName(modelPath);
        var log = new CapturingLogger();
        using var tokenizer = new LMTokenizer(tokenizerPath);
        using var predictor = new SmallLMPredictor(
            Path.GetDirectoryName(modelPath)!, modelSize: Path.GetFileNameWithoutExtension(modelPath),
            preferGpu: false, logger: log, tokenizer: tokenizer, modelPath: modelPath, ownsTokenizer: false);

        Check.WithCustomMessage($"{name} did not load. Log:{Environment.NewLine}{log}")
            .That(predictor.IsAvailable).IsTrue();

        predictor.Predict("The weather today is");

        // The session is already loaded by the constructor, so this budget covers generation only.
        // A logged error ends the wait immediately - a broken feed must fail fast and legibly.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        IReadOnlyList<Core.Prediction.PredictionResult>? results = null;
        while (DateTime.UtcNow < deadline && !log.HasError && (results = predictor.TryGetResults()) == null)
            Thread.Sleep(50);

        Check.WithCustomMessage($"{name} produced no prediction. Log:{Environment.NewLine}{log}")
            .That(results).IsNotNull();
        var suggestions = results!;
        Check.WithCustomMessage($"{name} returned an empty suggestion set").That(suggestions).Not.IsEmpty();
        Check.That(suggestions[0].Word).Not.IsEmpty();
        Check.That(suggestions[0].Word).Not.Contains(" ");
        Check.That(suggestions[0].Confidence).IsStrictlyGreaterThan(0.0);

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{name} predicted \"{suggestions[0].Word}\" (confidence {suggestions[0].Confidence:F3})");
    }

    private static List<(string ModelPath, string TokenizerPath)> FindLocalModels()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EyeCtrl", "models");
        if (!Directory.Exists(dir)) return [];

        // ModelRegistryService pairs "<name>.onnx" with "<name>.tokenizer.json".
        var found = new List<(string, string)>();
        foreach (var onnx in Directory.EnumerateFiles(dir, "*.onnx"))
        {
            var tokenizer = onnx.Replace(".onnx", ".tokenizer.json", StringComparison.Ordinal);
            if (File.Exists(tokenizer)) found.Add((onnx, tokenizer));
        }
        return found;
    }

    private sealed class CapturingLogger : ILogger<SmallLMPredictor>
    {
        private readonly StringBuilder _entries = new();
        private volatile bool _hasError;

        public bool HasError => _hasError;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
                _entries.AppendLine($"[{logLevel}] {formatter(state, exception)} {exception}");
            if (logLevel >= LogLevel.Error) _hasError = true;
        }

        public override string ToString()
        {
            lock (_entries) return _entries.ToString();
        }
    }
}
