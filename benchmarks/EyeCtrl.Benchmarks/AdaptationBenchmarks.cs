using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

namespace EyeCtrl.Benchmarks;

/// <summary>
/// Layer 3 — the struggle-adaptation decision path: <see cref="StruggleMonitor.Assess"/> (the seven §13.2
/// scorers over a filled evidence window) and <see cref="AdaptationController.Tick"/>. Both run on the
/// dispatcher thread at the coordinator's 4 Hz cadence — the same thread that services dwell updates and
/// cursor rendering — so the budget per tick is a slice of 250 ms, and <c>[MemoryDiagnoser]</c> is here to
/// keep an eye on per-tick garbage rather than raw time (a Gen0 collection suspends the gaze thread too).
/// </summary>
/// <remarks>
/// Assess is idempotent for a fixed <c>now</c> — it neither mutates the sink's ring nor advances any
/// boundary — so it tolerates BDN's many-invocation convergence and needs no cursor-advance discipline
/// (unlike GazePipelineWithFatigueBenchmarks). The controller methods are the exception and say why
/// individually.
///
/// Window fill: FullWindow saturates the sink's 256-outcome ring, the worst case for the two scorers with
/// nested loops (ScoreAbandonment's retry scan, ScoreAccidental's re-approach scan). Both break out of the
/// inner loop once the timestamp gap exceeds their window, so the realistic cost sits nearer
/// TypicalWindow — hence both are reported.
/// </remarks>
[SimpleJob(RuntimeMoniker.Net10_0)]
[MemoryDiagnoser]
[RankColumn]
public class AdaptationBenchmarks
{
    // Anchor `now` well above zero: real tracker timestamps carry a large arbitrary epoch, and evidence is
    // laid out BEHIND now so the whole 60 s evidence window is populated.
    private const long Now = 1_000_000_000L;
    private const long WindowMicroseconds = 60_000_000L;

    private StruggleMonitor _fullMonitor = null!;
    private StruggleMonitor _typicalMonitor = null!;

    private AdaptationController _idleController = null!;
    private AdaptationController _saturatedController = null!;
    private AdaptationController _rampController = null!;
    private EffectiveSettingsProvider _rampProvider = null!;

    private StruggleAssessment _gatedStruggle = null!;

    [GlobalSetup]
    public void Setup()
    {
        _fullMonitor = BuildMonitor(outcomes: 256, summaries: 256);
        _typicalMonitor = BuildMonitor(outcomes: 32, summaries: 120);

        // Every knob's feeding mode gated and scoring above the Conservative RelaxThreshold (0.50), so all
        // four UpdateKnob paths do real work: SustainFailure feeds tolerance/dwell/smoothing,
        // AccidentalActivation drives the quarantined dwell tightening, Aiming + PoorTracking feed geometry.
        _gatedStruggle = new StruggleAssessment(
            new[]
            {
                new FailureModeAssessment(FailureMode.SustainFailure, 0.80, 1.0, true, null),
                new FailureModeAssessment(FailureMode.AccidentalActivation, 0.75, 1.0, true, null),
                new FailureModeAssessment(FailureMode.Aiming, 0.70, 1.0, true, null),
                new FailureModeAssessment(FailureMode.PoorTracking, 0.65, 1.0, true, null),
            },
            aggregateLevel: 0.80);

        _idleController = BuildController(out _);
        _saturatedController = BuildController(out _);
        _rampController = BuildController(out _rampProvider);

        // Drive the saturated controller to fill 1.0 on every knob, so its per-tick cost is the steady-state
        // "struggle persists, nothing left to relax" path — no SetMomentaryFill, no Changed fan-out.
        long ts = Now;
        for (int i = 0; i < 40; i++, ts += 250_000)
            _saturatedController.Tick(_gatedStruggle, ts);
    }

    /// <summary>Assess over a saturated 256-outcome ring — the worst-case scorer input.</summary>
    /// <returns>The assessment, returned so nothing is dead-code-eliminated.</returns>
    [Benchmark]
    public StruggleAssessment Monitor_Assess_FullWindow() => _fullMonitor.Assess(Now);

    /// <summary>Assess over a realistic window: ~30 interactions and 60 s of 2 Hz quality summaries.</summary>
    /// <returns>The assessment, returned so nothing is dead-code-eliminated.</returns>
    [Benchmark(Baseline = true)]
    public StruggleAssessment Monitor_Assess_TypicalWindow() => _typicalMonitor.Assess(Now);

