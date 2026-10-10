namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System;
using System.Collections.Generic;
using System.Linq;
using EyeCtrl.App.Tests.Diagnostics;
using EyeCtrl.Core.Models;

/// <summary>
/// R2's on-screen judgement as a pure function: every assertion the hardware run makes lives here,
/// taking frames and the effective scale, so it can be driven headlessly with synthetic captures.
/// The harness only captures and calls <see cref="Judge"/>.
///
/// <para>
/// <b>Why it is extracted (review 01 M1).</b> With the assertions inline in the harness, no headless
/// test executed them: deleting the control check (O14) or the DPI check (O15) stayed green, and
/// neither is killed by a healthy on-screen run either — they only bite when something is wrong.
/// O15 is the one that matters: a 2× DWM-stretched capture passes the positive control
/// (review 01 F3), so the DPI verdict is the only guard against a bitmap-stretched run, and it must
/// be pinned.
/// </para>
///
/// <para>
/// <b>The schedule is part of the judgement.</b> The A/B assertion set and the sweep's range and
/// step are constants here and the frames passed in must match them exactly — a harness that
/// captures half the sweep, or a coarser step, fails in <see cref="Judge"/> rather than quietly
/// recording a thinner one (O9/O10).
/// </para>
/// </summary>
internal static class ProbeRingOnScreenJudge
{
    /// <summary>R2's stated tolerance, the same ±0.05 px R1 asserts at.</summary>
    internal const double TolerancePx = 0.05;

    /// <summary>The fractions condition A and B are asserted at (requirement e).</summary>
    internal static IReadOnlyList<double> AssertionFractions { get; } = [0.25, 0.5, 0.75];

    /// <summary>The sweep's scheduled fractions: every 0.05 px over [0, 1) (requirement f).</summary>
    internal static IReadOnlyList<double> SweepFractions { get; } =
        [.. Enumerable.Range(0, 20).Select(i => i * 0.05)];

    /// <summary>One captured fraction with both conditions.</summary>
    /// <param name="Fraction">The scheduled fraction.</param>
    /// <param name="A">Condition A's capture (whole-pixel placement).</param>
    /// <param name="B">Condition B's capture (sub-pixel placement).</param>
    internal sealed record AbFrame(double Fraction, ProbeRaster A, ProbeRaster B);

    /// <summary>One sweep capture: condition B at the scheduled fraction.</summary>
    /// <param name="Fraction">The scheduled fraction.</param>
    /// <param name="B">Condition B's capture.</param>
    internal sealed record SweepFrame(double Fraction, ProbeRaster B);

