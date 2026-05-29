using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using EyeCtrl.Core.Prediction;

namespace EyeCtrl.Benchmarks;

[SimpleJob(RuntimeMoniker.Net10_0)]
[MemoryDiagnoser]
[RankColumn]
public class CasingBenchmarks
{
    private IReadOnlySet<string> _abbreviations = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Abbreviations include the trailing period, matching CapitalizationHelper's comparison
        _abbreviations = new HashSet<string>(StringComparer.Ordinal) { "Dr.", "Mr.", "Ms." };
    }

    [Benchmark]
    public bool ShouldCapitalize_AfterPeriod()
    {
        return CapitalizationHelper.ShouldCapitalize("The sentence ended.");
    }

    [Benchmark]
    public bool ShouldCapitalize_MidSentence()
    {
        return CapitalizationHelper.ShouldCapitalize("hello world");
    }

    [Benchmark]
    public bool ShouldCapitalize_WithAbbrev()
    {
        return CapitalizationHelper.ShouldCapitalize("I saw Dr.", _abbreviations);
    }

    [Benchmark]
    public string ApplySmartCapitalization()
    {
        return CapitalizationHelper.ApplySmartCapitalization("hello", "What is ");
    }

    [Benchmark]
    public bool CasingHelper_IsAllCaps()
    {
        return CasingHelper.IsAllCaps("HELLO WORLD");
    }
}
