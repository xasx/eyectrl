namespace EyeCtrl.Core.Tests;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>
/// §13.3 smoothing routing: the provider→filter VALUE path, the active-filter range clamp, and (Task 3)
/// the knob-aware request, the §9.1 rebuild budget, and R1 continuity on the adaptive path. Drives a real
/// <see cref="GazeEngine"/> with a synchronous fake provider — each SimulateGaze runs one OnGazePoint
/// inline, so a deferred reconfiguration is applied and observable with no threading.
/// </summary>
public class GazeEngineSmoothingRoutingTests
{
    // Default EyeCtrlSettings.Cursor: OneEuro, OneEuroMinCutoff 5.0, OneEuroBeta 0.5, SmoothingFactor 0.5.
    // Beta is pinned to 0 so the One-Euro cutoff == minCutoff at every speed — isolates the parameter under
    // test (same trick as GazeEngineFilterRecreationTests); with the default beta 0.5 a fast sample spikes
    // the speed-adaptive cutoff and washes the effect out.
    private static (GazeEngine Engine, EffectiveSettingsProvider Provider, TestGazeProvider TestGaze, EyeCtrlSettings Settings) Make(
        GazeFilterMode mode = GazeFilterMode.OneEuro, bool adaptEnabled = true)
    {
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = mode;
        settings.Cursor.OneEuroBeta = 0.0;
        settings.Adaptation.Enabled = adaptEnabled;
        var provider = new EffectiveSettingsProvider(settings.Adaptation);
        var testGaze = new TestGazeProvider();
        var engine = new GazeEngine(testGaze, new TestInputSimulator(), settings, effectiveSettings: provider);
        return (engine, provider, testGaze, settings);
    }

    // --- Task 2: the value path (method-level, no pumping) ---

    [Fact]
    public void EffectiveParameter_NoProvider_ReturnsNull()
    {
        var settings = new EyeCtrlSettings();
        var engine = new GazeEngine(new TestGazeProvider(), new TestInputSimulator(), settings);
        Check.That(engine.EffectiveSmoothingParameter()).IsNull();
    }

    [Theory]
    [InlineData(GazeFilterMode.OneEuro, 5.0)]
    [InlineData(GazeFilterMode.Smoothed, 0.5)]
    public void EffectiveParameter_NoFill_EqualsTheActiveModesBaseline(GazeFilterMode mode, double baseline)
    {
        var (engine, _, _, _) = Make(mode);
        Check.That(engine.EffectiveSmoothingParameter()).IsEqualTo(baseline);
    }

    [Fact]
    public void EffectiveParameter_Disabled_EqualsBaseline_EvenWithFillSet()
    {
        var (engine, provider, _, _) = Make(GazeFilterMode.OneEuro, adaptEnabled: false);
        provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
        Check.That(engine.EffectiveSmoothingParameter()).IsEqualTo(5.0);
    }

    [Fact]
    public void EffectiveParameter_Direct_ReturnsNull_EvenWithFillSet()
    {
        // §13.3: DirectFilter takes no smoothing parameter and mode is never auto-switched (§5), so the
        // knob must no-op — the smoothing fill is simply not observable in Direct mode.
        var (engine, provider, _, _) = Make(GazeFilterMode.Direct);
        provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
        Check.That(engine.EffectiveSmoothingParameter()).IsNull();
    }

