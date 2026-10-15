namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EyeCtrl.App.Tests.Diagnostics;
using EyeCtrl.Core.Models;

/// <summary>
/// R2's on-screen judgement as a pure function: the harness captures and measures; the assertions
/// live here, taking frames and the effective scale, so they can be driven headlessly with
/// synthetic captures.
///
/// <para>
/// <b>Measure first, then judge (fixup 03 item 1).</b> <see cref="Measure"/> collects every
/// measurement and every failed requirement into a <see cref="JudgeReport"/> and never throws;
/// <see cref="WriteReport"/> prints the full per-axis table; <see cref="Judge(JudgeReport)"/> then
/// fails once with a combined summary of all failures. The thresholds are unchanged — ±0.05 px is
/// pre-registered — and the run-1 lesson is the reason for the split: a single failed check must
/// not hide the table the owner needs to tell a measurement offset from a real non-linearity.
/// </para>
///
/// <para>
/// <b>Repeats.</b> Each row is captured <c>N</c> times (re-presenting the frame each time) and
/// judged on the mean of its repeats; the spread (max − min) is reported as the noise. The f = 0
/// reference is captured several times in a row and reported as the noise floor. The mirrored
/// halves of every shift (left/right, top/bottom block centroids) are kept for diagnosis.
/// </para>
///
/// <para>
/// <b>The judged estimator is the 21-row average (owner's decision 2026-10-11, after run 2).</b>
/// Every judged value — the B rows, the A rows, the reference A–B check, and the sweep's numbers —
/// is <see cref="ProbeRingCaptureAnalysis.MeasureMirroredMultiRowShift"/>: R1's mirrored-block
/// estimator averaged over the scan rows' centre ± 10 and the columns' centre ± 10. Run 2 showed
/// the single scan row biased under diagonal motion on the hardware rasterizer while the 21-row
/// average stayed on target, and a genuinely misplaced ring moves every row, so the average keeps
/// the test's power against snapping. The single scan row stays in the output as
/// <c>R2 DIAGNOSTIC single-row …</c> lines with their own catch, never judged. The judged path
/// fails loudly: no catch around the 21-row measurement, and <see cref="ViolatesTolerance"/> makes
/// a non-finite judged value a failure (a bare <c>Math.Abs(NaN − f) &gt; tolerance</c> is false).
/// </para>
///
/// <para>
/// <b>Stale captures.</b> A capture that is byte-identical to the previous one is the previous
/// frame (consecutive plan steps never share a placement). The harness retakes it up to 3 times;
/// a stale capture reaching a judged row is a failure with a "stale frame" message, and in the
/// sweep and the diagnostics it is excluded from the mean and flagged (<c>stale=N</c>). The
/// offline analyser applies the same rule to a saved archive.
/// </para>
///
/// <para>
/// <b>Intruded captures (fixup 10 item 2; run 3's gaze-cursor overlay).</b> A capture whose pixels
/// are off-background outside the expected ring's dilated footprint carries an overlay and is
/// retaken like a stale one; if it stands, a judged row fails ("an overlay — e.g. a gaze cursor —
/// covered the capture region") and the sweep and diagnostics exclude and flag it
/// (<c>intruded=N</c>). The check and its thresholds live in <see cref="ProbeRingIntrusion"/>.
/// </para>
///
/// <para>
/// <b>The schedule is part of the judgement.</b> The A/B assertion set and the sweep's range and
/// step are constants here and the rows passed in must match them exactly — a harness that
/// captures half the sweep, or a coarser step, fails in <see cref="Measure"/> rather than quietly
/// recording a thinner one (O9/O10).
/// </para>
/// </summary>
internal static class ProbeRingOnScreenJudge
{
    /// <summary>R2's stated tolerance, the same ±0.05 px R1 asserts at. Pre-registered; do not change.</summary>
    internal const double TolerancePx = 0.05;

    /// <summary>The fractions condition A and B are asserted at (requirement e).</summary>
    internal static IReadOnlyList<double> AssertionFractions { get; } = [0.25, 0.5, 0.75];

    /// <summary>The sweep's scheduled fractions: every 0.05 px over [0, 1) (requirement f).</summary>
    internal static IReadOnlyList<double> SweepFractions { get; } =
        [.. Enumerable.Range(0, 20).Select(i => i * 0.05)];

    /// <summary>Condition A's label: whole-pixel placement.</summary>
    internal const string ConditionA = "A";

    /// <summary>Condition B's label: sub-pixel placement.</summary>
    internal const string ConditionB = "B";

    /// <summary>The f = 0 reference rows' kind (the noise floor).</summary>
    internal const string KindReference = "reference";

    /// <summary>The A/B assertion rows' kind.</summary>
    internal const string KindAssertion = "assertion";

    /// <summary>The sweep rows' kind. The assertion fractions 0.25/0.5/0.75 are sweep fractions too.</summary>
    internal const string KindSweep = "sweep";

    /// <summary>D2's kind: the solid-look control rows (diagnostic only).</summary>
    internal const string KindSolidControl = "solid-control";

    /// <summary>D3's kind: the X-only rows (diagnostic only).</summary>
    internal const string KindXOnly = "x-only";

    /// <summary>One captured fraction with both conditions (the single-repeat shape the tests drive).</summary>
    /// <param name="Fraction">The scheduled fraction.</param>
    /// <param name="A">Condition A's capture (whole-pixel placement).</param>
    /// <param name="B">Condition B's capture (sub-pixel placement).</param>
    internal sealed record AbFrame(double Fraction, ProbeRaster A, ProbeRaster B);

    /// <summary>One sweep capture: condition B at the scheduled fraction.</summary>
    /// <param name="Fraction">The scheduled fraction.</param>
    /// <param name="B">Condition B's capture.</param>
    internal sealed record SweepFrame(double Fraction, ProbeRaster B);

