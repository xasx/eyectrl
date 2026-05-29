using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using EyeCtrl.Core.Prediction;

namespace EyeCtrl.Benchmarks;

[SimpleJob(RuntimeMoniker.Net10_0)]
[MemoryDiagnoser]
[RankColumn]
public class BigramPhraseBenchmarks
{
    private BigramModel _bigramModel = null!;
    private PhraseModel _phraseModel = null!;

    [GlobalSetup]
    public void Setup()
    {
        _bigramModel = BuildBigramModel();
        _phraseModel = BuildPhraseModel();
    }

    [IterationSetup(Target = nameof(Bigram_ApplyDecay_Large))]
    public void BigramDecaySetup()
    {
        _bigramModel = BuildBigramModel();
    }

    [IterationSetup(Target = nameof(Phrase_ApplyDecay_Large))]
    public void PhraseDecaySetup()
    {
        _phraseModel = BuildPhraseModel();
    }

    [Benchmark]
    public List<(string word, int frequency)> Bigram_GetNextWords()
    {
        return _bigramModel.GetNextWords("hello", 5);
    }

    [Benchmark]
    public void Bigram_ApplyDecay_Large()
    {
        _bigramModel.ApplyDecay(true, 30);
    }

    [Benchmark]
    public List<(string phrase, int frequency)> Phrase_GetPhrases()
    {
        return _phraseModel.GetPhrases("hello there", 5);
    }

    [Benchmark]
    public void Phrase_ApplyDecay_Large()
    {
        _phraseModel.ApplyDecay(true, 30);
    }

    private static BigramModel BuildBigramModel()
    {
        var model = new BigramModel();

        for (int i = 0; i < 500; i++)
        {
            model.Learn($"context{i % 25}", $"target{i}");
            model.Learn($"context{i % 25}", $"target{i}");
        }
        for (int i = 0; i < 50; i++)
        {
            model.Learn("hello", $"world{i}");
            model.Learn("hello", $"world{i}");
        }
        for (int i = 0; i < 1000; i++)
        {
            model.Learn($"decay{i % 100}", $"next{i}");
            model.Learn($"decay{i % 100}", $"next{i}");
        }

        return model;
    }

    private static PhraseModel BuildPhraseModel()
    {
        var model = new PhraseModel();

        for (int i = 0; i < 1000; i++)
        {
            var phrase = new[] { $"hello{i % 5}", $"there{i % 5}", $"friend{i % 100}" };
            model.LearnPhrase(phrase);
            model.LearnPhrase(phrase);
        }
        for (int i = 0; i < 100; i++)
        {
            model.LearnPhrase(new[] { "hello", "there", $"continuation{i}" });
            model.LearnPhrase(new[] { "hello", "there", $"continuation{i}" });
        }

        return model;
    }
}
