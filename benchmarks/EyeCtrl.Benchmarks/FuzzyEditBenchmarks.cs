using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using EyeCtrl.Core.Prediction;

namespace EyeCtrl.Benchmarks;

[SimpleJob(RuntimeMoniker.Net10_0)]
[MemoryDiagnoser]
[RankColumn]
public class FuzzyEditBenchmarks
{
    private Dictionary<char, HashSet<char>> _adjacencyMap = null!;
    private readonly string _a3 = "hel";
    private readonly string _b3 = "hlo";
    private readonly string _a6 = "system";
    private readonly string _b6 = "systen";
    private readonly string _a11 = "application";
    private readonly string _b11 = "applucation";
    private readonly string _a25 = "extraordinarymeetingrooms";
    private readonly string _b25 = "extraordinerymeetingroomz";

    [GlobalSetup]
    public void Setup()
    {
        _adjacencyMap = KeyboardAdjacencyMap.For("QWERTY");
    }

    [Benchmark]
    public double Compute_3chars()
    {
        return FuzzyEditDistance.Compute(_a3, _b3, _adjacencyMap);
    }

    [Benchmark]
    public double Compute_6chars()
    {
        return FuzzyEditDistance.Compute(_a6, _b6, _adjacencyMap);
    }

    [Benchmark]
    public double Compute_11chars()
    {
        return FuzzyEditDistance.Compute(_a11, _b11, _adjacencyMap);
    }

    [Benchmark]
    public double Compute_25chars()
    {
        return FuzzyEditDistance.Compute(_a25, _b25, _adjacencyMap);
    }
}