    /// <summary>One table row's captures: which condition, which fraction, and the repeats in capture order.</summary>
    /// <param name="Condition">"A" or "B".</param>
    /// <param name="Fraction">The scheduled fraction.</param>
    /// <param name="Repeats">The frames, one per repeat.</param>
    /// <param name="StaleFlags">Which repeats are stale copies, in capture order; null means none are.</param>
    /// <param name="IntrudedFlags">Which repeats are intruded (an overlay broke the background rule); null means none are.</param>
    internal sealed record RowSamples(
        string Condition,
        double Fraction,
        IReadOnlyList<ProbeRaster> Repeats,
        IReadOnlyList<bool>? StaleFlags = null,
        IReadOnlyList<bool>? IntrudedFlags = null);

    /// <summary>One diagnostic row: its own reference and its repeats (fixup 04 deciders).</summary>
    /// <param name="Fraction">The scheduled x fraction.</param>
    /// <param name="FractionY">The scheduled y fraction (0 for the X-only decider).</param>
    /// <param name="Repeats">The frames, one per repeat.</param>
    /// <param name="StaleFlags">Which repeats are stale copies; null means none are.</param>
    /// <param name="IntrudedFlags">Which repeats are intruded; null means none are.</param>
    internal sealed record DiagnosticRow(
        double Fraction,
        double FractionY,
        IReadOnlyList<ProbeRaster> Repeats,
        IReadOnlyList<bool>? StaleFlags = null,
        IReadOnlyList<bool>? IntrudedFlags = null);

    /// <summary>
    /// A diagnostic block (D2/D3): its own f = 0 reference and its rows. Diagnostics are measured
    /// and printed, never judged.
    /// </summary>
    /// <param name="Id">"D2" or "D3".</param>
    /// <param name="Label">What the block is for, for the output.</param>
    /// <param name="Reference">The block's own f = 0 capture.</param>
    /// <param name="Rows">The block's rows.</param>
    internal sealed record DiagnosticGroup(
        string Id, string Label, ProbeRaster Reference, IReadOnlyList<DiagnosticRow> Rows);

    /// <summary>Everything the judgement needs.</summary>
    /// <param name="EffectiveScale">The window's measured device pixels per DIP (requirement a).</param>
    /// <param name="ExpectedPattern">The expected ring rendered offscreen at the reference geometry (requirement c).</param>
    /// <param name="ReferenceB">The condition B reference capture at f = 0, repeat 0.</param>
    /// <param name="ReferenceA">The condition A reference capture at f = 0.</param>
    /// <param name="ReferenceBRepeats">The f = 0 B captures in a row (the noise floor); includes repeat 0.</param>
    /// <param name="Geometry">The scan geometry of the reference frame, in capture-local pixels.</param>
    /// <param name="HalfPx">The expected stroke radius, in pixels.</param>
    /// <param name="AssertionRows">A and B rows at <see cref="AssertionFractions"/> (requirement e).</param>
    /// <param name="SweepRows">B rows at <see cref="SweepFractions"/> (requirement f).</param>
    /// <param name="Diagnostics">The D2/D3 blocks: measured and printed, never judged.</param>
    /// <param name="ReferenceBStaleFlags">Which of the f = 0 B repeats are stale copies, in capture order.</param>
    /// <param name="ReferenceBIntrudedFlags">Which of the f = 0 B repeats are intruded, in capture order.</param>
    /// <param name="ReferenceAStale">Whether the f = 0 A reference is a stale copy (fixup 11 R1).</param>
    /// <param name="ReferenceAIntruded">Whether the f = 0 A reference is intruded (fixup 11 R1).</param>
    internal sealed record JudgeInputs(
        double EffectiveScale,
        ProbeRaster ExpectedPattern,
        ProbeRaster ReferenceB,
        ProbeRaster ReferenceA,
        IReadOnlyList<ProbeRaster> ReferenceBRepeats,
        ProbeRingCaptureAnalysis.RingScanGeometry Geometry,
        double HalfPx,
        IReadOnlyList<RowSamples> AssertionRows,
        IReadOnlyList<RowSamples> SweepRows,
        IReadOnlyList<DiagnosticGroup> Diagnostics,
        IReadOnlyList<bool>? ReferenceBStaleFlags = null,
        IReadOnlyList<bool>? ReferenceBIntrudedFlags = null,
        bool ReferenceAStale = false,
        bool ReferenceAIntruded = false);

    /// <summary>One failed requirement, named, with the message the fix must read.</summary>
    /// <param name="Requirement">The requirement's label, e.g. "(c) positive control".</param>
    /// <param name="Message">The full explanation.</param>
    internal sealed record RequirementFailure(string Requirement, string Message);

