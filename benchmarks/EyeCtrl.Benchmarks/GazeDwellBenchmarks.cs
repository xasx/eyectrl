using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

namespace EyeCtrl.Benchmarks;

[SimpleJob(RuntimeMoniker.Net10_0)]
[MemoryDiagnoser]
[RankColumn]
public class GazeDwellBenchmarks
{
    [Params(5, 20, 50)]
    public int TargetCount { get; set; }

    private DwellDetector _detector = null!;
    private PhysicalPoint _gazeInsideNearest = new(960, 540);

    [GlobalSetup]
    public void Setup()
    {
        _detector = new DwellDetector(cooldownMs: 0);
        int cols = (int)Math.Ceiling(Math.Sqrt(TargetCount));
        double spacing = 1920.0 / (cols + 1);
        int placed = 0;
        for (int row = 0; placed < TargetCount; row++)
        {
            for (int col = 0; col < cols && placed < TargetCount; col++)
            {
                double cx = spacing * (col + 1);
                double cy = spacing * (row + 1);
                if (placed == 0)
                {
                    cx = 960.0;
                    cy = 540.0;
                }
                _detector.AddTarget(new DwellTarget($"t{placed}", new PhysicalPoint(cx, cy), 60.0, dwellTimeMs: 1000));
                placed++;
            }
        }
    }

    [Benchmark]
    public void Update()
    {
        for (int i = 0; i < GazeStreamGenerator.SamplesPerBatch; i++)
            _detector.Update(_gazeInsideNearest, (long)i * GazeStreamGenerator.SamplePeriodMicros);
    }
}