    [Theory]
    // mode, baseline, band fraction, expected — the fill is always the full band (1.0)
    [InlineData(GazeFilterMode.OneEuro, 5.0, 0.50, 2.5)]    // plain composition, no clamp
    [InlineData(GazeFilterMode.OneEuro, 0.2, 0.50, 0.1)]    // composes exactly onto the floor
    [InlineData(GazeFilterMode.OneEuro, 0.1, 0.50, 0.1)]    // 0.05 < floor 0.1 → clamped (§13.3 safety)
    [InlineData(GazeFilterMode.OneEuro, 0.1, 1.00, 0.1)]    // 0.0: OneEuroFilter's ctor would throw → clamped
    [InlineData(GazeFilterMode.OneEuro, 20.0, 0.50, 10.0)]  // top baseline; the upper bound never engages
    [InlineData(GazeFilterMode.Smoothed, 0.5, 0.50, 0.25)]  // EMA composes the same way
    [InlineData(GazeFilterMode.Smoothed, 1.0, 1.00, 0.0)]   // EMA floor is 0, and 0.0 is a legal factor
    public void EffectiveParameter_ClampsToTheActiveFiltersValidatedRange(
        GazeFilterMode mode, double baseline, double fraction, double expected)
    {
        // The provider's Smoothing policy carries the union superset [0, 20]; the ACTIVE filter's own
        // validated range ([0.1, 20] One-Euro, [0, 1] EMA) is re-applied here (§13.3 safety requirement).
        var (engine, provider, _, settings) = Make(mode);
        if (mode == GazeFilterMode.OneEuro) settings.Cursor.OneEuroMinCutoff = baseline;
        else settings.Cursor.SmoothingFactor = baseline;
        settings.Adaptation.SmoothingMoreMaxFraction = fraction;
        provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);