    /// <summary>One row's measured values, aggregated over its repeats.</summary>
    /// <param name="Condition">"A" or "B".</param>
    /// <param name="Fraction">The scheduled fraction.</param>
    /// <param name="Repeats">How many captures were aggregated.</param>
    /// <param name="StaleRepeats">How many of them were stale copies, excluded from every aggregate and flagged.</param>
    /// <param name="IntrudedRepeats">How many of them were intruded (an overlay over the capture), excluded and flagged.</param>
    /// <param name="ExpectedX">The expected x movement.</param>
    /// <param name="ExpectedY">The expected y movement.</param>
    /// <param name="MeanX">The judged mean x movement: the 21-row average, over the non-stale repeats.</param>
    /// <param name="MeanY">The judged mean y movement: the 21-row average, over the non-stale repeats.</param>
    /// <param name="SpreadX">max − min of the per-repeat judged x movements (the noise).</param>
    /// <param name="SpreadY">max − min of the per-repeat judged y movements.</param>
    /// <param name="LeftX">Mean of the left edge block's 21-row centroid shifts.</param>
    /// <param name="RightX">Mean of the right edge block's 21-row centroid shifts.</param>
    /// <param name="TopY">Mean of the top edge block's 21-row centroid shifts.</param>
    /// <param name="BottomY">Mean of the bottom edge block's 21-row centroid shifts.</param>
    /// <param name="SampleX">Every repeat's judged (21-row) x movement, in capture order — stale and intruded repeats included, so the failure messages' repeat numbers map 1:1 to captures.</param>
    /// <param name="SampleY">Every repeat's judged (21-row) y movement, in capture order.</param>
    /// <param name="StaleFlags">Which repeats are stale, in capture order.</param>
    /// <param name="IntrudedFlags">Which repeats are intruded, in capture order.</param>
    /// <param name="SingleRowMeanX">Diagnostic: the single-scan-row mean of x over the repeats; never judged.</param>
    /// <param name="SingleRowMeanY">Diagnostic: the single-scan-row mean of y over the repeats.</param>
    /// <param name="SingleRowSpreadX">Diagnostic: the single-row x spread over the repeats.</param>
    /// <param name="SingleRowSpreadY">Diagnostic: the single-row y spread over the repeats.</param>
    /// <param name="SingleRowError">Why the single-row diagnostic is missing, else null (its own catch).</param>
    internal sealed record RowMeasurement(
        string Condition,
        double Fraction,
        int Repeats,
        int StaleRepeats,
        int IntrudedRepeats,
        double ExpectedX,
        double ExpectedY,
        double MeanX,
        double MeanY,
        double SpreadX,
        double SpreadY,
        double LeftX,
        double RightX,
        double TopY,
        double BottomY,
        IReadOnlyList<double> SampleX,
        IReadOnlyList<double> SampleY,
        IReadOnlyList<bool> StaleFlags,
        IReadOnlyList<bool> IntrudedFlags,
        double SingleRowMeanX,
        double SingleRowMeanY,
        double SingleRowSpreadX,
        double SingleRowSpreadY,
        string? SingleRowError);

    /// <summary>One diagnostic row's measurement, labelled for the output (never judged).</summary>
    /// <param name="Id">"D2" or "D3".</param>
    /// <param name="Label">What the diagnostic is for.</param>
    /// <param name="Row">The measured row, or null when the measurement threw (fixup 05 P4).</param>
    /// <param name="Error">The failure message when <paramref name="Row"/> is null.</param>
    internal sealed record DiagnosticMeasurement(string Id, string Label, RowMeasurement? Row, string? Error);

    /// <summary>The whole measured run, with every failure collected — nothing thrown yet.</summary>
    internal sealed record JudgeReport(
        ProbeRingCaptureAnalysis.DpiVerdict Dpi,
        ProbeRingCaptureAnalysis.ControlVerdict Control,
        ProbeRingCaptureAnalysis.MirroredShift ReferenceOffset,
        RowMeasurement? ReferenceNoise,
        IReadOnlyList<RowMeasurement> AssertionRows,
        IReadOnlyList<RowMeasurement> SweepRows,
        ProbeRingCaptureAnalysis.SweepSummary Sweep,
        IReadOnlyList<DiagnosticMeasurement> Diagnostics,
        IReadOnlyList<RequirementFailure> Failures);

