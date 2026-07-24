namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;

public class AdaptationControllerTests
{
    private const long Sec = 1_000_000; // microseconds per second

    // Enabled, Conservative posture (default): Relax 0.50 / Sustain 3 s / RelaxStep 0.10, Recover 0.25 / RecoverStep 0.05.
    private static (AdaptationController Controller, EffectiveSettingsProvider Provider, AdaptationSettings Settings) Make()
    {
        var settings = new AdaptationSettings { Enabled = true };
        var provider = new EffectiveSettingsProvider(settings);
        return (new AdaptationController(provider, settings), provider, settings);
    }

    private static StruggleAssessment Assess(params FailureModeAssessment[] modes) => new(modes, 0.0);

    // The controller reads GatePassed + Score (not Confidence or AggregateLevel), so those are arbitrary here.
    private static FailureModeAssessment Mode(FailureMode mode, double score, bool gated = true) =>
        new(mode, score, gated ? 1.0 : 0.5, gated, null);

    [Fact]
    public void Forgiving_RelaxesOnlyAfterSustain_Tolerance()
    {
        var (c, p, _) = Make();
        var a = Assess(Mode(FailureMode.SustainFailure, 0.7)); // ≥ RelaxThreshold 0.5, gated

        // Below the 3 s sustain (t=0, t=2 s): no relaxation → baseline.
        c.Tick(a, 0);
        c.Tick(a, 2 * Sec);
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsEqualTo(50.0);

        // Sustain reached (t=3 s): first step (fill 0.10). Band +ToleranceLooser(0.5)×50 = +25; 10% → +2.5 → 52.5.
        c.Tick(a, 3 * Sec);
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsCloseTo(52.5, 1e-9);

        // Still sustained (t=4 s): second step (fill 0.20) → +5 → 55.
        c.Tick(a, 4 * Sec);
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsCloseTo(55.0, 1e-9);
    }

    [Fact]
    public void Forgiving_RecoversWhenStruggleSubsides()
    {
        var (c, p, _) = Make();
        var high = Assess(Mode(FailureMode.SustainFailure, 0.7));
        c.Tick(high, 0); c.Tick(high, 3 * Sec); c.Tick(high, 4 * Sec); // relax to fill 0.20 → 55
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsCloseTo(55.0, 1e-9);

        // No struggle (drive 0 < RecoverThreshold 0.25) → recover by RecoverStep 0.05: 0.20 → 0.15 → +3.75 → 53.75.
        c.Tick(Assess(), 5 * Sec);
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsCloseTo(53.75, 1e-9);
    }

    [Fact]
    public void Deadband_HoldsWithoutOscillation()
    {
        var (c, p, _) = Make();
        var high = Assess(Mode(FailureMode.SustainFailure, 0.7));
        c.Tick(high, 0); c.Tick(high, 3 * Sec); // relax to fill 0.10 → 52.5
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsCloseTo(52.5, 1e-9);

        // Drive in the deadband [0.25, 0.5): 0.35 → hold across ticks (neither relax nor recover, §8.8).
        var mid = Assess(Mode(FailureMode.SustainFailure, 0.35));
        c.Tick(mid, 4 * Sec);
        c.Tick(mid, 5 * Sec);
        c.Tick(mid, 6 * Sec);
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsCloseTo(52.5, 1e-9);
    }

    [Fact]
    public void UngatedMode_DoesNotDrive()
    {
        // High score but the confidence gate is NOT met → drive 0 → never relaxes (F4: below-gate evidence
        // never drives adaptation).
        var (c, p, _) = Make();
        var a = Assess(Mode(FailureMode.SustainFailure, 0.9, gated: false));
        c.Tick(a, 0); c.Tick(a, 3 * Sec); c.Tick(a, 6 * Sec);
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsEqualTo(50.0);
    }

