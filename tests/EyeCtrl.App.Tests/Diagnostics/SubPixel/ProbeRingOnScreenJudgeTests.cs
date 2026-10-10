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
}
