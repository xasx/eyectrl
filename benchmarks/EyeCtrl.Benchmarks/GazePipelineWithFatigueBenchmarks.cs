using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.EyeCare;
using EyeCtrl.EyeCare.Exercises;

namespace EyeCtrl.Benchmarks;

/// <summary>
/// Layer 4 — the full O(window) fatigue scan wired into the real GazeEngine pipeline. This is
/// the one eye-care-pipeline variant that CANNOT share GazePipelineBenchmarks: it runs the
/// FatigueMonitor scan every sample, so it needs the cursor-advance discipline to keep the
/// window bounded (see GazeFatigueMonitorBenchmarks's remarks for why replay can't surface the
/// sub-µs per-sample cost).
/// </summary>
/// <remarks>
/// Disabled / Enabled_NoFatigue live in GazePipelineBenchmarks instead — they early-return
/// before the queue, so they tolerate replay and can share the class with the 4 originals (which
/// need many invocations to converge FreeLook -> Fixation). This class uses cursor-advance +
/// invocationCount:1 + a large batch (100 000 samples) so the per-iteration time clears BDN's
/// threshold and the monotonic cursor keeps the FatigueMonitor window bounded.
/// </remarks>
[SimpleJob(RuntimeMoniker.Net10_0, invocationCount: 1)]
[MemoryDiagnoser]
[RankColumn]
public class GazePipelineWithFatigueBenchmarks
{
    private const int WindowSeconds = 60;
    private const int SamplesPerSec = 30;
    private const int SamplesPerBatch = 100_000;
    private const int PrefillSamples = WindowSeconds * SamplesPerSec + 2;

    private GazePoint[] _fixation = null!;

    private EyeCareBenchmarkFakes.FakeGazeProvider _provider = null!;
    private GazeEngine _engine = null!;
    private EyeCareService _service = null!;
    private int _cursor;

    [GlobalSetup]
    public void Setup()
    {
        int total = PrefillSamples + SamplesPerBatch + 16;
        _fixation = GazeStreamGenerator.GenerateFixation(length: total);
    }

    [IterationSetup]
    public void IterationSetup()
    {
        RebuildWithEyeCare();
        _cursor = PrefillSamples;
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _engine.Dispose();
        _service?.Dispose();
    }

    // Advances the cursor through SamplesPerBatch samples — no replay, so the timestamps fed to
    // GazeEngine (and through GazeSampled to the FatigueMonitor) stay monotonic and the window
    // stays bounded.
    [Benchmark]
    public void Pipeline_OneEuro_Fixation_WithEyeCare_WithFatigue()
    {
        var provider = _provider;
        var fixation = _fixation;
        for (int k = 0; k < SamplesPerBatch; k++)
            provider.SimulateGaze(fixation[_cursor++]);
    }

    private void RebuildWithEyeCare()
    {
        _provider = new EyeCareBenchmarkFakes.FakeGazeProvider();
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.OneEuro;
        settings.EyeCare.Enabled = true;
        settings.EyeCare.FatigueMonitorEnabled = true;
        _engine = new GazeEngine(_provider, new EyeCareBenchmarkFakes.FakeInputSimulator(), settings);
        _service = new EyeCareService(
            settings.EyeCare,
            new ExerciseRunner(EyeCareBenchmarkFakes.ExercisePool, seed: 0),
            EyeCareBenchmarkFakes.Hooks);
        _engine.GazeSampled += sample =>
            _service.OnGazeSample(sample, isPaused: false, canInterrupt: false);
        _engine.Start();
        for (int i = 0; i < PrefillSamples; i++)
            _provider.SimulateGaze(_fixation[i]);
    }
}
