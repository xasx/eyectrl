namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System;
using System.Linq;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;

/// <summary>
/// Fixup 04 F2, headless: the capture order must make every repeat an independent present. WPF
/// invalidates nothing when a <c>TranslateTransform</c> is set to its current value, so a capture
/// after an identical placement is the same pixels — the pre-fixup sweep and reference spreads read
/// 0 by construction for exactly that reason.
/// </summary>
[Collection("WpfApplication")]
public class ProbeRingCapturePlanTests
{
    private const double Scale = 2.0;
    private const double WindowLeftDip = 800.0;
    private const double WindowTopDip = 440.0;
    private const int ScreenWidthPx = 3840;
    private const int ScreenHeightPx = 2400;

    private static (double X, double Y) Placement(ProbeRingCaptureStep step)
    {
        var frame = ProbeRingFrameSchedule.For(
            WindowLeftDip, WindowTopDip, Scale, step.FractionX, step.FractionY, ScreenWidthPx, ScreenHeightPx);
        var topLeft = SubPixelPlacement.TopLeft(
            frame.RingLocalCentrePx, ProbeRingCaptureScene.RingSizeDip, Scale, step.Snap);
        return (topLeft.X, topLeft.Y);
    }

    /// <summary>
    /// The F2 pin: no two consecutive captures may share a placement. Killed by the pre-fixup order
    /// (fraction-major sweep), whose repeats were back-to-back identical placements.
    /// </summary>
    [Fact]
    public void NoTwoConsecutiveCaptures_ShareAPlacement()
    {
        var plan = ProbeRingCapturePlan.Build();

        for (var i = 1; i < plan.Count; i++)
        {
            var previous = Placement(plan[i - 1]);
            var current = Placement(plan[i]);

            Check.WithCustomMessage(
                    $"Capture {i} ({plan[i].Kind} {plan[i].Condition} f={plan[i].FractionX:0.00}/{plan[i].FractionY:0.00} "
                    + $"snap={plan[i].Snap}) repeats capture {i - 1}'s placement ({previous.X}, {previous.Y}); WPF "
                    + "would present the same surface and the repeat would be the same pixels.")
                .That(current == previous).IsFalse();
        }
    }

    /// <summary>
    /// The shape the ticket asks for: a pass-major, rotated sweep (three passes over all 20
    /// fractions), five interleaved f = 0 B references plus condition A's own reference, and the
    /// decider blocks. Asserted structurally, so a reverted order fails here as well as at the
    /// placement pin.
    /// </summary>
    [Fact]
    public void TheSweepIsPassMajor_AndTheReferencesAreInterleaved()
    {
        var plan = ProbeRingCapturePlan.Build();

        var references = plan
            .Select((step, index) => (step, index))
            .Where(entry => entry.step.Kind == ProbeRingOnScreenJudge.KindReference)
            .ToList();
        Check.WithCustomMessage("Five f = 0 B references plus condition A's own, six reference captures in all.")
            .That(references).HasSize(6);
        Check.WithCustomMessage("The run must open with a reference.")
            .That(references[0].index).IsEqualTo(0);
        Check.WithCustomMessage("Both conditions need a reference.")
            .That(references.Count(entry => entry.step.Condition == ProbeRingOnScreenJudge.ConditionA)).IsEqualTo(1);
        Check.WithCustomMessage(
                "No two reference captures may be adjacent (f = 0 next to f = 0 repeats a placement); got positions "
                + string.Join(", ", references.Select(entry => entry.index)) + ".")
            .That(references.Zip(references.Skip(1), (a, b) => b.index - a.index).All(gap => gap > 1)).IsTrue();

        // Sweep passes: each run of KindSweep must be exactly one pass over the fractions, rotated so
        // neither end is f = 0, so fraction-major order (three repeats of one fraction in a row) and
        // an unrotated pass both fail here.
        var sweepFractions = plan
            .Where(step => step.Kind == ProbeRingOnScreenJudge.KindSweep)
            .Select(step => step.FractionX)
            .ToList();
        Check.That(sweepFractions).HasSize(
            ProbeRingCapturePlan.SweepPasses * ProbeRingOnScreenJudge.SweepFractions.Count);
        for (var pass = 0; pass < ProbeRingCapturePlan.SweepPasses; pass++)
        {
            var rotation = ProbeRingCapturePlan.SweepRotation(pass);
            var expected = Enumerable.Range(0, ProbeRingOnScreenJudge.SweepFractions.Count)
                .Select(i => ProbeRingOnScreenJudge.SweepFractions[
                    (rotation + i) % ProbeRingOnScreenJudge.SweepFractions.Count])
                .ToArray();
            var slice = sweepFractions
                .Skip(pass * ProbeRingOnScreenJudge.SweepFractions.Count)
                .Take(ProbeRingOnScreenJudge.SweepFractions.Count)
                .ToList();
            Check.WithCustomMessage($"Sweep pass {pass} must cover every fraction once, from rotation {rotation}.")
                .That(slice).ContainsExactly(expected);
        }

        Check.WithCustomMessage("The plan must carry the D2 solid-control block.")
            .That(plan.Count(step => step.Kind == ProbeRingOnScreenJudge.KindSolidControl))
            .IsEqualTo(1 + ProbeRingCapturePlan.SolidControlFractions.Count * ProbeRingOnScreenHarness.RowRepeats);
        Check.WithCustomMessage("The plan must carry the D3 X-only block.")
            .That(plan.Count(step => step.Kind == ProbeRingOnScreenJudge.KindXOnly))
            .IsEqualTo(1 + ProbeRingCapturePlan.XOnlyFractions.Count * ProbeRingCapturePlan.XOnlyRepeats);
    }

    /// <summary>
    /// The estimate the report prints is derived from the plan's length; a plan change must move it.
    /// </summary>
    [Fact]
    public void ThePlanLength_IsWhatTheTimeEstimateUses()
    {
        var plan = ProbeRingCapturePlan.Build();
        Check.WithCustomMessage(
                $"The plan has {plan.Count} captures; the printed estimate assumes "
                + $"{ProbeRingCapturePlan.EstimatedMillisecondsPerCapture} ms each and the target is about 10 s.")
            .That(plan.Count * ProbeRingCapturePlan.EstimatedMillisecondsPerCapture).IsLessOrEqualThan(12_000);
    }
}