    /// <summary>
    /// Measures the run and evaluates every requirement, collecting failures instead of throwing.
    /// The A/B frame set and the sweep schedule are validated against the constants here.
    /// </summary>
    /// <param name="inputs">The captured frames and geometry.</param>
    internal static JudgeReport Measure(JudgeInputs inputs)
    {
        var failures = new List<RequirementFailure>();

        // (a) The effective DPI must be the owner's, or a bitmap-stretched capture can pass the
        // control and every fraction would be measured at twice its size (review 02 O15).
        var dpi = ProbeRingCaptureAnalysis.CheckEffectiveDpi(inputs.EffectiveScale);
        if (!dpi.IsOwnerScale)
            failures.Add(new RequirementFailure("(a) effective DPI", dpi.Message));

        // (c) The positive control: the reference capture must hold the expected ring.
        var expectedCentre = new PhysicalPoint(inputs.Geometry.ReferenceCentreX, inputs.Geometry.ReferenceCentreY);
        var control = ProbeRingCaptureAnalysis.CheckExpectedRing(
            inputs.ReferenceB, inputs.ExpectedPattern, expectedCentre, inputs.HalfPx);
        if (!control.Matched)
        {
            failures.Add(new RequirementFailure("(c) positive control", control.Reason));

            // (fixup 10 item 3; run 3's black first frame) A reference without a ring cannot anchor
            // a scan: measuring shifts against it would throw "No ring alpha in the scan window"
            // and bury the cause. Return the control failure and the DPI verdict now, with nothing
            // measured — the run can never pass on a reference that holds no ring.
            return new JudgeReport(
                dpi,
                control,
                ReferenceOffset: new ProbeRingCaptureAnalysis.MirroredShift(0.0, 0.0, 0.0, 0.0, 0.0, 0.0),
                ReferenceNoise: null,
                AssertionRows: [],
                SweepRows: [],
                Sweep: new ProbeRingCaptureAnalysis.SweepSummary(
                    double.NaN, double.NaN,
                    "Not measured: the positive control failed, so there is no ring to measure."),
                Diagnostics: [],
                failures);
        }

        // R1 (fixup 11, review 08): the references anchor every row — reference B r0 anchors every B
        // row and the sweep, reference A anchors every A row. A stale or intruded one voids the run:
        // measuring against it would surface as rendering failures on every row (run 4's window
        // appears exactly when the owner's gaze cursor is near it). Fail as an unclean reference and
        // measure nothing.
        var referenceProblems = new List<string>(4);
        if (inputs.ReferenceBStaleFlags is { Count: > 0 } staleB && staleB[0])
            referenceProblems.Add("reference B (r0) was stale (the screen did not repaint)");
        if (inputs.ReferenceBIntrudedFlags is { Count: > 0 } intrudedB && intrudedB[0])
            referenceProblems.Add("reference B (r0) was covered by an overlay");
        if (inputs.ReferenceAStale)
            referenceProblems.Add("reference A was stale (the screen did not repaint)");
        if (inputs.ReferenceAIntruded)
            referenceProblems.Add("reference A was covered by an overlay");
        if (referenceProblems.Count > 0)
        {
            failures.Add(new RequirementFailure("(e) reference frames",
                $"The judged anchor is not clean: {string.Join("; ", referenceProblems)}. Reference B r0 anchors "
                + "every B row and the sweep and reference A anchors every A row, so every row measured against it "
                + "is void; this fails as an unclean reference, not as a rendering failure."));
            return new JudgeReport(
                dpi,
                control,
                ReferenceOffset: new ProbeRingCaptureAnalysis.MirroredShift(0.0, 0.0, 0.0, 0.0, 0.0, 0.0),
                ReferenceNoise: null,
                AssertionRows: [],
                SweepRows: [],
                Sweep: new ProbeRingCaptureAnalysis.SweepSummary(
                    double.NaN, double.NaN,
                    "Not measured: a judged reference frame was stale or intruded."),
                Diagnostics: [],
                failures);
        }

        // With nothing to round, A and B must draw at the same place (R1's first assertion), judged
        // on the 21-row average like every other judged value (owner's decision 2026-10-11).
        var referenceOffset = ProbeRingCaptureAnalysis.MeasureMirroredMultiRowShift(
            inputs.ReferenceA, inputs.ReferenceB, inputs.Geometry);
        if (ViolatesTolerance(referenceOffset.MeanX, 0.0) || ViolatesTolerance(referenceOffset.MeanY, 0.0))
        {
            failures.Add(new RequirementFailure("(e) reference A-B",
                "With nothing to round, A and B must draw at the same place, judged on the 21-row average; measured "
                + $"({referenceOffset.MeanX:0.0000}, {referenceOffset.MeanY:0.0000}) px."
                + NonFiniteNote(referenceOffset.MeanX) + NonFiniteNote(referenceOffset.MeanY)));
        }

        RequireSchedule(
            inputs.AssertionRows.Where(row => row.Condition == ConditionA).Select(row => row.Fraction),
            AssertionFractions, "A/B assertion", failures);
        RequireSchedule(
            inputs.AssertionRows.Where(row => row.Condition == ConditionB).Select(row => row.Fraction),
            AssertionFractions, "A/B assertion", failures);
        RequireSchedule(
            inputs.SweepRows.Select(row => row.Fraction), SweepFractions, "sweep", failures);

        var referenceNoise = inputs.ReferenceBRepeats.Count > 1
            ? MeasureRow(
                ConditionB, 0.0, inputs.ReferenceBRepeats, inputs.ReferenceBStaleFlags,
                inputs.ReferenceBIntrudedFlags, inputs.ReferenceB, inputs.Geometry, 0.0, 0.0)
            : null;

        var assertionRows = new List<RowMeasurement>();
        foreach (var row in inputs.AssertionRows)
        {
            var reference = row.Condition == ConditionA ? inputs.ReferenceA : inputs.ReferenceB;
            var expected = row.Condition == ConditionA
                ? Math.Round(row.Fraction, MidpointRounding.AwayFromZero)
                : row.Fraction;
            var measured = MeasureRow(
                row.Condition, row.Fraction, row.Repeats, row.StaleFlags, row.IntrudedFlags, reference,
                inputs.Geometry, expected, expected);
            assertionRows.Add(measured);

            // F1 (fixup 09, review 07 MAJOR): a judged row must hold exactly the scheduled repeats.
            // Zero repeats used to pass vacuously — NaN means, a per-repeat loop over nothing, and a
            // schedule check that only looks at fractions. Nothing may skip the ±0.05 gate by being
            // absent; this also covers an incomplete or edited archive fed to the offline analyser.
            if (measured.Repeats != ProbeRingOnScreenHarness.RowRepeats)
            {
                failures.Add(new RequirementFailure(
                    row.Condition == ConditionB ? "(e) condition B" : "(e) condition A",
                    Invariant(
                        $"The judged {row.Condition} row at f = {row.Fraction:0.00} holds {measured.Repeats} captures, not the scheduled {ProbeRingOnScreenHarness.RowRepeats}: a row without its repeats cannot be judged and must never pass vacuously.")));
            }

            // F1 (orchestrator decision): EVERY repeat must be within ±0.05 — the run-1 rule applied
            // to each capture — now on the 21-row average (owner's decision 2026-10-11, after run 2).
            // A stale or intruded repeat is a failure in its own right and is excluded from the
            // aggregates: it is never passed and never silently dropped. The mean and spread stay in
            // the report.
            for (var i = 0; i < measured.SampleX.Count; i++)
            {
                if (measured.StaleFlags[i])
                {
                    failures.Add(new RequirementFailure(
                        row.Condition == ConditionB ? "(e) condition B" : "(e) condition A",
                        $"Stale frame: condition {row.Condition}'s repeat {i + 1} of {measured.Repeats} at "
                        + $"f = {row.Fraction:0.00} is a byte-identical copy of the previous capture — the screen "
                        + "did not repaint, so this value is the previous placement's. A stale capture is retaken "
                        + "up to 3 times; one that reaches a judged row fails here, never dropped and never passed."));
                    continue;
                }

                if (measured.IntrudedFlags[i])
                {
                    failures.Add(new RequirementFailure(
                        row.Condition == ConditionB ? "(e) condition B" : "(e) condition A",
                        $"Intruded frame: condition {row.Condition}'s repeat {i + 1} of {measured.Repeats} at "
                        + $"f = {row.Fraction:0.00} is off-background outside the ring's footprint — an overlay "
                        + "(e.g. a gaze cursor) covered the capture region, so this value is not the ring alone. "
                        + "It is retaken up to 3 times; one that reaches a judged row fails here (fixup 10)."));
                    continue;
                }

                if (row.Condition == ConditionB)
                {
                    if (ViolatesTolerance(measured.SampleX[i], row.Fraction))
                    {
                        failures.Add(new RequirementFailure("(e) condition B",
                            $"Condition B must render fractionally on the hardware path (x axis): f = {row.Fraction:0.00} px "
                            + $"but repeat {i + 1} of {measured.Repeats} moved ({measured.SampleX[i]:0.0000}, "
                            + $"{measured.SampleY[i]:0.0000}) px."
                            + NonFiniteNote(measured.SampleX[i])
                            + " If either axis is 0 or 1, something snapped the "
                            + "ring to device pixels (m1/m4's shape)."));
                    }

                    if (ViolatesTolerance(measured.SampleY[i], row.Fraction))
                    {
                        failures.Add(new RequirementFailure("(e) condition B",
                            $"Condition B's vertical (y axis) move at f = {row.Fraction:0.00} measured "
                            + $"{measured.SampleY[i]:0.0000} px on repeat {i + 1} of {measured.Repeats}."
                            + NonFiniteNote(measured.SampleY[i])));
                    }
                }
                else
                {
                    if (ViolatesTolerance(measured.SampleX[i], expected))
                    {
                        failures.Add(new RequirementFailure("(e) condition A",
                            $"Condition A must land on whole device pixels (x axis): f = {row.Fraction:0.00} should move "
                            + $"{expected:0} px, but repeat {i + 1} of {measured.Repeats} measured "
                            + $"{measured.SampleX[i]:0.0000}."
                            + NonFiniteNote(measured.SampleX[i])));
                    }

                    if (ViolatesTolerance(measured.SampleY[i], expected))
                    {
                        failures.Add(new RequirementFailure("(e) condition A",
                            $"Condition A's vertical (y axis) move at f = {row.Fraction:0.00} should be {expected:0} px, "
                            + $"but repeat {i + 1} of {measured.Repeats} measured {measured.SampleY[i]:0.0000}."
                            + NonFiniteNote(measured.SampleY[i])));
                    }
                }
            }
        }

        // (f) The fine-fraction sweep: a record of how finely the real display path resolves B, now
        // over the judged 21-row average. It has no pass/fail beyond the schedule check above; a
        // stale repeat is excluded from the row's mean and flagged on its line.
        var sweepRows = inputs.SweepRows
            .Select(row => MeasureRow(
                ConditionB, row.Fraction, row.Repeats, row.StaleFlags, row.IntrudedFlags, inputs.ReferenceB,
                inputs.Geometry, row.Fraction, row.Fraction))
            .ToList();
        var sweepSummary = ProbeRingCaptureAnalysis.EvaluateSweep(
            sweepRows
                .Select(row => new ProbeRingCaptureAnalysis.SweepRow(row.Fraction, row.MeanX, row.MeanY))
                .ToList());

        // Deciders D2/D3 (orchestrator decision): measured and printed with a DIAGNOSTIC label,
        // never judged — their job is to separate a scan-row or dash artefact from a real placement
        // error, which a verdict on them would misstate as evidence about the owner's look.
        var diagnostics = new List<DiagnosticMeasurement>();
        foreach (var group in inputs.Diagnostics)
        {
            foreach (var row in group.Rows)
            {
                // A diagnostic must never cost the judged evidence (fixup 05 P4): a throw here —
                // an empty scan window at some offset row of the 21-row scan — becomes an error entry.
                try
                {
                    diagnostics.Add(new DiagnosticMeasurement(group.Id, group.Label, MeasureRow(
                        ConditionB, row.Fraction, row.Repeats, row.StaleFlags, row.IntrudedFlags,
                        group.Reference, inputs.Geometry, row.Fraction, row.FractionY), null));
                }
                catch (Exception ex)
                {
                    diagnostics.Add(new DiagnosticMeasurement(group.Id, group.Label, null, ex.Message));
                }
            }
        }

        return new JudgeReport(
            dpi, control, referenceOffset, referenceNoise, assertionRows, sweepRows, sweepSummary,
            diagnostics, failures);
    }