    /// <summary>
    /// The overwhelmingly common tick: no struggle. Every knob is already at baseline, so Step's recover
    /// branch clamps at 0 and no fill changes — nothing is pushed to the provider.
    /// </summary>
    [Benchmark]
    public void Controller_Tick_NoStruggle() => _idleController.Tick(StruggleAssessment.None, Now);

    /// <summary>
    /// Gated struggle with every fill already at the band edge: the controller holds, so this is the
    /// steady-state cost of a tick during a sustained struggle episode. Idempotent for a fixed timestamp
    /// (relaxSince is already set and the fills cannot move further), so many invocations are safe.
    /// </summary>
    [Benchmark]
    public void Controller_Tick_GatedStruggle_Saturated() => _saturatedController.Tick(_gatedStruggle, Now);

    /// <summary>
    /// One complete Conservative relax ramp — the stepping path, where every tick moves a fill by 0.10 and
    /// pushes it through <see cref="EffectiveSettingsProvider.SetMomentaryFill"/> (raising <c>Changed</c>).
    /// The whole ramp is the measured op rather than a single tick, because a tick only steps while the fill
    /// is below the band edge: <see cref="AdaptationController.Reset"/> re-arms at the start, then a 3 s
    /// sustain hold, then the ten stepping ticks. This is the sequence that used to rebuild the
    /// FixationDetector ten times; it now writes the radius in place.
    /// </summary>
    [Benchmark]
    public void Controller_RelaxRamp_TenSteps()
    {
        _rampController.Reset();
        long ts = Now;
        _rampController.Tick(_gatedStruggle, ts); // arms relaxSince
        ts += 3_000_000;                          // Conservative SustainSeconds
        for (int i = 0; i < 10; i++, ts += 250_000)
            _rampController.Tick(_gatedStruggle, ts);
    }

    // Fills a sink with `outcomes` dwell outcomes and `summaries` gaze-quality summaries laid out behind
    // Now, then wraps it in a monitor. Reported in ascending timestamp order, as the production surfaces do
    // (the sink's monotonic guard drops out-of-order reports).
    private static StruggleMonitor BuildMonitor(int outcomes, int summaries)
    {
        var sink = new BufferingTelemetrySink();

        // Spread outcomes across the evidence window and across contexts, so the drift scorer's per-context
        // grouping and the accidental scorer's same-context scan both have work to do.
        var contexts = new[] { DwellContext.Keyboard, DwellContext.Toolbar, DwellContext.ActionMenu, DwellContext.Other };
        long outcomeStep = WindowMicroseconds / Math.Max(1, outcomes);
        for (int i = 0; i < outcomes; i++)
        {
            long ts = Now - WindowMicroseconds + (i + 1) * outcomeStep;
            var context = contexts[i % contexts.Length];
            bool activated = i % 3 == 0;
            // A coherent +X near-miss bias so ScoreDrift's cone clustering runs its full O(k²) pass rather
            // than bailing on zero-magnitude vectors.
            var center = new PhysicalPoint(600 + i % 7 * 25, 400 + i % 5 * 25);
            var gaze = new PhysicalPoint(center.X + 30, center.Y + 6);
            sink.Report(new DwellOutcome(
                ts, context, $"target-{i % 11}",
                activated ? OutcomeKind.Activated : OutcomeKind.Abandoned,
                activated ? AbandonReason.None : AbandonReason.GazeLeft,
                activated ? 1.0 : 0.55 + i % 4 * 0.1,
                gaze, center, 45.0, null));
        }

        // 500 ms summaries (the GazeQualityAggregator's default cadence), newest at Now.
        for (int i = summaries - 1; i >= 0; i--)
        {
            long ts = Now - i * 500_000L;
            sink.ReportGazeQuality(new GazeQualitySummary(ts, 0.12, 90.0, 0.4, 15));
        }

        var eyeCare = new EyeCtrlSettings().EyeCare;
        eyeCare.FatigueMonitorEnabled = true; // worst case: the fatigue amplifier scan also runs
        return new StruggleMonitor(sink, eyeCare, backspaceTargetId: "backspace");
    }

    private static AdaptationController BuildController(out EffectiveSettingsProvider provider)
    {
        var settings = new EyeCtrlSettings();
        settings.Adaptation.Enabled = true; // a disabled controller Resets and returns — nothing to measure
        provider = new EffectiveSettingsProvider(settings.Adaptation);
        return new AdaptationController(provider, settings.Adaptation);
    }
}
