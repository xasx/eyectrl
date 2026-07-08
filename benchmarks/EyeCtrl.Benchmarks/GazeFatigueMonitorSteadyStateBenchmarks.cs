using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.EyeCare;

namespace EyeCtrl.Benchmarks;

/// <summary>
/// Layer 5 — FatigueMonitor steady-state baseline at the default 60s window. Separate class
/// from GazeFatigueMonitorBenchmarks because BenchmarkDotNet applies [Params] to every
/// [Benchmark] in a class: putting these in the params class would triple-run them with
/// identical numbers and different param labels.
/// </summary>
/// <remarks>
/// Same cursor-advance + large-batch discipline as GazeFatigueMonitorBenchmarks (see that
/// class's remarks for why a plain replay can't surface the sub-µs per-sample O(window) cost).
/// </remarks>
[SimpleJob(RuntimeMoniker.Net10_0, invocationCount: 1)]
[MemoryDiagnoser]
[RankColumn]
public class GazeFatigueMonitorSteadyStateBenchmarks
{
    private const int WindowSeconds = 60;
    private const int SamplesPerSec = 30;
    private const int SamplesPerBatch = 100_000;

    private GazePoint[] _fixation = null!;
    private GazePoint[] _saccade = null!;
    private GazePoint[] _noisyFixation = null!;
    private int _winSamples;

    private FatigueMonitor _fatigueFixation = null!;
    private FatigueMonitor _fatigueSaccade = null!;
    private FatigueMonitor _fatigueNoisyFixation = null!;
    private int _cursorFixation;
    private int _cursorSaccade;
    private int _cursorNoisyFixation;

    [GlobalSetup]
    public void Setup()
    {
        _winSamples = WindowSeconds * SamplesPerSec + 2;   // +2: 33333µs/sample → 30·N < Ns
        int total = _winSamples + SamplesPerBatch + 16;

        _fixation = GazeStreamGenerator.GenerateFixation(length: total);
        _saccade = GazeStreamGenerator.GenerateSaccade(length: total);
        _noisyFixation = GazeStreamGenerator.GenerateNoisyFixation(length: total);
    }

    [IterationSetup(Target = nameof(Fatigue_Fixation))]
    public void SetupFixation()
    {
        _fatigueFixation = BuildPrefilled(_fixation);
        _cursorFixation = _winSamples;
    }

    [IterationSetup(Target = nameof(Fatigue_Saccade))]
    public void SetupSaccade()
    {
        _fatigueSaccade = BuildPrefilled(_saccade);
        _cursorSaccade = _winSamples;
    }

    [IterationSetup(Target = nameof(Fatigue_NoisyFixation))]
    public void SetupNoisyFixation()
    {
        _fatigueNoisyFixation = BuildPrefilled(_noisyFixation);
        _cursorNoisyFixation = _winSamples;
    }

    [Benchmark]
    public void Fatigue_Fixation()
    {
        var mon = _fatigueFixation;
        var s = _fixation;
        for (int k = 0; k < SamplesPerBatch; k++)
            mon.OnSample(s[_cursorFixation++]);
    }

    [Benchmark]
    public void Fatigue_Saccade()
    {
        var mon = _fatigueSaccade;
        var s = _saccade;
        for (int k = 0; k < SamplesPerBatch; k++)
            mon.OnSample(s[_cursorSaccade++]);
    }

    [Benchmark]
    public void Fatigue_NoisyFixation()
    {
        var mon = _fatigueNoisyFixation;
        var s = _noisyFixation;
        for (int k = 0; k < SamplesPerBatch; k++)
            mon.OnSample(s[_cursorNoisyFixation++]);
    }

    private FatigueMonitor BuildPrefilled(GazePoint[] stream)
    {
        var settings = new EyeCareSettings { Enabled = true, FatigueMonitorEnabled = true };
        var monitor = new FatigueMonitor(settings, fixationRadiusPixels: 50, WindowSeconds);
        for (int i = 0; i < _winSamples; i++)
            monitor.OnSample(stream[i]);
        return monitor;
    }
}