    /// <summary>
    /// Judges a full on-screen run. Throws (NFluent) on the first failed requirement, with the
    /// requirement's own message.
    /// </summary>
    /// <param name="effectiveScale">The window's measured device pixels per DIP (requirement a).</param>
    /// <param name="expectedPattern">The expected ring rendered offscreen at the reference geometry (requirement c).</param>
    /// <param name="referenceB">The condition B reference capture at f = 0.</param>
    /// <param name="referenceA">The condition A reference capture at f = 0.</param>
    /// <param name="assertionFrames">A and B captures at <see cref="AssertionFractions"/> (requirement e).</param>
    /// <param name="sweepFrames">B captures at <see cref="SweepFractions"/> (requirement f).</param>
    /// <param name="geometry">The scan geometry of the reference frame, in capture-local pixels.</param>
    /// <param name="halfPx">The expected stroke radius, in pixels.</param>
    /// <param name="writeLine">Receives the session report lines.</param>
    internal static void Judge(
        double effectiveScale,
        ProbeRaster expectedPattern,
        ProbeRaster referenceB,
        ProbeRaster referenceA,
        IReadOnlyList<AbFrame> assertionFrames,
        IReadOnlyList<SweepFrame> sweepFrames,
        ProbeRingCaptureAnalysis.RingScanGeometry geometry,
        double halfPx,
        Action<string> writeLine)
    {
        // (a) The effective DPI must be the owner's, or a bitmap-stretched capture can pass the
        // control and every fraction would be measured at twice its size (review 01 O15).
        var dpi = ProbeRingCaptureAnalysis.CheckEffectiveDpi(effectiveScale);
        writeLine(Invariant($"R2 effective window scale: {effectiveScale:0.###}"));
        Check.WithCustomMessage(dpi.Message).That(dpi.IsOwnerScale).IsTrue();

        // (c) The positive control: the reference capture must hold the expected ring.
        var expectedCentre = new PhysicalPoint(geometry.ReferenceCentreX, geometry.ReferenceCentreY);
        var control = ProbeRingCaptureAnalysis.CheckExpectedRing(referenceB, expectedPattern, expectedCentre, halfPx);
        writeLine($"R2 positive control: {control.Reason}");
        Check.WithCustomMessage(control.Reason).That(control.Matched).IsTrue();

        // With nothing to round, A and B must draw at the same place (R1's first assertion).
        var referenceOffset = ProbeRingCaptureAnalysis.MeasureShift(referenceA, referenceB, geometry);
        writeLine(Invariant($"R2 reference A-B offset at f=0: ({referenceOffset.X:0.0000}, {referenceOffset.Y:0.0000}) px"));
        Check.WithCustomMessage(
                "With nothing to round, A and B must draw at the same place; measured "
                + $"({referenceOffset.X:0.0000}, {referenceOffset.Y:0.0000}) px.")
            .That(referenceOffset.X).IsCloseTo(0.0, TolerancePx);
        Check.WithCustomMessage(
                $"The reference frames' vertical offset must also be 0, measured {referenceOffset.Y:0.0000} px.")
            .That(referenceOffset.Y).IsCloseTo(0.0, TolerancePx);

        // (e) R1's centroid assertions on the hardware path.
        RequireSchedule(assertionFrames.Select(frame => frame.Fraction).ToList(), AssertionFractions,
            "A/B assertion", writeLine);
        foreach (var frame in assertionFrames)
        {
            var shiftB = ProbeRingCaptureAnalysis.MeasureShift(frame.B, referenceB, geometry);
            var shiftA = ProbeRingCaptureAnalysis.MeasureShift(frame.A, referenceA, geometry);
            var expectedA = Math.Round(frame.Fraction, MidpointRounding.AwayFromZero);

            writeLine(Invariant(
                $"R2 hardware f={frame.Fraction:0.00}: B=({shiftB.X:0.0000}, {shiftB.Y:0.0000}), A=({shiftA.X:0.0000}, {shiftA.Y:0.0000})"));

            Check.WithCustomMessage(
                    $"Condition B must render fractionally on the hardware path: f = {frame.Fraction:0.00} px but "
                    + $"the ring moved ({shiftB.X:0.0000}, {shiftB.Y:0.0000}) px. If either axis is 0 or 1, "
                    + "something snapped the ring to device pixels (m1/m4's shape).")
                .That(shiftB.X).IsCloseTo(frame.Fraction, TolerancePx);
            Check.WithCustomMessage(
                    $"Condition B's vertical move at f = {frame.Fraction:0.00} measured {shiftB.Y:0.0000} px.")
                .That(shiftB.Y).IsCloseTo(frame.Fraction, TolerancePx);
            Check.WithCustomMessage(
                    $"Condition A must land on whole device pixels: f = {frame.Fraction:0.00} should move "
                    + $"{expectedA:0} px, measured {shiftA.X:0.0000}.")
                .That(shiftA.X).IsCloseTo(expectedA, TolerancePx);
            Check.WithCustomMessage(
                    $"Condition A's vertical move at f = {frame.Fraction:0.00} should be {expectedA:0} px, "
                    + $"measured {shiftA.Y:0.0000}.")
                .That(shiftA.Y).IsCloseTo(expectedA, TolerancePx);
        }

        // (f) The fine-fraction sweep: a record of how finely the real display path resolves B.
        RequireSchedule(sweepFrames.Select(frame => frame.Fraction).ToList(), SweepFractions, "sweep", writeLine);
        var rows = new List<ProbeRingCaptureAnalysis.SweepRow>();
        foreach (var frame in sweepFrames)
        {
            var shift = ProbeRingCaptureAnalysis.MeasureShift(frame.B, referenceB, geometry);
            rows.Add(new ProbeRingCaptureAnalysis.SweepRow(frame.Fraction, shift.X, shift.Y));
            writeLine(Invariant(
                $"R2 sweep f={frame.Fraction:0.00}: measured=({shift.X:0.0000}, {shift.Y:0.0000}), error=({shift.X - frame.Fraction:0.0000}, {shift.Y - frame.Fraction:0.0000})"));
        }

        var summary = ProbeRingCaptureAnalysis.EvaluateSweep(rows);
        writeLine(Invariant(
            $"R2 sweep summary: max |error| {summary.MaxAbsErrorPx:0.0000} px; resolves B to {summary.ResolvedStepPx:0.00} px. {summary.Note}"));
    }

    /// <summary>
    /// The captured frames must be exactly the scheduled ones. This is what makes a shortened or
    /// coarsened sweep fail instead of being recorded as a thinner one (O9/O10).
    /// </summary>
    private static void RequireSchedule(
        IReadOnlyList<double> actual, IReadOnlyList<double> scheduled, string name, Action<string> writeLine)
    {
        writeLine(Invariant($"R2 {name} frames: {actual.Count} of the scheduled {scheduled.Count}."));
        Check.WithCustomMessage(
                $"The harness captured {actual.Count} {name} frames, not the scheduled {scheduled.Count}. "
                + "The sweep is a record, but it must be the scheduled record.")
            .That(actual.Count).IsEqualTo(scheduled.Count);
        for (var i = 0; i < scheduled.Count; i++)
        {
            Check.WithCustomMessage(
                    $"The {name} frame {i} carries fraction {actual[i]}, not the scheduled {scheduled[i]}.")
                .That(actual[i]).IsEqualTo(scheduled[i]);
        }
    }

    private static string Invariant(FormattableString message) => FormattableString.Invariant(message);
}