    /// <summary>
    /// Prints the measured run — every row, both axes, its repeats' mean and spread, and the
    /// mirrored halves — before any verdict. This is what a failed run must carry now.
    /// </summary>
    /// <param name="report">The measured run.</param>
    /// <param name="writeLine">Receives the report lines.</param>
    internal static void WriteReport(JudgeReport report, Action<string> writeLine)
    {
        writeLine(Invariant(
            $"R2 judged estimator: the 21-row average (ShiftX over rows, ShiftY over columns, centre ±{ProbeRingCaptureAnalysis.JudgedRowRadius}) — owner's decision 2026-10-11, after run 2. The single scan row is printed below as R2 DIAGNOSTIC single-row lines and is never judged."));
        writeLine(report.Dpi.Message);
        writeLine($"R2 positive control: {report.Control.Reason}");
        writeLine(Invariant(
            $"R2 reference A-B offset at f=0: ({report.ReferenceOffset.MeanX:0.0000}, {report.ReferenceOffset.MeanY:0.0000}) px"));

        if (report.ReferenceNoise is not null)
            WriteRow(report.ReferenceNoise, "noise", diagnosticId: null, writeLine);

        foreach (var row in report.AssertionRows)
            WriteRow(row, "row", diagnosticId: null, writeLine);
        foreach (var row in report.SweepRows)
            WriteRow(row, "sweep", diagnosticId: null, writeLine);

        writeLine(Invariant(
            $"R2 sweep summary: max |error| {report.Sweep.MaxAbsErrorPx:0.0000} px; resolves B to {report.Sweep.ResolvedStepPx:0.00} px. {report.Sweep.Note}"));

        // Deciders last, clearly labelled DIAGNOSTIC: they are never judged (orchestrator decision).
        if (report.Diagnostics.Count > 0)
        {
            writeLine(
                "R2 diagnostics follow (D2 solid-look control, D3 X-only motion): measured and printed, never "
                + "judged. Stale and intruded repeats are excluded from their means and flagged with "
                + "stale=N / intruded=N.");
            foreach (var diagnostic in report.Diagnostics)
            {
                if (diagnostic.Row is null)
                {
                    writeLine($"R2 DIAGNOSTIC {diagnostic.Id} failed: {diagnostic.Error}");
                    continue;
                }

                WriteRow(diagnostic.Row, $"DIAGNOSTIC {diagnostic.Id}", diagnostic.Id, writeLine);
            }
        }

        writeLine($"R2 failures so far: {report.Failures.Count} (the verdict follows)");
    }

