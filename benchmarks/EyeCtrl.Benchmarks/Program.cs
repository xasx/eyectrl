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
            config = config.AddFilter(new DisableClassFilter("SmallLMBenchmarks"));
        }

        BenchmarkSwitcher
            .FromAssembly(typeof(Program).Assembly)
            .Run(args, config);
    }

    private static bool SmallLMModelAvailable()
    {
        var envDir = Environment.GetEnvironmentVariable("EYECTRL_MODEL_DIR");
        var modelDir = envDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "EyeCtrl", "models");

        if (!Directory.Exists(modelDir))
            return false;

        var onnxFiles = Directory.GetFiles(modelDir, "*.onnx", SearchOption.TopDirectoryOnly);
        var tokenizerFiles = Directory.GetFiles(modelDir, "*.tokenizer.json", SearchOption.TopDirectoryOnly);

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