    [Fact]
    public void NoStruggle_ReturnsExactBaseline_EvenOutOfRange()
    {
        // §12 sub-band value model at the controller level: with no gated struggle the controller sets no
        // fill, so effective == the exact baseline — even a baseline outside the knob's validated range
        // (the zero-delta ⇒ exact-baseline invariant, resolved by the provider / EffectiveValue).
        var (c, p, _) = Make();
        c.Tick(Assess(), 0);
        c.Tick(Assess(), 3 * Sec);
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 5000)).IsEqualTo(5000.0);
    }

    [Fact]
    public void Reset_ReturnsToBaseline()
    {
        var (c, p, _) = Make();
        var a = Assess(Mode(FailureMode.SustainFailure, 0.7));
        c.Tick(a, 0); c.Tick(a, 3 * Sec);
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsNotEqualTo(50.0);

        c.Reset(); // the always-reachable escape (§8.6)
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsEqualTo(50.0);
    }

    [Fact]
    public void MasterDisabled_ClearsMomentaryState()
    {
        var (c, p, s) = Make();
        var a = Assess(Mode(FailureMode.SustainFailure, 0.7));
        c.Tick(a, 0); c.Tick(a, 3 * Sec);
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsNotEqualTo(50.0);

        s.Enabled = false;
        c.Tick(a, 6 * Sec);  // disabled branch → clears controller + provider state
        s.Enabled = true;    // re-enable: if the controller CLEARED (not just the provider gate hid it), effective is baseline
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsEqualTo(50.0);
    }

    [Fact]
    public void ToleranceDisabled_StaysBaseline_UnderStruggle()
    {
        var (c, p, s) = Make();
        s.AdaptTolerance = false;
        var a = Assess(Mode(FailureMode.SustainFailure, 0.7));
        c.Tick(a, 0); c.Tick(a, 3 * Sec);
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsEqualTo(50.0);
    }

    [Fact]
    public void SustainIsWallClock_NotConsecutiveTicks()
    {
        // Sustain is measured in wall-clock from first-above-threshold (§7.2 "held SustainSeconds"), NOT a
        // count of consecutive ticks — so a single tick ≥ SustainSeconds after detection relaxes even with no
        // intermediate ticks (a stalled/sparse dispatcher). The coordinator (1b-⑤) advances `now` monotonically
        // at a fixed cadence and Resets on Pause, so a gap is the documented "continuous hold" (see Step).
        var (c, p, _) = Make();
        var a = Assess(Mode(FailureMode.SustainFailure, 0.7));
        c.Tick(a, 0);          // first tick above threshold → start sustaining, no step yet
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsEqualTo(50.0);
        c.Tick(a, 5 * Sec);    // 5 s ≥ 3 s sustain on the first tick after the gap → one relax step (0.10)
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsCloseTo(52.5, 1e-9);
    }

    [Fact]
    public void PostureSwitch_TakesEffectNextTick()
    {
        // Posture is read live (PosturePresets.For(_adaptation.Posture)) each tick, so a runtime posture change
        // takes effect on the next tick. Relax one Conservative step (0.10), switch to Responsive, then the next
        // sustained tick steps by Responsive's RelaxStepFraction 0.20 (→ 0.30 → 57.5), not Conservative's 0.10.
        var (c, p, s) = Make();
        var a = Assess(Mode(FailureMode.SustainFailure, 0.7));
        c.Tick(a, 0); c.Tick(a, 3 * Sec); // Conservative: fill 0.10 → 52.5
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsCloseTo(52.5, 1e-9);

        s.Posture = AdaptationPosture.Responsive; // RelaxThreshold 0.30 / SustainSeconds 1 / RelaxStepFraction 0.20
        c.Tick(a, 4 * Sec);                        // 0.10 + 0.20 = 0.30 → 50 + 0.30*25 = 57.5 (Conservative would give 55)
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsCloseTo(57.5, 1e-9);
    }

    [Fact]
    public void SustainFailure_LoosensTolerance_ShortensDwell_IncreasesSmoothing_NotGeometry()
    {
        var (c, p, _) = Make();
        var a = Assess(Mode(FailureMode.SustainFailure, 0.7));
        c.Tick(a, 0); c.Tick(a, 3 * Sec); // fill 0.10 on each driven knob

        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsCloseTo(52.5, 1e-9);   // +looser
        Check.That(p.Effective(AdaptationKnob.DwellTime, DwellContext.Other, 700)).IsCloseTo(672.0, 1e-9);      // −shorter (−0.4×700×0.10 = −28)
        Check.That(p.Effective(AdaptationKnob.Smoothing, DwellContext.Other, 5.0)).IsCloseTo(4.75, 1e-9);       // −more (−0.5×5×0.10 = −0.25)
        Check.That(p.Effective(AdaptationKnob.TargetGeometry, DwellContext.Other, 100)).IsEqualTo(100.0);        // sustain-failure does NOT enlarge geometry
    }

    [Fact]
    public void PoorTracking_IncreasesSmoothingAndGeometry_NotToleranceOrDwell()
    {
        var (c, p, _) = Make();
        var a = Assess(Mode(FailureMode.PoorTracking, 0.7));
        c.Tick(a, 0); c.Tick(a, 3 * Sec);

        Check.That(p.Effective(AdaptationKnob.Smoothing, DwellContext.Other, 5.0)).IsCloseTo(4.75, 1e-9);       // −more (smoothing fed by poor-tracking too, union)
        Check.That(p.Effective(AdaptationKnob.TargetGeometry, DwellContext.Other, 100)).IsCloseTo(105.0, 1e-9); // +bigger (+0.5×100×0.10 = +5)
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsEqualTo(50.0);          // NOT tolerance
        Check.That(p.Effective(AdaptationKnob.DwellTime, DwellContext.Other, 700)).IsEqualTo(700.0);            // NOT dwell (never tighten on poor-tracking, §7.1)
    }

    [Fact]
    public void Aiming_EnlargesGeometryOnly()
    {
        var (c, p, _) = Make();
        var a = Assess(Mode(FailureMode.Aiming, 0.7));
        c.Tick(a, 0); c.Tick(a, 3 * Sec);

        Check.That(p.Effective(AdaptationKnob.TargetGeometry, DwellContext.Other, 100)).IsCloseTo(105.0, 1e-9);
        Check.That(p.Effective(AdaptationKnob.Smoothing, DwellContext.Other, 5.0)).IsEqualTo(5.0);   // aiming ≠ smoothing
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsEqualTo(50.0);
    }

    [Fact]
    public void Drift_LoosensToleranceOnly()
    {
        var (c, p, _) = Make();
        var a = Assess(Mode(FailureMode.Drift, 0.7));
        c.Tick(a, 0); c.Tick(a, 3 * Sec);

        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsCloseTo(52.5, 1e-9); // loosen tolerance (the "report" half is 1b-⑤)
        Check.That(p.Effective(AdaptationKnob.DwellTime, DwellContext.Other, 700)).IsEqualTo(700.0);          // drift never shortens dwell
        Check.That(p.Effective(AdaptationKnob.TargetGeometry, DwellContext.Other, 100)).IsEqualTo(100.0);
    }

    [Fact]
    public void DwellDisabled_StaysBaseline_WhileOtherKnobsAdapt()
    {
        var (c, p, s) = Make();
        s.AdaptDwell = false;
        var a = Assess(Mode(FailureMode.SustainFailure, 0.7));
        c.Tick(a, 0); c.Tick(a, 3 * Sec);

        Check.That(p.Effective(AdaptationKnob.DwellTime, DwellContext.Other, 700)).IsEqualTo(700.0);            // dwell disabled
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsCloseTo(52.5, 1e-9);   // tolerance still adapts
        Check.That(p.Effective(AdaptationKnob.Smoothing, DwellContext.Other, 5.0)).IsCloseTo(4.75, 1e-9);       // smoothing still adapts
    }
}