        Check.That(engine.EffectiveSmoothingParameter()).IsEqualTo(expected);
    }

    [Theory]
    [InlineData(GazeFilterMode.OneEuro, 0.1, 1.0)]   // the composed-0.0 trap: must assign 0.1, not 0.0
    [InlineData(GazeFilterMode.Smoothed, 0.5, 1.0)]
    public void EffectiveParameter_IsAlwaysAssignableToTheSettingsGuard(GazeFilterMode mode, double baseline, double fill)
    {
        // Setter-as-oracle drift guard (the FixationRadius precedent): the CursorSettings setters ARE the
        // validated range — they throw outside it. If the clamp ever drifts from the guard, this throws.
        var (engine, provider, _, settings) = Make(mode);
        if (mode == GazeFilterMode.OneEuro) settings.Cursor.OneEuroMinCutoff = baseline;
        else settings.Cursor.SmoothingFactor = baseline;
        provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: fill, tighteningFill: 0.0);

        double effective = engine.EffectiveSmoothingParameter()!.Value;
        if (mode == GazeFilterMode.OneEuro) settings.Cursor.OneEuroMinCutoff = effective;
        else settings.Cursor.SmoothingFactor = effective;
    }

    [Fact]
    public void EffectiveParameter_ComposesAgainstTheActiveModesBaseline_AcrossAModeSwitch()
    {
        // The fill is a fraction OF the baseline, and the baseline is resolved PER MODE at read time:
        // OneEuro 5.0 → 2.5, then switching the mode to Smoothed (baseline 0.5) composes the SAME fill
        // against 0.5 → 0.25. The apply-time re-read (Task 3) is what makes a mode switch converge.
        var (engine, provider, _, settings) = Make(GazeFilterMode.OneEuro);
        provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
        Check.That(engine.EffectiveSmoothingParameter()).IsEqualTo(2.5);

        settings.Cursor.FilterMode = GazeFilterMode.Smoothed;
        Check.That(engine.EffectiveSmoothingParameter()).IsEqualTo(0.25);
    }

    [Fact]
    public void AppliedParameter_AfterConstruction_IsTheInitialBaselineParameter()
    {
        var (engine, _, _, _) = Make(GazeFilterMode.OneEuro);
        Check.That(engine.AppliedSmoothingParameter).IsEqualTo(5.0);
    }

    [Fact]
    public void AppliedParameter_DirectModeAtConstruction_IsNull()
    {
        var (engine, _, _, _) = Make(GazeFilterMode.Direct);
        Check.That(engine.AppliedSmoothingParameter).IsNull();
    }

    // --- Task 3: the wiring (behavioural, through SimulateGaze) ---

    [Fact]
    public void SmoothingFill_RetunesOneEuro_Live()
    {
        // Two identical engines; a full Smoothing fill on A only. After the next sample applies the
        // deferred rebuild, A's live filter must run at the EFFECTIVE minCutoff (5.0 − 0.50×5.0 = 2.5 Hz)
        // and lag a step MORE than B's baseline filter. End-to-end: Changed → knob-aware request →
        // gaze-thread apply → factory overload.
        var a = Make(GazeFilterMode.OneEuro);
        var b = Make(GazeFilterMode.OneEuro);

        long ts = 0;
        for (int i = 0; i < 12; i++, ts += 33_000)
        {
            a.TestGaze.SimulateGaze(500, 500, ts);
            b.TestGaze.SimulateGaze(500, 500, ts);
        }
        a.Provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
        a.TestGaze.SimulateGaze(500, 500, ts); // rebuild #1 (seeded clock → first is immediate)
        b.TestGaze.SimulateGaze(500, 500, ts); // identical timestamp streams — One-Euro alpha is dt-dependent
        ts += 33_000;

        Check.That(a.Engine.AppliedSmoothingParameter).IsEqualTo(2.5);
        Check.That(b.Engine.AppliedSmoothingParameter).IsEqualTo(5.0);

        double lastA = 0, lastB = 0;
        a.Engine.CursorMoved += (p, _) => lastA = p.X;
        b.Engine.CursorMoved += (p, _) => lastB = p.X;
        a.TestGaze.SimulateGaze(600, 500, ts);
        b.TestGaze.SimulateGaze(600, 500, ts);

        Check.That(lastA).IsStrictlyLessThan(lastB); // the 2.5 Hz filter trails the step further behind
    }

    [Fact]
    public void SmoothingFill_RetunesEma_Live()
    {
        // Same for the EMA path: effective factor 0.5 − 0.50×0.5 = 0.25 lags far more than the baseline
        // (EMA has no speed adaptation, so the difference is large and unambiguous).
        var a = Make(GazeFilterMode.Smoothed);
        var b = Make(GazeFilterMode.Smoothed);

        long ts = 0;
        for (int i = 0; i < 12; i++, ts += 33_000)
        {
            a.TestGaze.SimulateGaze(500, 500, ts);
            b.TestGaze.SimulateGaze(500, 500, ts);
        }
        a.Provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
        a.TestGaze.SimulateGaze(500, 500, ts); // rebuild #1
        b.TestGaze.SimulateGaze(500, 500, ts); // identical streams (EMA is dt-independent; kept for symmetry)
        ts += 33_000;

        Check.That(a.Engine.AppliedSmoothingParameter).IsEqualTo(0.25);

        double lastA = 0, lastB = 0;
        a.Engine.CursorMoved += (p, _) => lastA = p.X;
        b.Engine.CursorMoved += (p, _) => lastB = p.X;
        a.TestGaze.SimulateGaze(600, 500, ts);
        b.TestGaze.SimulateGaze(600, 500, ts);

        Check.That(lastA).IsStrictlyLessThan(lastB);
    }

    [Fact]
    public void NonSmoothingFill_DoesNotRebuildTheFilter()
    {
        // §13.3 item (2), the negative: Changed is payloadless and fires for EVERY knob's fill, but a
        // DwellTime fill must not spend a rebuild. The step-response comparison alone cannot pin this — a
        // spurious same-parameter rebuild is behaviourally invisible (warm-started at the same value) — so
        // the rebuild COUNT is the assertion (external implementation review, 2026-09-02: an unconditional
        // request in the handler survived the step-response variants; only the counter kills it).
        var a = Make(GazeFilterMode.OneEuro);
        var b = Make(GazeFilterMode.OneEuro);
        a.Provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);

        double lastA = 0, lastB = 0;
        a.Engine.CursorMoved += (p, _) => lastA = p.X;
        b.Engine.CursorMoved += (p, _) => lastB = p.X;

        long ts = 0;
        for (int i = 0; i < 13; i++, ts += 33_000)
        {
            a.TestGaze.SimulateGaze(500, 500, ts);
            b.TestGaze.SimulateGaze(500, 500, ts);
        }
        a.TestGaze.SimulateGaze(600, 500, ts);
        b.TestGaze.SimulateGaze(600, 500, ts);

        Check.That(lastA).IsEqualTo(lastB);
        Check.That(a.Engine.AppliedSmoothingParameter).IsEqualTo(5.0); // no rebuild happened
        Check.That(a.Engine.InterpolatorRebuildCount).IsEqualTo(0);    // …and none was SPENT (the cost property)
    }

    [Fact]
    public void SecondAdaptiveRebuild_DefersUntilOneSecondElapsed()
    {
        // §9.1 budget: adaptation can step the fill every 250 ms tick; the ADAPTIVE rebuild is rate-limited
        // to ~1/s and a pending request is DEFERRED, not dropped — the next sample after the interval
        // applies the then-current effective value. Rebuild #1 (fill 1.0 → 2.5 Hz) is immediate; a second
        // fill change (fill 0.5 → effective 5.0 − 0.50×0.5×5.0 = 3.75 Hz) within 1 s must not apply until
        // the interval elapses — at exactly the 1 s boundary it does. The first fill is set AFTER the
        // warmup loop: set earlier, the loop's ts=0 sample consumes rebuild #1 immediately and anchors the
        // budget clock at 0, so the "exactly 1 s" sample lands at elapsed 1.396 s and the boundary itself
        // is never exercised (round-2 review, finding 2).
        var a = Make(GazeFilterMode.OneEuro);

        long ts = 0;
        for (int i = 0; i < 12; i++, ts += 33_000) a.TestGaze.SimulateGaze(500, 500, ts);

        a.Provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
        a.TestGaze.SimulateGaze(500, 500, ts); // rebuild #1 at ts = 396_000 (seeded clock → immediate)
        Check.That(a.Engine.AppliedSmoothingParameter).IsEqualTo(2.5);

        a.Provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 0.5, tighteningFill: 0.0);
        a.TestGaze.SimulateGaze(500, 500, ts += 100_000);     // elapsed exactly 100_000 → DEFERRED
        Check.That(a.Engine.AppliedSmoothingParameter).IsEqualTo(2.5);

        a.TestGaze.SimulateGaze(500, 500, ts += 900_000);     // elapsed exactly 1_000_000 → applies (boundary)
        Check.That(a.Engine.AppliedSmoothingParameter).IsEqualTo(3.75);
    }

    [Fact]
    public void ClearMomentaryFills_RestoresTheBaselineFilter()
    {
        // §8.6 escape: clearing the fills (toolbar reset / session boundary) must return the LIVE filter to
        // baseline — the handler sees effective (no fill) ≠ applied and requests the restore rebuild.
        var a = Make(GazeFilterMode.OneEuro);
        var b = Make(GazeFilterMode.OneEuro); // never adapted reference

        long ts = 0;
        for (int i = 0; i < 12; i++, ts += 33_000)
        {
            a.TestGaze.SimulateGaze(500, 500, ts);
            b.TestGaze.SimulateGaze(500, 500, ts);
        }
        a.Provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
        a.TestGaze.SimulateGaze(500, 500, ts); // rebuild #1
        b.TestGaze.SimulateGaze(500, 500, ts); // identical timestamp streams — One-Euro alpha is dt-dependent
        Check.That(a.Engine.AppliedSmoothingParameter).IsEqualTo(2.5);

        a.Provider.ClearMomentaryFills();
        a.TestGaze.SimulateGaze(500, 500, ts += 2_000_000); // 2 s later — past the budget clock
        b.TestGaze.SimulateGaze(500, 500, ts);
        Check.That(a.Engine.AppliedSmoothingParameter).IsEqualTo(5.0);

        double lastA = 0, lastB = 0;
        a.Engine.CursorMoved += (p, _) => lastA = p.X;
        b.Engine.CursorMoved += (p, _) => lastB = p.X;
        a.TestGaze.SimulateGaze(600, 500, ts += 33_000);
        b.TestGaze.SimulateGaze(600, 500, ts);

        // Both filters now run at 5.0 Hz from (within rounding of) the same steady state, so the step
        // responses agree to far below a pixel. Bit-equality is NOT asserted: A's filter was rebuilt
        // (warm-seeded) while B's is continuous, and float rounding along those two histories differs.
        Check.That(lastA).IsCloseTo(lastB, 1e-6);
    }

    [Fact]
    public void FilterModeSwitch_AppliesTheNewModesEffectiveParameter()
    {
        // A mode switch (baseline rebuild path, per-frame — not budget-limited) must also apply the
        // EFFECTIVE parameter of the NEW mode: the same fill composes against 0.5 → 0.25.
        var a = Make(GazeFilterMode.OneEuro);

        long ts = 0;
        for (int i = 0; i < 12; i++, ts += 33_000) a.TestGaze.SimulateGaze(500, 500, ts);

        a.Provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
        a.TestGaze.SimulateGaze(500, 500, ts); // rebuild #1
        Check.That(a.Engine.AppliedSmoothingParameter).IsEqualTo(2.5);

        a.Settings.Cursor.FilterMode = GazeFilterMode.Smoothed;
        a.TestGaze.SimulateGaze(500, 500, ts += 33_000);
        Check.That(a.Engine.AppliedSmoothingParameter).IsEqualTo(0.25);
    }

    [Fact]
    public void AdaptiveRebuild_IsContinuous_ContinuesTheTrajectoryNoSnap()
    {
        // R1 continuity on the ADAPTIVE path. The engine drifts +10 px per sample (raw 560 by sample 18,
        // filtered X lagging behind raw at ≈550.5). Filling and pumping one more sample rebuilds the filter
        // BEFORE the sample is processed: the fresh 2.5 Hz filter is warm-seeded from the outgoing one, so
        // the output must continue the trajectory — strictly between the last filtered X and the raw 570.
        // A cold filter would snap to exactly 570 (a cold One-Euro's first call returns the raw point).
        // The applied-parameter assert first pins that the rebuild REALLY ran — without it this test passes
        // even when no rebuild happens at all (review round 1, finding 5).
        var a = Make(GazeFilterMode.OneEuro);
        double lastX = 0;
        a.Engine.CursorMoved += (p, _) => lastX = p.X;

        long ts = 0;
        for (int i = 0; i < 12; i++, ts += 33_000) a.TestGaze.SimulateGaze(500, 500, ts);
        for (int i = 1; i <= 6; i++, ts += 33_000) a.TestGaze.SimulateGaze(500 + 10 * i, 500, ts);
        double before = lastX;

        a.Provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
        a.TestGaze.SimulateGaze(570, 500, ts); // rebuild consumes at the top; THIS sample runs the new filter

        Check.That(a.Engine.AppliedSmoothingParameter).IsEqualTo(2.5); // the rebuild really ran
        Check.That(lastX).IsStrictlyGreaterThan(before); // no backwards jump (warm seed worked)
        Check.That(lastX).IsStrictlyLessThan(570.0);     // no snap to raw (cold filter would return 570)
    }

    [Fact]
    public void OneEuroFloorTrap_EndToEnd_ConstructsAndApplies_NoThrow()
    {
        // §13.3's mandated end-to-end discharge: a 0.1 Hz baseline at fraction 1.0 composes to exactly 0.0
        // pre-clamp, which OneEuroFilter's ctor rejects — unguarded, that throw lands on the gaze thread
        // inside ApplyPendingInterpolatorRebuild. The clamp must hold through the REAL rebuild path
        // (factory construction), not just at the value level. SimulateGaze is synchronous, so reaching
        // the assertions at all proves no exception escaped OnGazePoint.
        var a = Make(GazeFilterMode.OneEuro);

        long ts = 0;
        for (int i = 0; i < 12; i++, ts += 33_000) a.TestGaze.SimulateGaze(500, 500, ts);

        a.Settings.Cursor.OneEuroMinCutoff = 0.1;
        a.Settings.Adaptation.SmoothingMoreMaxFraction = 1.0;
        a.Provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
        a.TestGaze.SimulateGaze(500, 500, ts); // rebuild consumes here (baseline edit + fill both pending)

        Check.That(a.Engine.AppliedSmoothingParameter).IsEqualTo(0.1);
    }

    [Fact]
    public void GazeClockRestart_AppliesImmediately_ResyncsTheBudgetClock()
    {
        // A device reconnect can move the gaze clock backwards — the same jump class the telemetry sink's
        // resync guard handles (TelemetryClockRestartTests). Without the signed elapsed guard, a restart
        // makes elapsed hugely negative, it reads "< MinInterval", and every adaptive rebuild is deferred
        // for the length of the old epoch's uptime. Rebuild #1 at 1 h of uptime; the clock restarts near
        // zero with a changed fill — the rebuild must apply on the very next sample.
        var a = Make(GazeFilterMode.OneEuro);
        a.Provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);

        long ts = 3_600_000_000; // 1 h of device uptime
        for (int i = 0; i < 12; i++, ts += 33_000) a.TestGaze.SimulateGaze(500, 500, ts);
        a.TestGaze.SimulateGaze(500, 500, ts);
        Check.That(a.Engine.AppliedSmoothingParameter).IsEqualTo(2.5);

        a.Provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 0.5, tighteningFill: 0.0);
        a.TestGaze.SimulateGaze(500, 500, 50_000); // reconnect: gaze clock back near zero
        Check.That(a.Engine.AppliedSmoothingParameter).IsEqualTo(3.75);
    }

    [Fact]
    public void RacingFillClear_DuringRebuildApply_RevalidationRequestsRestore()
    {
        // The §8.6 lost-update close (external implementation review, 2026-09-02: deleting the revalidation
        // tail survived all 31 tests). The hook fires AFTER the fresh interpolator is swapped in but BEFORE
        // the applied-bits publication — the exact window where the dispatcher's Changed handler would read
        // the fill change (clear → effective 5.0) against the STALE applied bits (5.0, not yet exchanged to
        // 2.5), find them equal, and skip its request. Without the post-publish revalidation the filter
        // would sit at 2.5 Hz with no outstanding request; with it, the next sample past the 1 s budget
        // restores the baseline. NOTE the hook must fire BEFORE the exchange: placed after it, the handler
        // sees the new bits, requests itself, and the tail is untested.
        var a = Make(GazeFilterMode.OneEuro);
        long ts = 0;
        for (int i = 0; i < 12; i++, ts += 33_000) a.TestGaze.SimulateGaze(500, 500, ts);

        a.Provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
        a.Engine.BeforeSmoothingPublishForTest = () => a.Provider.ClearMomentaryFills();
        a.TestGaze.SimulateGaze(500, 500, ts); // rebuild applies 2.5; the clear races inside the stale-bits window
        a.Engine.BeforeSmoothingPublishForTest = null;
        Check.That(a.Engine.AppliedSmoothingParameter).IsEqualTo(2.5); // this rebuild still landed at 2.5

        a.TestGaze.SimulateGaze(500, 500, ts += 1_000_000); // past the 1 s budget — the re-request applies
        Check.That(a.Engine.AppliedSmoothingParameter).IsEqualTo(5.0);
    }

    // --- Fakes (own copies — no shared base class; mirrors GazeEngineFilterRecreationTests) ---

    private sealed class TestGazeProvider : IGazeProvider
    {
        public event Action<GazePoint>? GazePointReceived;
        public bool IsConnected => true;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void SimulateGaze(double x, double y, long ts) =>
            GazePointReceived?.Invoke(new GazePoint(x, y, ts, true));
    }

    private sealed class TestInputSimulator : IInputSimulator
    {
        public void MoveCursor(PhysicalPoint point) { }
        public void LeftClick(PhysicalPoint point) { }
        public void RightClick(PhysicalPoint point) { }
        public void DoubleClick(PhysicalPoint point) { }
        public void MouseDown(PhysicalPoint point) { }
        public void MouseUp(PhysicalPoint point) { }
        public void Scroll(PhysicalPoint point, int deltaX, int deltaY) { }
        public void SendKeyPress(ushort virtualKeyCode) { }
        public void SendKeyDown(ushort virtualKeyCode) { }
        public void SendKeyUp(ushort virtualKeyCode) { }
        public void SendText(string text) { }
        public void SendModifiedKeyPress(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode) { }
    }
}
