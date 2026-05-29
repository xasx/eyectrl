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
        _bigramModel = new BigramModel();

        // 500 distinct word pairs, each learned 2x to survive MinFrequency=2 filter
        for (int i = 0; i < 500; i++)
        {
            var word1 = $"context{i % 25}";
            var word2 = $"target{i}";
            _bigramModel.Learn(word1, word2);
            _bigramModel.Learn(word1, word2);
        }
        // Ensure "hello" context has entries
        for (int i = 0; i < 50; i++)
        {
            _bigramModel.Learn("hello", $"world{i}");
            _bigramModel.Learn("hello", $"world{i}");
        }

        // Large dataset for ApplyDecay: 2000 bigrams
        for (int i = 0; i < 1000; i++)
        {
            _bigramModel.Learn($"decay{i % 100}", $"next{i}");
            _bigramModel.Learn($"decay{i % 100}", $"next{i}");
        }

        // PhraseModel: learn 1500 three-word phrases (1000 sequences, each 2x)
        _phraseModel = new PhraseModel();
        for (int i = 0; i < 1000; i++)
        {
            var phrase = new[] { $"hello{i % 5}", $"there{i % 5}", $"friend{i % 100}" };
            _phraseModel.LearnPhrase(phrase);
            _phraseModel.LearnPhrase(phrase);
        }
        // Ensure "hello there" prefix has entries
        for (int i = 0; i < 100; i++)
        {
            _phraseModel.LearnPhrase(new[] { "hello", "there", $"continuation{i}" });
            _phraseModel.LearnPhrase(new[] { "hello", "there", $"continuation{i}" });
        }
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
}
