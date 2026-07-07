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
/// Same per-iteration pre-fill + invocationCount:1 discipline as GazeFatigueMonitorBenchmarks
/// (see that class's remarks for why both are required — BDN's multi-invocation model would
/// otherwise grow the window unboundedly under fixed-ts replay).
/// </remarks>
[SimpleJob(RuntimeMoniker.Net10_0, invocationCount: 1)]
[MemoryDiagnoser]
[RankColumn]
public class GazeFatigueMonitorSteadyStateBenchmarks
{
    private const int WindowSeconds = 60;
    private const int SamplesPerSec = 30;
    private const int SteadyTail = 1000;

    // Immutable streams — built once, reused across iterations.
    private GazePoint[] _fixation = null!;
    private GazePoint[] _saccade = null!;
    private GazePoint[] _noisyFixation = null!;

    // Full continuous streams + prefix length, stashed for IterationSetup.
    private GazePoint[] _fixationFull = null!;
    private GazePoint[] _saccadeFull = null!;
    private GazePoint[] _noisyFixationFull = null!;
    private int _samplesToFill;

    // Fresh per iteration.
    private FatigueMonitor _fatigueFixation = null!;
    private FatigueMonitor _fatigueSaccade = null!;
    private FatigueMonitor _fatigueNoisyFixation = null!;

    [GlobalSetup]
    public void Setup()
    {
        int samplesToFill = WindowSeconds * SamplesPerSec + 2;   // +2: 33333µs/sample → 30·N < Ns
        int total = samplesToFill + SteadyTail;

        var fix = GazeStreamGenerator.GenerateFixation(length: total);
        var sac = GazeStreamGenerator.GenerateSaccade(length: total);
        var noi = GazeStreamGenerator.GenerateNoisyFixation(length: total);

        _fixationFull = fix;
        _saccadeFull = sac;
        _noisyFixationFull = noi;
        _samplesToFill = samplesToFill;

        _fixation = fix[^SteadyTail..];
        _saccade = sac[^SteadyTail..];
        _noisyFixation = noi[^SteadyTail..];
    }

    [IterationSetup(Target = nameof(Fatigue_Fixation))]
    public void SetupFixation() => _fatigueFixation = BuildPrefilled(_fixationFull, _samplesToFill);

    [IterationSetup(Target = nameof(Fatigue_Saccade))]
    public void SetupSaccade() => _fatigueSaccade = BuildPrefilled(_saccadeFull, _samplesToFill);

    [IterationSetup(Target = nameof(Fatigue_NoisyFixation))]
    public void SetupNoisyFixation() => _fatigueNoisyFixation = BuildPrefilled(_noisyFixationFull, _samplesToFill);

    [Benchmark]
    public void Fatigue_Fixation() => Replay(_fixation, _fatigueFixation);

    [Benchmark]
    public void Fatigue_Saccade() => Replay(_saccade, _fatigueSaccade);

    [Benchmark]
    public void Fatigue_NoisyFixation() => Replay(_noisyFixation, _fatigueNoisyFixation);

    private static void Replay(GazePoint[] stream, FatigueMonitor monitor)
    {
        for (int i = 0; i < stream.Length; i++)
            monitor.OnSample(stream[i]);
    }

    private static FatigueMonitor BuildPrefilled(GazePoint[] full, int samplesToFill)
    {
        var settings = new EyeCareSettings { Enabled = true, FatigueMonitorEnabled = true };
        var monitor = new FatigueMonitor(settings, fixationRadiusPixels: 50, WindowSeconds);
        for (int i = 0; i < samplesToFill; i++)
            monitor.OnSample(full[i]);
        return monitor;
    }
}
