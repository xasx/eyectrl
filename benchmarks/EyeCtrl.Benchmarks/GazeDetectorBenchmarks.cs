using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

namespace EyeCtrl.Benchmarks;

/// <summary>
/// Layer 2 — per-detector micro-benchmarks. Detectors take PhysicalPoint, not GazePoint, so
/// GlobalSetup projects the GazePoint streams into parallel PhysicalPoint arrays. Detectors
/// accumulate state across the batch (production-representative). Each benchmark replays 1000
/// samples per invocation; ns/op and B/op are per-1000-samples.
/// </summary>
[SimpleJob(RuntimeMoniker.Net10_0)]
[MemoryDiagnoser]
[RankColumn]
public class GazeDetectorBenchmarks
{
    private PhysicalPoint[] _fixationPoints = null!;
    private GazePoint[] _dropoutStream = null!;

    private FixationDetector _fixationDetector = null!;
    private FixationCentroidTracker _centroidTracker = null!;
    private GapInterpolator _gapInterpolator = null!;

    [GlobalSetup]
    public void Setup()
    {
        _fixationPoints = ToPhysical(GazeStreamGenerator.GenerateFixation());
        _dropoutStream = GazeStreamGenerator.GenerateWithDropout();

        // DwellSettings defaults: FixationRadiusPixels=50, FixationDetectionMs=300.
        _fixationDetector = new FixationDetector(50.0, 300);

        // DwellSettings default: VelocityThresholdPxPerSec=100.0.
        _centroidTracker = new FixationCentroidTracker(100.0);
        _centroidTracker.StartTracking();

        // GapInterpolator over a DirectFilter; MaxGapInterpolationMs=150 (CursorSettings default).
        _gapInterpolator = new GapInterpolator(new DirectFilter(), 150);
    }

    // FixationDetector.Update takes (PhysicalPoint, long timestampMicroseconds). Timestamps are
    // reconstructed from the sample index × SamplePeriodMicros (PhysicalPoint is a struct with no
    // timestamp field).
    [Benchmark]
    public void FixationDetector_Update()
    {
        for (int i = 0; i < _fixationPoints.Length; i++)
            _fixationDetector.Update(_fixationPoints[i], (long)i * GazeStreamGenerator.SamplePeriodMicros);
    }

    [Benchmark]
    public void CentroidTracker_Update()
    {
        for (int i = 0; i < _fixationPoints.Length; i++)
            _centroidTracker.Update(_fixationPoints[i], (long)i * GazeStreamGenerator.SamplePeriodMicros);
    }

    [Benchmark]
    public (PhysicalPoint Position, bool IsInGap) GapInterpolator_Process()
    {
        (PhysicalPoint Position, bool IsInGap) last = default;
        for (int i = 0; i < _dropoutStream.Length; i++)
            last = _gapInterpolator.Process(_dropoutStream[i]);
        return last;
    }

    private static PhysicalPoint[] ToPhysical(GazePoint[] stream)
    {
        var pts = new PhysicalPoint[stream.Length];
        for (int i = 0; i < stream.Length; i++)
            pts[i] = new PhysicalPoint(stream[i].X, stream[i].Y);
        return pts;
    }
}
