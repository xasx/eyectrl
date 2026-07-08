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
/// CURSOR-ADVANCE + LARGE BATCH (why a plain replay doesn't work). FatigueMonitor.OnSample is
/// cheap (~400 ns/sample at a 300-sample window, ~1.8 us at 1800). Two constraints are in tension:
///
/// (1) BDN's per-iteration overhead (~2 ms pilot/JIT/GC) dwarfs a small batch, so the O(window)
/// signal vanishes — empirically the scaling even inverts. The batch must be large enough (here
/// 100 000 samples → ~40–180 ms/iteration) to clear BDN's minimum-iteration-time threshold.
///
/// (2) FatigueMonitor evicts via Peek().ts &lt; currentTs - windowMicros. A fixed-ts replay against
/// a persistent monitor makes iteration 2+ (currentTs jumping backward) evict nothing and grow
/// the window unboundedly. invocationCount:1 alone prevents multi-invocation stacking but a
/// single large batch still replays the same timestamps each iteration.
///
/// The cursor-advance design resolves both: the [Benchmark] method advances a CURSOR through one
/// long monotonic stream (no replay), so timestamps always increase, the window stays bounded
/// (verified: queue holds exactly winSamples), and the 100 000-sample batch is large enough to
/// measure. [IterationSetup] rebuilds + pre-fills the monitor and resets the cursor so each
/// iteration opens on a full, evicting window. Reported ns/op is per-100000-samples; divide by
/// SamplesPerBatch for per-sample. (GazeDwellBenchmarks avoids this entirely because
/// DwellDetector's per-sample work is O(targets) over a FIXED set — replay doesn't grow the work.)
/// </remarks>
[SimpleJob(RuntimeMoniker.Net10_0, invocationCount: 1)]
[MemoryDiagnoser]
[RankColumn]
public class GazeFatigueMonitorBenchmarks
{
    private const int SamplesPerSec = 30;
    private const int SamplesPerBatch = 100_000;   // large enough to clear BDN's ~100ms threshold

    [Params(10, 30, 60)]   // seconds → ~300 / 900 / 1800 samples in-window at 30 Hz
    public int WindowSeconds { get; set; }

    // One long monotonic stream per scenario; cursor advances through it per iteration.
    private GazePoint[] _fixation = null!;
    private GazePoint[] _saccade = null!;
    private GazePoint[] _noisyFixation = null!;
    private int _winSamples;

    // Fresh per iteration — rebuilt + pre-filled so each iteration opens on a full, evicting
    // window and the cursor restarts at winSamples.
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

    [IterationSetup(Target = nameof(Fatigue_Fixation_Win))]
    public void SetupFixation()
    {
        _fatigueFixation = BuildPrefilled(_fixation);
        _cursorFixation = _winSamples;
    }

    [IterationSetup(Target = nameof(Fatigue_Saccade_Win))]
    public void SetupSaccade()
    {
        _fatigueSaccade = BuildPrefilled(_saccade);
        _cursorSaccade = _winSamples;
    }

    [IterationSetup(Target = nameof(Fatigue_NoisyFixation_Win))]
    public void SetupNoisyFixation()
    {
        _fatigueNoisyFixation = BuildPrefilled(_noisyFixation);
        _cursorNoisyFixation = _winSamples;
    }

    // Advances the cursor through SamplesPerBatch samples — no replay, so timestamps stay
    // monotonic and the window stays bounded. ns/op is per-SamplesPerBatch; /SamplesPerBatch = per-sample.
    [Benchmark]
    public void Fatigue_Fixation_Win()
    {
        var mon = _fatigueFixation;
        var s = _fixation;
        for (int k = 0; k < SamplesPerBatch; k++)
            mon.OnSample(s[_cursorFixation++]);
    }

    [Benchmark]
    public void Fatigue_Saccade_Win()
    {
        var mon = _fatigueSaccade;
        var s = _saccade;
        for (int k = 0; k < SamplesPerBatch; k++)
            mon.OnSample(s[_cursorSaccade++]);
    }

    [Benchmark]
    public void Fatigue_NoisyFixation_Win()
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
