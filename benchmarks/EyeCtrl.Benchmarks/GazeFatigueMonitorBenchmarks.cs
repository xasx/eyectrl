using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.EyeCare;

namespace EyeCtrl.Benchmarks;

/// <summary>
/// Layer 5 — FatigueMonitor O(window) scan, params-scaled. The fatigue monitor re-scans the
/// entire rolling window every sample (the foreach over _samples in OnSample). This class
/// characterizes how that cost scales with the window size, mirroring Layer 3's
/// (GazeDwellBenchmarks) [Params(5,20,50)] target-count sweep.
/// </summary>
/// <remarks>
/// PER-ITERATION PRE-FILL + invocationCount:1 (BOTH REQUIRED). BenchmarkDotNet runs a
/// [Benchmark] method many invocations per iteration on the SAME instance. FatigueMonitor
/// evicts via Peek().ts &lt; currentTs - windowMicros. If the method replays a fixed-ts array
/// against a persistent monitor, invocation 2+ jumps currentTs BACKWARD, cutoff goes below
/// the queue front, nothing evicts, and the window grows ~1000/invocation — producing
/// non-reproducible numbers, runaway O(queue) cost, and Queue&lt;T&gt; backing-array doublings
/// (non-zero B/op). The fix: build + pre-fill a FRESH monitor per iteration in
/// [IterationSetup], and pin invocationCount:1 so each iteration times exactly ONE clean
/// 1000-sample monotonic replay over a full, evicting window. (GazeDwellBenchmarks avoids
/// this because DwellDetector's per-sample work is O(targets) over a FIXED set — non-monotonic
/// timestamps don't grow the work. FatigueMonitor's work is O(queue), and the queue is what
/// breaks under replay.)
///
/// TIMESTAMP CONTINUITY (load-bearing): the pre-fill and timed tail are slices of ONE
/// continuous GenerateFixation(length: ...) stream. Two independent calls (timestamps
/// restarting at 0) would make cutoff go negative, evict nothing, and silently measure the
/// growth phase.
/// </remarks>
[SimpleJob(RuntimeMoniker.Net10_0, invocationCount: 1)]
[MemoryDiagnoser]
[RankColumn]
public class GazeFatigueMonitorBenchmarks
{
    private const int SamplesPerSec = 30;
    private const int SteadyTail = 1000;

    [Params(10, 30, 60)]
    public int WindowSeconds { get; set; }

    // Immutable streams — built once, reused across iterations (never mutated by OnSample).
    private GazePoint[] _fixation = null!;
    private GazePoint[] _saccade = null!;
    private GazePoint[] _noisyFixation = null!;

    // Full continuous streams + prefix length, stashed for IterationSetup to rebuild monitors.
    private GazePoint[] _fixationFull = null!;
    private GazePoint[] _saccadeFull = null!;
    private GazePoint[] _noisyFixationFull = null!;
    private int _samplesToFill;

    // Fresh per iteration — see the class remarks.
    private FatigueMonitor _fatigueFixation = null!;
    private FatigueMonitor _fatigueSaccade = null!;
    private FatigueMonitor _fatigueNoisyFixation = null!;

    // Streams only — cheap, immutable, safe to build once per [Params] value.
    [GlobalSetup]
    public void Setup()
    {
        int samplesToFill = WindowSeconds * SamplesPerSec + 2;
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

    // Fresh monitor + pre-fill EVERY iteration — invocationCount:1 means each iteration is one
    // timed replay, so this runs once per measured op. Untimed by BDN. TARGETED per benchmark so
    // only the needed monitor is built (building all 3 per iteration created GC pressure that,
    // with invocationCount:1's high iteration count, distorted the small per-sample costs).
    [IterationSetup(Target = nameof(Fatigue_Fixation_Win))]
    public void SetupFixation() => _fatigueFixation = BuildPrefilled(_fixationFull, _samplesToFill);

    [IterationSetup(Target = nameof(Fatigue_Saccade_Win))]
    public void SetupSaccade() => _fatigueSaccade = BuildPrefilled(_saccadeFull, _samplesToFill);

    [IterationSetup(Target = nameof(Fatigue_NoisyFixation_Win))]
    public void SetupNoisyFixation() => _fatigueNoisyFixation = BuildPrefilled(_noisyFixationFull, _samplesToFill);

    [Benchmark]
    public void Fatigue_Fixation_Win() => Replay(_fixation, _fatigueFixation);

    [Benchmark]
    public void Fatigue_Saccade_Win() => Replay(_saccade, _fatigueSaccade);

    [Benchmark]
    public void Fatigue_NoisyFixation_Win() => Replay(_noisyFixation, _fatigueNoisyFixation);

    private static void Replay(GazePoint[] stream, FatigueMonitor monitor)
    {
        for (int i = 0; i < stream.Length; i++)
            monitor.OnSample(stream[i]);
    }

    private FatigueMonitor BuildPrefilled(GazePoint[] full, int samplesToFill)
    {
        var settings = new EyeCareSettings { Enabled = true, FatigueMonitorEnabled = true };
        var monitor = new FatigueMonitor(settings, fixationRadiusPixels: 50, WindowSeconds);
        for (int i = 0; i < samplesToFill; i++)
            monitor.OnSample(full[i]);
        return monitor;
    }
}
