using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using EyeCtrl.Core.Prediction;

namespace EyeCtrl.Benchmarks;

/// <summary>
/// Phase 8: Performance benchmarks for prediction latency and decay operations.
/// Target: GetSuggestions should complete in < 10ms (99th percentile).
/// </summary>
[SimpleJob(RuntimeMoniker.Net90)]
[MemoryDiagnoser]
[RankColumn]
public class PredictionBenchmarks
{
    private WordPredictor _predictor = null!;
    private WordPredictor _predictorWithBigrams = null!;
    private WordPredictor _predictorWithManyBigrams = null!;
    private Dictionary<string, int> _largeWordList = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Setup predictor with 10K word dictionary
        var words = GenerateLargeWordList(10000);
        _predictor = new WordPredictor(new InMemoryDictionaryProvider(words));

        // Setup predictor with some bigrams
        _predictorWithBigrams = new WordPredictor(new InMemoryDictionaryProvider(words));
        _predictorWithBigrams.LearnSequence(new[] { "hello", "world" });
        _predictorWithBigrams.LearnSequence(new[] { "hello", "there" });
        _predictorWithBigrams.LearnSequence(new[] { "hello", "friend" });

        // Store large word list for iteration setup
        _largeWordList = words;
    }

    [IterationSetup(Target = nameof(ApplyDecay_WithLargeDataset))]
    public void IterationSetup()
    {
        // Rebuild predictor with many bigrams for each iteration to ensure consistent baseline
        _predictorWithManyBigrams = new WordPredictor(new InMemoryDictionaryProvider(_largeWordList));
        for (int i = 0; i < 1000; i++)
        {
            _predictorWithManyBigrams.LearnSequence(new[] { $"word{i % 100}", $"next{i % 50}", $"after{i % 25}" });
        }
    }

    [Benchmark]
    public List<PredictionResult> GetSuggestions_EmptyPrefix()
    {
        return _predictor.GetSuggestions("", context: null, maxCount: 5);
    }

    [Benchmark]
    public List<PredictionResult> GetSuggestions_WithPrefix()
    {
        return _predictor.GetSuggestions("hel", context: null, maxCount: 5);
    }

    [Benchmark]
    public List<PredictionResult> GetSuggestions_WithContext()
    {
        return _predictorWithBigrams.GetSuggestions("", context: "hello", maxCount: 5);
    }

    [Benchmark]
    public List<PredictionResult> GetSuggestions_FuzzyMatching()
    {
        return _predictor.GetSuggestions("hllo", context: null, maxCount: 5);
    }

    [Benchmark]
    public void ApplyDecay_WithLargeDataset()
    {
        _predictorWithManyBigrams.ApplyDecay(decayEnabled: true, halfLifeDays: 30);
    }

    private static Dictionary<string, int> GenerateLargeWordList(int count)
    {
        var words = new Dictionary<string, int>();

        // Add common prefixes to make fuzzy matching more realistic
        var prefixes = new[] { "hel", "wor", "tes", "app", "com", "pro", "dev", "use", "sys", "dat" };
        var suffixes = new[] { "lo", "ld", "t", "le", "mon", "gram", "elop", "r", "tem", "a" };

        for (int i = 0; i < count; i++)
        {
            var prefix = prefixes[i % prefixes.Length];
            var suffix = suffixes[(i / prefixes.Length) % suffixes.Length];
            var word = $"{prefix}{suffix}{i}";
            words[word] = count - i; // Descending frequency
        }

        // Add some realistic words
        var commonWords = new[] { "hello", "world", "test", "application", "program", "computer", "system", "data" };
        foreach (var word in commonWords)
        {
            words[word] = 1000;
        }

        return words;
    }
}

// Helper class for in-memory dictionary
public class InMemoryDictionaryProvider : IDictionaryProvider
{
    private readonly Dictionary<string, int> _words;
    public InMemoryDictionaryProvider(Dictionary<string, int> words) => _words = words;
    public Dictionary<string, int> Load() => new(_words);
    public void Save(Dictionary<string, int> words) { }
}
