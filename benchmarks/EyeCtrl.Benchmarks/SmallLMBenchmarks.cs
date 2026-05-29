using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using EyeCtrl.Core.Prediction;

namespace EyeCtrl.Benchmarks;

[SimpleJob(RuntimeMoniker.Net10_0)]
[MemoryDiagnoser]
[RankColumn]
public class SmallLMBenchmarks
{
    private SmallLMPredictor _predictor = null!;
    private LMTokenizer _tokenizer = null!;
    private long[] _pretokenized = null!;
    private bool _available;

    [GlobalSetup]
    public void Setup()
    {
        var envDir = Environment.GetEnvironmentVariable("EYECTRL_MODEL_DIR");
        var modelDir = envDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "EyeCtrl", "models");

        var onnxFiles = Directory.Exists(modelDir)
            ? Directory.GetFiles(modelDir, "*.onnx", SearchOption.TopDirectoryOnly)
            : [];

        var tokenizerFiles = Directory.Exists(modelDir)
            ? Directory.GetFiles(modelDir, "*.tokenizer.json", SearchOption.TopDirectoryOnly)
            : [];

        _available = onnxFiles.Length > 0 && tokenizerFiles.Length > 0;

        if (!_available)
        {
            return;
        }

        var tokenizerPath = tokenizerFiles[0];
        _tokenizer = new LMTokenizer(tokenizerPath);
        _pretokenized = _tokenizer.Encode("The quick brown fox jumps over the lazy dog");

        var modelPath = onnxFiles[0];

        _predictor = new SmallLMPredictor(
            modelsDirectory: modelDir,
            modelSize: "135M",     // any valid ModelConstants key or non-null/non-"Off" clears the guard
            preferGpu: false,
            logger: null,
            tokenizer: _tokenizer,
            modelPath: modelPath);   // explicit path overrides ModelConstants resolution
    }

    [Benchmark]
    public long[] Tokenizer_Encode()
    {
        if (!_available) return [];
        return _tokenizer.Encode("The quick brown fox jumps over the lazy dog");
    }

    [Benchmark]
    public string Tokenizer_Decode()
    {
        if (!_available) return string.Empty;
        return _tokenizer.Decode(_pretokenized);
    }

    [Benchmark]
    public IReadOnlyList<PredictionResult>? PredictAndWait()
    {
        if (!_available || !_predictor.IsAvailable) return null;

        _predictor.Predict("The quick brown fox", null);

        // Spin-wait for inference to complete (5s timeout)
        var deadline = DateTime.UtcNow.AddSeconds(5);
        IReadOnlyList<PredictionResult>? results;
        while ((results = _predictor.TryGetResults()) == null)
        {
            if (DateTime.UtcNow > deadline)
                return null;
            Thread.Sleep(1);
        }

        return results;
    }
}
