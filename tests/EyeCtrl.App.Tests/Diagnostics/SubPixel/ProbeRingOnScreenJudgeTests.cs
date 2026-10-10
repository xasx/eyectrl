namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System;
using System.Linq;
using EyeCtrl.Core.Models;

/// <summary>
/// Review 01 M1: the harness's judgement, driven headlessly. These are the tests that kill the
/// mutants which survived the first round — deleting the control verdict (O14), deleting the DPI
/// verdict (O15), changing condition A's assertion (O12), and shortening or coarsening the sweep
/// (O9/O10). O15 is the one that matters: a 2× DWM-stretched capture passes the positive control,
/// so the DPI verdict is the only guard against it.
///
/// <para>
/// The frames are synthetic renders through the same <see cref="ProbeRingCaptureScene"/> the
/// analysis tests use; the happy path proves the wiring the harness actually runs, and each failure
/// test asserts the <b>failed requirement's message</b>, not merely that something threw — so a
/// mutant that deletes one check cannot pass by failing a later one instead.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class ProbeRingOnScreenJudgeTests
{
    private const double HalfPx = ProbeRingCaptureScene.RingSizeDip * ProbeRingCaptureAnalysis.OwnerScale / 2.0;

    private static readonly PhysicalPoint CentreInCapture = new(300, 300);

    private static ProbeRingCaptureAnalysis.RingScanGeometry Geometry =>
        ProbeRingCaptureAnalysis.RingScanGeometry.For(CentreInCapture, HalfPx);

    private static ProbeRaster RingAt(double fraction, bool snap) =>
        ProbeRingCaptureScene.WeightsAt(new PhysicalPoint(300 + fraction, 300 + fraction), snap);

    private static (ProbeRaster Expected, ProbeRaster ReferenceB, ProbeRaster ReferenceA,
        ProbeRingOnScreenJudge.AbFrame[] Assertions, ProbeRingOnScreenJudge.SweepFrame[] Sweep) HealthyInputs(
            bool fractionalA = false)
    {
        var referenceB = RingAt(0.0, snap: false);
        var referenceA = RingAt(0.0, snap: true);
        var expected = RingAt(0.0, snap: false);   // independently rendered, not the reference frame

        var assertions = ProbeRingOnScreenJudge.AssertionFractions
            .Select(fraction => new ProbeRingOnScreenJudge.AbFrame(
                fraction,
                RingAt(fraction, snap: !fractionalA),
                RingAt(fraction, snap: false)))
            .ToArray();

        var sweep = ProbeRingOnScreenJudge.SweepFractions
            .Select(fraction => new ProbeRingOnScreenJudge.SweepFrame(fraction, RingAt(fraction, snap: false)))
            .ToArray();

        return (expected, referenceB, referenceA, assertions, sweep);
    }

    private static void RunJudge(
        double scale,
        ProbeRaster expected,
        ProbeRaster referenceB,
        ProbeRaster referenceA,
        ProbeRingOnScreenJudge.AbFrame[] assertions,
        ProbeRingOnScreenJudge.SweepFrame[] sweep) =>
        ProbeRingOnScreenJudge.Judge(
            scale, expected, referenceB, referenceA, assertions, sweep, Geometry, HalfPx, _ => { });

    private static string? FailureMessage(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    [Fact]
    public void SweepFractions_AreEveryPointOhFiveOverZeroToOne()
    {
        var fractions = ProbeRingOnScreenJudge.SweepFractions;

        Check.WithCustomMessage(
                "The sweep must schedule every 0.05 px over [0, 1) — twenty frames, the range and step the "
                + "record's fineness claim rests on (O9/O10's target).")
            .That(fractions).HasSize(20);
        for (var i = 0; i < fractions.Count; i++)
            Check.WithCustomMessage($"Sweep fraction {i} must be {i * 0.05}.")
                .That(fractions[i]).IsCloseTo(i * 0.05, 1e-12);

        Check.WithCustomMessage("The A/B assertion set must be f = 0.25 / 0.5 / 0.75.")
            .That(ProbeRingOnScreenJudge.AssertionFractions).ContainsExactly(0.25, 0.5, 0.75);
    }

    [StaFact]
    public void Judge_PassesOnConsistentSyntheticFrames()
    {
        var (expected, referenceB, referenceA, assertions, sweep) = HealthyInputs();

        // No throw is the pass: every requirement is asserted inside the Judge.
        RunJudge(2.0, expected, referenceB, referenceA, assertions, sweep);
    }

    [StaFact]
    public void Judge_FailsWhenTheScaleIsNotTheOwners()
    {
        var (expected, referenceB, referenceA, assertions, sweep) = HealthyInputs();

        var failure = FailureMessage(() => RunJudge(1.0, expected, referenceB, referenceA, assertions, sweep));

        Check.WithCustomMessage(
                "A 1.0-scale run must fail on the DPI verdict (O15): a DWM-bitmap-stretched capture passes the "
                + "positive control, so this is the only guard against it.")
            .That(failure).IsNotNull();
        Check.WithCustomMessage($"The failure must be the DPI requirement's; got: {failure}")
            .That(failure!).Contains("192");
    }

    [StaFact]
    public void Judge_FailsWhenTheControlDoesNotMatch()
    {
        var (expected, _, referenceA, assertions, sweep) = HealthyInputs();

        // The capture holds the ring one whole pixel from where the expected pattern puts it.
        // Everything else in the fixture is healthy, so the control's own message is the only thing
        // that can come out.
        var misplaced = RingAt(1.0, snap: false);

        var failure = FailureMessage(() => RunJudge(2.0, expected, misplaced, referenceA, assertions, sweep));

        Check.WithCustomMessage(
                "A capture whose ring is not where it was scheduled must fail on the control verdict (O14); "
                + "nothing else in this fixture is wrong.")
            .That(failure).IsNotNull();
        Check.WithCustomMessage($"The failure must be the control's; got: {failure}")
            .That(failure!).Contains("centre sits");
    }

    [StaFact]
    public void Judge_FailsWhenConditionAIsFractional()
    {
        var (expected, referenceB, referenceA, assertions, sweep) = HealthyInputs(fractionalA: true);

        var failure = FailureMessage(() => RunJudge(2.0, expected, referenceB, referenceA, assertions, sweep));

        Check.WithCustomMessage(
                "Condition A rendering fractionally is m1's shape: A then measures f where a whole pixel is "
                + "expected, and the whole-pixel assertion must be what fails (O12).")
            .That(failure).IsNotNull();
        Check.WithCustomMessage($"The failure must be condition A's; got: {failure}")
            .That(failure!).Contains("Condition A");
    }

    [StaFact]
    public void Judge_FailsWhenTheSweepScheduleIsShort()
    {
        var (expected, referenceB, referenceA, assertions, sweep) = HealthyInputs();
        var shortSweep = sweep.Take(10).ToArray();

        var failure = FailureMessage(() => RunJudge(2.0, expected, referenceB, referenceA, assertions, shortSweep));

        Check.WithCustomMessage(
                "Half the sweep must fail as a wrong schedule (O9): the record's fineness claim would "
                + "otherwise be made from ten points instead of twenty.")
            .That(failure).IsNotNull();
        Check.WithCustomMessage($"The failure must name the sweep schedule; got: {failure}")
            .That(failure!).Contains("sweep");
    }

    /// <summary>
    /// K1 (review 02 F1): m4's on-screen shape, on the Judge — B rendered snapped, so it measures
    /// the whole-pixel values (0/1/1) instead of the fractions. Kills the "B check made
    /// tautological" (J1) and "B accepts whole pixels" (J2) mutants.
    /// </summary>
    [StaFact]
    public void Judge_FailsWhenConditionBIsSnapped()
    {
        var (expected, referenceB, referenceA, _, sweep) = HealthyInputs();

        var snappedB = ProbeRingOnScreenJudge.AssertionFractions
            .Select(fraction => new ProbeRingOnScreenJudge.AbFrame(
                fraction, RingAt(fraction, snap: true), RingAt(fraction, snap: true)))
            .ToArray();

        var failure = FailureMessage(() => RunJudge(2.0, expected, referenceB, referenceA, snappedB, sweep));

        Check.WithCustomMessage(
                "A snapped B must fail on condition B's assertion: at f = 0.25/0.5/0.75 it measures "
                + "0/1/1, not 0.25/0.5/0.75.")
            .That(failure).IsNotNull();
        Check.WithCustomMessage($"The failure must be condition B's; got: {failure}")
            .That(failure!).Contains("Condition B");
    }

    /// <summary>
    /// K2 (review 02 F1): B off by 0.1 px must fail at the stated ±0.05. Kills J1/J2 and, because
    /// 0.1 is inside a loosened 0.3 tolerance, J5 too.
    /// </summary>
    [StaFact]
    public void Judge_FailsWhenBIsOffByATenth()
    {
        var (expected, referenceB, referenceA, _, sweep) = HealthyInputs();

        var offByATenth = ProbeRingOnScreenJudge.AssertionFractions
            .Select(fraction => new ProbeRingOnScreenJudge.AbFrame(
                fraction, RingAt(fraction, snap: true), RingAt(fraction + 0.1, snap: false)))
            .ToArray();

        var failure = FailureMessage(() => RunJudge(2.0, expected, referenceB, referenceA, offByATenth, sweep));

        Check.WithCustomMessage(
                "B rendered 0.1 px from the scheduled fraction must fail at R2's ±0.05; a loosened "
                + "tolerance must not let it through.")
            .That(failure).IsNotNull();
        Check.WithCustomMessage($"The failure must be condition B's; got: {failure}")
            .That(failure!).Contains("Condition B");
    }

    /// <summary>
    /// K3 (review 02 F3): reference A a whole pixel from reference B. At f = 0 they must draw at
    /// the same place; kills the "reference A-B check made tautological" mutant (J3).
    /// </summary>
    [StaFact]
    public void Judge_FailsWhenTheReferencesDisagree()
    {
        var (expected, referenceB, _, assertions, sweep) = HealthyInputs();

        var referenceAOnePixelOff = RingAt(1.0, snap: true);

        var failure = FailureMessage(() =>
            RunJudge(2.0, expected, referenceB, referenceAOnePixelOff, assertions, sweep));

        Check.WithCustomMessage(
                "Reference A drawn a whole pixel from reference B must fail the reference-offset check "
                + "(at f = 0 A and B draw at the same place).")
            .That(failure).IsNotNull();
        Check.WithCustomMessage($"The failure must be the reference offset's; got: {failure}")
            .That(failure!).Contains("With nothing to round");
    }
}
