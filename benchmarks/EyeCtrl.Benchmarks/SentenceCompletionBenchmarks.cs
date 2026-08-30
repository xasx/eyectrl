using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using EyeCtrl.Core.Prediction;

namespace EyeCtrl.Benchmarks;

[SimpleJob(RuntimeMoniker.Net10_0)]
[MemoryDiagnoser]
[RankColumn]
public class SentenceCompletionBenchmarks
{
    private string[] _sentences = null!;
    private SentenceCompletionIndex _index = null!;

    [GlobalSetup]
    public void Setup()
    {
        // 5000 synthetic sentences
        _sentences = new string[5000];
        var subjects = new[] { "the quick brown fox", "my old friend", "the developer", "a curious cat", "our team" };
        var verbs = new[] { "jumps over", "runs toward", "looks at", "works on", "thinks about" };
        var objects = new[] { "the lazy dog", "the distant hill", "the project plan", "the window sill", "the deadline" };

        for (int i = 0; i < 5000; i++)
        {
            _sentences[i] = $"{subjects[i % 5]} {verbs[(i / 5) % 5]} {objects[(i / 25) % 5]}";
        }

        // Pre-populated index for query benchmarks
        _index = new SentenceCompletionIndex();
        for (int i = 0; i < _sentences.Length; i++)
        {
            _index.Insert(_sentences[i], (i % 100) + 1);
        }
    }

    [IterationSetup(Target = nameof(Insert_SingleSentence))]
    public void InsertSetup()
    {
        // Fresh index per invocation; IterationSetup forces UnrollFactor=1,
        // so InvocationCount=1 below is valid
        _index = new SentenceCompletionIndex();
    }

    [Benchmark]
    [InvocationCount(1)]
    public void Insert_SingleSentence()
    {
        _index.Insert("the quick brown fox jumps over the lazy dog", 10);
    }

    [Benchmark]
    public List<(string completion, int frequency)> GetCompletions_DeepMatch()
    {
        return _index.GetCompletions("the quick brown fox", 5);
    }

    [Benchmark]
    public List<(string completion, int frequency)> GetCompletions_NoMatch()
    {
        return _index.GetCompletions("xyznonexistent", 5);
    }
}
