using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using EyeCtrl.Core.Prediction;

namespace EyeCtrl.Benchmarks;

[SimpleJob(RuntimeMoniker.Net10_0)]
[MemoryDiagnoser]
[RankColumn]
public class OrchestratorBenchmarks
{
    private PredictionOrchestrator _orchestrator = null!;
    private PredictionOrchestrator _orchestratorWithTrie = null!;

    [GlobalSetup]
    public void Setup()
    {
        var words = new Dictionary<string, int>();
        for (int i = 0; i < 10000; i++)
        {
            var word = $"word{i}";
            words[word] = 10000 - i;
        }
        // Ensure "hel" prefix has matches
        words["hello"] = 500;
        words["help"] = 400;
        words["held"] = 300;

        var predictor = new WordPredictor(new InMemoryDictionaryProvider(words));
        // Pre-load bigrams for "hello" context — learn 2x to survive MinFrequency=2 filter
        foreach (var seq in new[] { new[] { "hello", "world" }, new[] { "hello", "there" }, new[] { "hello", "friend" } })
        {
            predictor.LearnSequence(seq);
            predictor.LearnSequence(seq);
        }

        _orchestrator = new PredictionOrchestrator(predictor, sentenceIndex: null, lmPredictor: null, mlEnabled: false);

        // Orchestrator with SentenceCompletionIndex
        var triePredictor = new WordPredictor(new InMemoryDictionaryProvider(words));
        var trieIndex = new SentenceCompletionIndex();
        for (int i = 0; i < 500; i++)
        {
            trieIndex.Insert($"How do I turn this on {i}", i + 1);
        }
        _orchestratorWithTrie = new PredictionOrchestrator(triePredictor, sentenceIndex: trieIndex, lmPredictor: null, mlEnabled: false);
    }

    [Benchmark]
    public List<PredictionResult> GetSuggestions_WordOnly()
    {
        return _orchestrator.GetSuggestions("hel", null, 5);
    }

    [Benchmark]
    public List<PredictionResult> GetSuggestions_WithContext()
    {
        return _orchestrator.GetSuggestions("", "hello", 5);
    }

    [Benchmark]
    public List<PredictionResult> GetSuggestions_WithSentenceIndex()
    {
        return _orchestratorWithTrie.GetSuggestions("How do I", null, 5);
    }
}
