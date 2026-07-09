using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.EyeCare;
using EyeCtrl.EyeCare.Exercises;

namespace EyeCtrl.Benchmarks;

/// <summary>
/// Layer 4 — end-to-end pipeline benchmarks. A real GazeEngine wired to shared fakes in
/// EyeCareBenchmarkFakes (mirroring tests/EyeCtrl.Core.Tests/GazeEngineTests.cs). The provider's
/// GazePointReceived is raised synchronously once per sample inside the timed loop.
/// </summary>
/// <remarks>
/// PER-ITERATION REBUILD: each benchmark gets a fresh provider+engine pair via a targeted
/// [IterationSetup], for two reasons: (a) InteractionManager starts in FreeLook and leaves it once
/// the Fixation scenario triggers, so reusing an engine would bias later iterations; (b) the
/// GazeEngine constructor subscribes to the provider's GazePointReceived event, so reusing a
/// provider across engines would stack subscriptions. [IterationCleanup] disposes the engine and
/// the null-guarded service (the 4 originals build no service, so the `?.`/reset is a safe no-op
/// for them).
/// (Because [IterationSetup] is present, BenchmarkDotNet pins InvocationCount=1 — one invocation
/// per iteration — so each measured op is a single full replay of the stream through a fresh engine
/// that starts in FreeLook. The reported number is therefore the cost of one whole-stream replay,
/// including the initial FreeLook->Fixation transition, not a post-convergence steady-state figure.
/// It is still an apples-to-apples basis for the four original benchmarks, where filter mode is the
/// only variable.) The two eye-care-wired
/// variants (WithEyeCare_Disabled, WithEyeCare_Enabled_NoFatigue) measure the subscriber's
/// marginal cost at different early-return points, replaying _fixationTimed (continuous with the
/// pre-fill) rather than _fixation. See GazePipelineWithFatigueBenchmarks for the third eye-care
/// variant, which needs its own class due to the multi-invocation growth flaw.
/// </remarks>
[SimpleJob(RuntimeMoniker.Net10_0)]
[MemoryDiagnoser]
[RankColumn]
public class GazePipelineBenchmarks
{
    private GazePoint[] _fixation = null!;
    private GazePoint[] _noisyFixation = null!;

    private const int WindowSeconds = 60;
    private const int SamplesPerSec = 30;
    private const int SteadyTail = 1000;
    private const int PrefillSamples = WindowSeconds * SamplesPerSec + 2;   // 1802

    private GazePoint[] _fixationTimed = null!;
    private GazePoint[] _fixationPrefill = null!;
    private EyeCareService _service = null!;

    // Built fresh per iteration by the targeted IterationSetup methods below — never reused across
    // iterations, so each measured run starts in FreeLook with no stacked event subscriptions.
    private EyeCareBenchmarkFakes.FakeGazeProvider _provider = null!;
    private GazeEngine _engine = null!;

    [GlobalSetup]
    public void Setup()
    {
        _fixation = GazeStreamGenerator.GenerateFixation();
        _noisyFixation = GazeStreamGenerator.GenerateNoisyFixation();

        // One continuous stream for the eye-care variants: prefix pre-fed, tail timed.
        var fixationLong = GazeStreamGenerator.GenerateFixation(length: PrefillSamples + SteadyTail);
        _fixationPrefill = fixationLong[..PrefillSamples];
        _fixationTimed = fixationLong[PrefillSamples..];
    }

    [IterationSetup(Target = nameof(Pipeline_Direct_Fixation))]
    public void SetupDirect() => Rebuild(GazeFilterMode.Direct);

    [IterationSetup(Target = nameof(Pipeline_Smoothed_Fixation))]
    public void SetupSmoothed() => Rebuild(GazeFilterMode.Smoothed);

    [IterationSetup(Target = nameof(Pipeline_OneEuro_Fixation))]
    public void SetupOneEuroFixation() => Rebuild(GazeFilterMode.OneEuro);

    [IterationSetup(Target = nameof(Pipeline_OneEuro_NoisyFixation))]
    public void SetupOneEuroNoisyFixation() => Rebuild(GazeFilterMode.OneEuro);

    [IterationSetup(Target = nameof(Pipeline_OneEuro_Fixation_WithEyeCare_Disabled))]
    public void SetupEyeCareDisabled() => RebuildWithEyeCare(BuildDisabledSettings());

    [IterationSetup(Target = nameof(Pipeline_OneEuro_Fixation_WithEyeCare_Enabled_NoFatigue))]
    public void SetupEyeCareNoFatigue() => RebuildWithEyeCare(BuildEnabledNoFatigueSettings());

    [IterationCleanup]
    public void IterationCleanup()
    {
        _engine.Dispose();
        // Untargeted cleanup also runs for the 4 original benchmarks (which build no service),
        // so the ?. and null-reset keep this a safe no-op for them.
        _service?.Dispose();
        _service = null!;
    }

    [Benchmark]
    public void Pipeline_Direct_Fixation() => Replay(_fixation);

    [Benchmark]
    public void Pipeline_Smoothed_Fixation() => Replay(_fixation);

    [Benchmark]
    public void Pipeline_OneEuro_Fixation() => Replay(_fixation);

    [Benchmark]
    public void Pipeline_OneEuro_NoisyFixation() => Replay(_noisyFixation);

    [Benchmark]
    public void Pipeline_OneEuro_Fixation_WithEyeCare_Disabled() => Replay(_fixationTimed);

    [Benchmark]
    public void Pipeline_OneEuro_Fixation_WithEyeCare_Enabled_NoFatigue() => Replay(_fixationTimed);

    private void Replay(GazePoint[] stream)
    {
        for (int i = 0; i < stream.Length; i++)
            _provider.SimulateGaze(stream[i]);
    }

    // Builds the fresh provider+engine pair for the upcoming iteration. The engine subscribes to
    // _provider in its constructor, so SimulateGaze on _provider drives exactly this engine.
    private void Rebuild(GazeFilterMode mode)
    {
        _provider = new EyeCareBenchmarkFakes.FakeGazeProvider();
        var settings = new EyeCtrlSettings();           // SelectionMode defaults to None
        settings.Cursor.FilterMode = mode;
        _engine = new GazeEngine(_provider, new EyeCareBenchmarkFakes.FakeInputSimulator(), settings);
        _engine.Start();
    }

    private void RebuildWithEyeCare(EyeCtrlSettings settings)
    {
        _provider = new EyeCareBenchmarkFakes.FakeGazeProvider();
        settings.Cursor.FilterMode = GazeFilterMode.OneEuro;
        _engine = new GazeEngine(_provider, new EyeCareBenchmarkFakes.FakeInputSimulator(), settings);
        _service = new EyeCareService(
            settings.EyeCare,
            new ExerciseRunner(EyeCareBenchmarkFakes.ExercisePool, seed: 0),
            EyeCareBenchmarkFakes.Hooks);
        _engine.GazeSampled += sample =>
            _service.OnGazeSample(sample, isPaused: false, canInterrupt: false);
        _engine.Start();
        for (int i = 0; i < _fixationPrefill.Length; i++)
            _provider.SimulateGaze(_fixationPrefill[i]);
    }

    private static EyeCtrlSettings BuildDisabledSettings()
    {
        var s = new EyeCtrlSettings();
        s.EyeCare.Enabled = false;
        return s;
    }

    private static EyeCtrlSettings BuildEnabledNoFatigueSettings()
    {
        var s = new EyeCtrlSettings();
        s.EyeCare.Enabled = true;
        s.EyeCare.FatigueMonitorEnabled = false;
        return s;
    }
}
