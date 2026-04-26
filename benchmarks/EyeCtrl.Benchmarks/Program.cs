using BenchmarkDotNet.Running;

namespace EyeCtrl.Benchmarks;

class Program
{
    static void Main(string[] args)
    {
        BenchmarkRunner.Run<PredictionBenchmarks>(args: args);
    }
}
