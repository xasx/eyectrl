using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Filters;
using BenchmarkDotNet.Running;

namespace EyeCtrl.Benchmarks;

class Program
{
    static void Main(string[] args)
    {
        var config = ManualConfig.Create(DefaultConfig.Instance);

        if (!SmallLMModelAvailable())
        {
            config = config.AddFilter(new DisableClassFilter(nameof(SmallLMBenchmarks)));
        }

        BenchmarkSwitcher
            .FromAssembly(typeof(Program).Assembly)
            .Run(args, config);
    }

    private static bool SmallLMModelAvailable()
    {
        var modelDir = ModelPathHelper.GetModelDirectory();
        var (onnxFiles, tokenizerFiles) = ModelPathHelper.FindModelFiles(modelDir);
        return onnxFiles.Length > 0 && tokenizerFiles.Length > 0;
    }
}

/// <summary>
/// BenchmarkDotNet filter that excludes benchmark classes by name.
/// </summary>
public class DisableClassFilter : IFilter
{
    private readonly string _className;

    public DisableClassFilter(string className) => _className = className;

    public bool Predicate(BenchmarkCase benchmarkCase) =>
        benchmarkCase.Descriptor.Type.Name != _className;
}
