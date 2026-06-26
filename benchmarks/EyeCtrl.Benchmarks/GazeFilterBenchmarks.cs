using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

namespace EyeCtrl.Benchmarks;

/// <summary>
/// Layer 1 — per-filter micro-benchmarks. Three filters (Direct, Smoothed, OneEuro) × three
/// scenarios (Fixation, Saccade, NoisyFixation) = 9 benchmarks. WithDropout is excluded: filters
/// never receive invalid samples in production (GapInterpolator only calls Filter() for valid
/// samples), so Filter_*_WithDropout would exercise dead code.
/// </summary>
/// <remarks>
/// Each benchmark replays a pre-built batch of 1000 samples per invocation. Reported ns/op and
/// B/op are therefore per-1000-samples. Filters are constructed once in GlobalSetup and accumulate
/// state across the batch (production-representative). The last result is returned to defeat
/// dead-code elimination. Each benchmark calls its concrete filter directly (no shared helper)
/// so BenchmarkDotNet's per-method dispatch and inline optimization are not obstructed.
/// </remarks>
[SimpleJob(RuntimeMoniker.Net10_0)]
[MemoryDiagnoser]
[RankColumn]
public class GazeFilterBenchmarks
{
    private GazePoint[] _fixation = null!;
    private GazePoint[] _saccade = null!;
    private GazePoint[] _noisyFixation = null!;

    private DirectFilter _direct = null!;
    private SmoothedFilter _smoothed = null!;
    private OneEuroFilter _oneEuro = null!;

    [GlobalSetup]
    public void Setup()
    {
        _fixation = GazeStreamGenerator.GenerateFixation();
        _saccade = GazeStreamGenerator.GenerateSaccade();
        _noisyFixation = GazeStreamGenerator.GenerateNoisyFixation();

        _direct = new DirectFilter();
        // SmoothedFilter has no parameterless ctor; 0.5 is the settings default.
        _smoothed = new SmoothedFilter(0.5);
        _oneEuro = new OneEuroFilter();
    }

    // --- Direct ---
    [Benchmark]
    public PhysicalPoint Filter_Direct_Fixation()
    {
        PhysicalPoint last = new(0, 0);
        for (int i = 0; i < _fixation.Length; i++)
            last = _direct.Filter(_fixation[i]);
        return last;
    }

    [Benchmark]
    public PhysicalPoint Filter_Direct_Saccade()
    {
        PhysicalPoint last = new(0, 0);
        for (int i = 0; i < _saccade.Length; i++)
            last = _direct.Filter(_saccade[i]);
        return last;
    }

    [Benchmark]
    public PhysicalPoint Filter_Direct_NoisyFixation()
    {
        PhysicalPoint last = new(0, 0);
        for (int i = 0; i < _noisyFixation.Length; i++)
            last = _direct.Filter(_noisyFixation[i]);
        return last;
    }

    // --- Smoothed ---
    [Benchmark]
    public PhysicalPoint Filter_Smoothed_Fixation()
    {
        PhysicalPoint last = new(0, 0);
        for (int i = 0; i < _fixation.Length; i++)
            last = _smoothed.Filter(_fixation[i]);
        return last;
    }

    [Benchmark]
    public PhysicalPoint Filter_Smoothed_Saccade()
    {
        PhysicalPoint last = new(0, 0);
        for (int i = 0; i < _saccade.Length; i++)
            last = _smoothed.Filter(_saccade[i]);
        return last;
    }

    [Benchmark]
    public PhysicalPoint Filter_Smoothed_NoisyFixation()
    {
        PhysicalPoint last = new(0, 0);
        for (int i = 0; i < _noisyFixation.Length; i++)
            last = _smoothed.Filter(_noisyFixation[i]);
        return last;
    }

    // --- OneEuro ---
    [Benchmark]
    public PhysicalPoint Filter_OneEuro_Fixation()
    {
        PhysicalPoint last = new(0, 0);
        for (int i = 0; i < _fixation.Length; i++)
            last = _oneEuro.Filter(_fixation[i]);
        return last;
    }

    [Benchmark]
    public PhysicalPoint Filter_OneEuro_Saccade()
    {
        PhysicalPoint last = new(0, 0);
        for (int i = 0; i < _saccade.Length; i++)
            last = _oneEuro.Filter(_saccade[i]);
        return last;
    }

    [Benchmark]
    public PhysicalPoint Filter_OneEuro_NoisyFixation()
    {
        PhysicalPoint last = new(0, 0);
        for (int i = 0; i < _noisyFixation.Length; i++)
            last = _oneEuro.Filter(_noisyFixation[i]);
        return last;
    }
}