    /// <summary>
    /// Prints one row, one line per axis, with its repeats and mirrored halves — the judged 21-row
    /// averages — then the single-scan-row values as a diagnostic, never judged.
    /// <paramref name="diagnosticId"/> is set only for the decider rows, so their lines carry the
    /// DIAGNOSTIC label the decision requires. A stale repeat count rides on the row lines as
    /// <c>stale=N</c>.
    /// </summary>
    private static void WriteRow(
        RowMeasurement row, string kind, string? diagnosticId, Action<string> writeLine)
    {
        var samplesX = string.Join(", ", row.SampleX.Select(value => value.ToString("0.0000", CultureInfo.InvariantCulture)));
        var samplesY = string.Join(", ", row.SampleY.Select(value => value.ToString("0.0000", CultureInfo.InvariantCulture)));
        var stale = row.StaleRepeats > 0 ? $" stale={row.StaleRepeats}" : string.Empty;
        var intruded = row.IntrudedRepeats > 0 ? $" intruded={row.IntrudedRepeats}" : string.Empty;

        writeLine(Invariant(
            $"R2 {kind} {row.Condition} f={row.Fraction:0.00} N={row.Repeats}{stale}{intruded} X: expected={row.ExpectedX:0.0000} measured={row.MeanX:0.0000} error={row.MeanX - row.ExpectedX:+0.0000;-0.0000;+0.0000} spread={row.SpreadX:0.0000} samples=({samplesX}) left={row.LeftX:0.0000} right={row.RightX:0.0000}"));
        writeLine(Invariant(
            $"R2 {kind} {row.Condition} f={row.Fraction:0.00} N={row.Repeats}{stale}{intruded} Y: expected={row.ExpectedY:0.0000} measured={row.MeanY:0.0000} error={row.MeanY - row.ExpectedY:+0.0000;-0.0000;+0.0000} spread={row.SpreadY:0.0000} samples=({samplesY}) top={row.TopY:0.0000} bottom={row.BottomY:0.0000}"));

        if (row.SingleRowError is not null)
        {
            writeLine($"R2 DIAGNOSTIC single-row {diagnosticId ?? kind} failed: {row.SingleRowError}");
            return;
        }

        writeLine(Invariant(
            $"R2 DIAGNOSTIC single-row {diagnosticId ?? kind} {row.Condition} f={row.Fraction:0.00} N={row.Repeats} single-row=({row.SingleRowMeanX:0.0000}, {row.SingleRowMeanY:0.0000}) spread=({row.SingleRowSpreadX:0.0000}, {row.SingleRowSpreadY:0.0000})"));
    }

    /// <summary>
    /// Fails once, with a combined summary naming every failed requirement — or returns when the
    /// run is clean. Collecting first is the fixup-03 rule: one failure must not hide the rest.
    /// </summary>
    /// <param name="report">The measured run.</param>
    internal static void Judge(JudgeReport report)
    {
        if (report.Failures.Count == 0)
            return;

        var summary = $"R2 failed {report.Failures.Count} requirement(s):"
            + string.Join(string.Empty, report.Failures.Select(failure => $"\n  [{failure.Requirement}] {failure.Message}"));

        // A plain throw, not NFluent: NFluent renders the whole checked collection into the message,
        // which would re-list every failure even if this summary were truncated. The summary is the
        // payload the fixed "collect all, then judge" behaviour exists for.
        throw new InvalidOperationException(summary);
    }

    /// <summary>
    /// Measures, prints and judges a single-repeat run (the shape the headless tests drive). The
    /// harness uses <see cref="Measure"/>/<see cref="WriteReport"/>/<see cref="Judge(JudgeReport)"/>
    /// separately so its replay rows and archive lines sit between the measurement and the verdict.
    /// </summary>
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
        var report = Measure(InputsFromFrames(
            effectiveScale, expectedPattern, referenceB, referenceA, assertionFrames, sweepFrames, geometry, halfPx));
        WriteReport(report, writeLine);
        Judge(report);
    }

    /// <summary>Wraps the single-repeat frames into the row shape <see cref="Measure"/> consumes.</summary>
    private static JudgeInputs InputsFromFrames(
        double effectiveScale,
        ProbeRaster expectedPattern,
        ProbeRaster referenceB,
        ProbeRaster referenceA,
        IReadOnlyList<AbFrame> assertionFrames,
        IReadOnlyList<SweepFrame> sweepFrames,
        ProbeRingCaptureAnalysis.RingScanGeometry geometry,
        double halfPx)
    {
        var assertionRows = assertionFrames
            .SelectMany(frame => new[]
            {
                new RowSamples(ConditionA, frame.Fraction, [frame.A, frame.A, frame.A]),
                new RowSamples(ConditionB, frame.Fraction, [frame.B, frame.B, frame.B]),
            })
            .ToList();

        var sweepRows = sweepFrames
            .Select(frame => new RowSamples(ConditionB, frame.Fraction, [frame.B]))
            .ToList();

        return new JudgeInputs(
            effectiveScale, expectedPattern, referenceB, referenceA, [referenceB], geometry, halfPx,
            assertionRows, sweepRows, Diagnostics: []);
    }

    /// <summary>
    /// Builds the judgement's inputs from a captured run: the reference frames, the rows grouped by
    /// condition and fraction (their repeats in capture order), and the geometry derived from the
    /// reference capture. Shared by the on-screen harness and the offline analyser.
    /// </summary>
    /// <param name="scale">The window's effective scale.</param>
    /// <param name="expectedPattern">The expected pattern rendered at the reference geometry.</param>
    /// <param name="samples">The captured samples.</param>
    internal static JudgeInputs BuildInputs(
        double scale, ProbeRaster expectedPattern, IReadOnlyList<ProbeRingCapturedSample> samples)
    {
        var referenceB = SamplesAt(samples, KindReference, ConditionB, 0.0).First();
        var referenceA = SamplesAt(samples, KindReference, ConditionA, 0.0).First();
        var halfPx = ProbeRingCaptureScene.RingSizeDip * scale / 2.0;
        var geometry = ProbeRingCaptureAnalysis.RingScanGeometry.For(
            new PhysicalPoint(
                referenceB.Frame.CaptureCentrePx.X - referenceB.Frame.CaptureOriginX,
                referenceB.Frame.CaptureCentrePx.Y - referenceB.Frame.CaptureOriginY),
            halfPx);

        var assertionRows = new List<RowSamples>();
        foreach (var condition in new[] { ConditionA, ConditionB })
        {
            foreach (var fraction in AssertionFractions)
            {
                var rowSamples = SamplesAt(samples, KindAssertion, condition, fraction);
                assertionRows.Add(new RowSamples(
                    condition,
                    fraction,
                    rowSamples.Select(s => s.Weights).ToList(),
                    rowSamples.Select(s => s.Stale).ToList(),
                    rowSamples.Select(s => s.Intruded).ToList()));
            }
        }

        var sweepRows = SweepFractions
            .Select(fraction =>
            {
                var rowSamples = SamplesAt(samples, KindSweep, ConditionB, fraction);
                return new RowSamples(
                    ConditionB,
                    fraction,
                    rowSamples.Select(s => s.Weights).ToList(),
                    rowSamples.Select(s => s.Stale).ToList(),
                    rowSamples.Select(s => s.Intruded).ToList());
            })
            .ToList();

        // D2/D3 (never judged): each block's own f = 0 reference and its rows.
        var diagnostics = new List<DiagnosticGroup>();
        var solidReference = SamplesAt(samples, KindSolidControl, ConditionB, 0.0).FirstOrDefault();
        if (solidReference is not null)
        {
            diagnostics.Add(new DiagnosticGroup(
                "D2", "solid-look B rows (WeightedSandwich)", solidReference.Weights,
                ProbeRingCapturePlan.SolidControlFractions
                    .Select(fraction =>
                    {
                        var rowSamples = SamplesAt(samples, KindSolidControl, ConditionB, fraction);
                        return new DiagnosticRow(
                            fraction, fraction,
                            rowSamples.Select(s => s.Weights).ToList(),
                            rowSamples.Select(s => s.Stale).ToList(),
                            rowSamples.Select(s => s.Intruded).ToList());
                    })
                    .Where(row => row.Repeats.Count > 0)
                    .ToList()));
        }

        var xOnlyReference = SamplesAt(samples, KindXOnly, ConditionB, 0.0).FirstOrDefault();
        if (xOnlyReference is not null)
        {
            diagnostics.Add(new DiagnosticGroup(
                "D3", "X-only B rows (y = 0)", xOnlyReference.Weights,
                ProbeRingCapturePlan.XOnlyFractions
                    .Select(fraction =>
                    {
                        var rowSamples = SamplesAt(samples, KindXOnly, ConditionB, fraction);
                        return new DiagnosticRow(
                            fraction, 0.0,
                            rowSamples.Select(s => s.Weights).ToList(),
                            rowSamples.Select(s => s.Stale).ToList(),
                            rowSamples.Select(s => s.Intruded).ToList());
                    })
                    .Where(row => row.Repeats.Count > 0)
                    .ToList()));
        }

        return new JudgeInputs(
            scale,
            expectedPattern,
            referenceB.Weights,
            referenceA.Weights,
            SamplesAt(samples, KindReference, ConditionB, 0.0).Select(s => s.Weights).ToList(),
            geometry,
            halfPx,
            assertionRows,
            sweepRows,
            diagnostics,
            SamplesAt(samples, KindReference, ConditionB, 0.0).Select(s => s.Stale).ToList(),
            SamplesAt(samples, KindReference, ConditionB, 0.0).Select(s => s.Intruded).ToList(),
            referenceA.Stale,
            referenceA.Intruded);
    }

    /// <summary>The samples of one row kind, condition and fraction, in repeat order.</summary>
    private static IReadOnlyList<ProbeRingCapturedSample> SamplesAt(
        IReadOnlyList<ProbeRingCapturedSample> samples, string kind, string condition, double fraction) =>
        samples
            .Where(s => s.Kind == kind && s.Condition == condition && Math.Abs(s.Fraction - fraction) < 1e-9)
            .OrderBy(s => s.Repeat)
            .ToList();

    /// <summary>
    /// Measures one row over its repeats with the <b>judged</b> estimator — the 21-row mirrored
    /// average (owner's decision 2026-10-11, after run 2) — then the per-repeat sample values, their
    /// mean and spread, and the mean of each judged half. Stale repeats are excluded from every
    /// aggregate and counted; the single-scan-row values are kept as a diagnostic with their own
    /// catch, so a diagnostic throw cannot lose the judged table.
    ///
    /// <para>
    /// <b>The judged path fails loudly.</b> The 21-row measurement runs with no catch: a throw
    /// propagates out of <see cref="Measure"/>, which is the failure. A non-finite value that could
    /// still be produced is caught by <see cref="ViolatesTolerance"/> at every comparison.
    /// </para>
    /// </summary>
    private static RowMeasurement MeasureRow(
        string condition,
        double fraction,
        IReadOnlyList<ProbeRaster> repeats,
        IReadOnlyList<bool>? staleFlags,
        IReadOnlyList<bool>? intrudedFlags,
        ProbeRaster reference,
        ProbeRingCaptureAnalysis.RingScanGeometry geometry,
        double expectedX,
        double expectedY)
    {
        var stale = staleFlags ?? new bool[repeats.Count];
        var intruded = intrudedFlags ?? new bool[repeats.Count];
        bool Excluded(int index) =>
            (index < stale.Count && stale[index]) || (index < intruded.Count && intruded[index]);

        // The judged estimator: the 21-row mirrored average. No catch — a throw here is the loud
        // failure the judged path must produce.
        var judged = repeats
            .Select(frame => ProbeRingCaptureAnalysis.MeasureMirroredMultiRowShift(frame, reference, geometry))
            .ToList();

        // The single row stays in the output as a diagnostic, on lines labelled
        // "R2 DIAGNOSTIC single-row …", with its own catch. That catch is defence in depth: the
        // single row is the 21-row average's centre row, so a single-row throw always also throws
        // on the judged path first (fixup 09 nit 3) — a mutant that rethrows here is an equivalent
        // mutant, and a later reviewer should not chase it.
        List<ProbeRingCaptureAnalysis.MirroredShift> singleRow = [];
        string? singleRowError = null;
        try
        {
            singleRow = repeats
                .Select(frame => ProbeRingCaptureAnalysis.MeasureMirroredShift(frame, reference, geometry))
                .ToList();
        }
        catch (Exception ex)
        {
            singleRowError = ex.Message;
        }

        var staleCount = judged.Where((_, index) => index < stale.Count && stale[index]).Count();
        var intrudedCount = judged.Where((_, index) => index < intruded.Count && intruded[index]).Count();
        var kept = judged
            .Where((_, index) => !Excluded(index))
            .ToList();

        // The diagnostic excludes stale and intruded repeats exactly as the judged line does
        // (fixup 09 nit 2, fixup 10 item 2): a stale single-row value is the previous frame's and an
        // intruded one carries the overlay, and neither must distort the printed spread.
        var keptSingle = singleRow
            .Where((_, index) => !Excluded(index))
            .ToList();

        return new RowMeasurement(
            condition,
            fraction,
            repeats.Count,
            staleCount,
            intrudedCount,
            expectedX,
            expectedY,
            kept.Count > 0 ? kept.Average(shift => shift.MeanX) : double.NaN,
            kept.Count > 0 ? kept.Average(shift => shift.MeanY) : double.NaN,
            kept.Count > 0 ? kept.Max(shift => shift.MeanX) - kept.Min(shift => shift.MeanX) : double.NaN,
            kept.Count > 0 ? kept.Max(shift => shift.MeanY) - kept.Min(shift => shift.MeanY) : double.NaN,
            kept.Count > 0 ? kept.Average(shift => shift.LeftX) : double.NaN,
            kept.Count > 0 ? kept.Average(shift => shift.RightX) : double.NaN,
            kept.Count > 0 ? kept.Average(shift => shift.TopY) : double.NaN,
            kept.Count > 0 ? kept.Average(shift => shift.BottomY) : double.NaN,
            judged.Select(shift => shift.MeanX).ToList(),
            judged.Select(shift => shift.MeanY).ToList(),
            [.. stale],
            [.. intruded],
            keptSingle.Count > 0 ? keptSingle.Average(shift => shift.MeanX) : double.NaN,
            keptSingle.Count > 0 ? keptSingle.Average(shift => shift.MeanY) : double.NaN,
            keptSingle.Count > 1
                ? keptSingle.Max(shift => shift.MeanX) - keptSingle.Min(shift => shift.MeanX)
                : double.NaN,
            keptSingle.Count > 1
                ? keptSingle.Max(shift => shift.MeanY) - keptSingle.Min(shift => shift.MeanY)
                : double.NaN,
            singleRowError);
    }

    /// <summary>
    /// Whether a judged value must fail: not a finite number at all, or off by more than
    /// <see cref="TolerancePx"/>. The finiteness arm is load-bearing (fixup 08 item 1):
    /// <c>Math.Abs(NaN − f) &gt; tolerance</c> is <c>false</c>, so a bare comparison would let a
    /// NaN judged value pass silently.
    /// </summary>
    /// <param name="judgedValue">The measured value the judgement reads.</param>
    /// <param name="expected">The value it must be within <see cref="TolerancePx"/> of.</param>
    internal static bool ViolatesTolerance(double judgedValue, double expected) =>
        !double.IsFinite(judgedValue) || Math.Abs(judgedValue - expected) > TolerancePx;

    /// <summary>The sentence a non-finite judged value appends to its failure message.</summary>
    private static string NonFiniteNote(double value) =>
        double.IsFinite(value)
            ? string.Empty
            : $" This judged (21-row) value is {value} — non-finite, so it can never be inside the "
            + $"±{TolerancePx:0.00} px tolerance and must not pass silently.";

    /// <summary>
    /// The captured rows must be exactly the scheduled ones. This is what makes a shortened or
    /// coarsened sweep a collected failure instead of a thinner record (O9/O10).
    /// </summary>
    private static void RequireSchedule(
        IEnumerable<double> actual,
        IReadOnlyList<double> scheduled,
        string name,
        List<RequirementFailure> failures)
    {
        var list = actual.ToList();
        if (list.Count != scheduled.Count)
        {
            failures.Add(new RequirementFailure($"({name} schedule)",
                $"The harness captured {list.Count} {name} frames, not the scheduled {scheduled.Count}. "
                + "The sweep is a record, but it must be the scheduled record."));
            return;
        }

        for (var i = 0; i < scheduled.Count; i++)
        {
            if (list[i] != scheduled[i])
            {
                failures.Add(new RequirementFailure($"({name} schedule)",
                    $"The {name} frame {i} carries fraction {list[i]}, not the scheduled {scheduled[i]}."));
            }
        }
    }

    private static string Invariant(FormattableString message) => FormattableString.Invariant(message);
}
