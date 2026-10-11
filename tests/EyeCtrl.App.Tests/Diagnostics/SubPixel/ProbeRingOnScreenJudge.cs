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
    internal sealed record RowSamples(string Condition, double Fraction, IReadOnlyList<ProbeRaster> Repeats);

    /// <summary>One diagnostic row: its own reference and its repeats (fixup 04 deciders).</summary>
    /// <param name="Fraction">The scheduled x fraction.</param>
    /// <param name="FractionY">The scheduled y fraction (0 for the X-only decider).</param>
    /// <param name="Repeats">The frames, one per repeat.</param>
    internal sealed record DiagnosticRow(double Fraction, double FractionY, IReadOnlyList<ProbeRaster> Repeats);

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
        IReadOnlyList<DiagnosticGroup> Diagnostics);

    /// <summary>One failed requirement, named, with the message the fix must read.</summary>
    /// <param name="Requirement">The requirement's label, e.g. "(c) positive control".</param>
    /// <param name="Message">The full explanation.</param>
    internal sealed record RequirementFailure(string Requirement, string Message);

    /// <summary>One row's measured values, aggregated over its repeats.</summary>
    /// <param name="Condition">"A" or "B".</param>
    /// <param name="Fraction">The scheduled fraction.</param>
    /// <param name="Repeats">How many captures were aggregated.</param>
    /// <param name="ExpectedX">The expected x movement.</param>
    /// <param name="ExpectedY">The expected y movement.</param>
    /// <param name="MeanX">The mean measured x movement over the repeats.</param>
    /// <param name="MeanY">The mean measured y movement over the repeats.</param>
    /// <param name="SpreadX">max − min of the per-repeat x movements (the noise).</param>
    /// <param name="SpreadY">max − min of the per-repeat y movements.</param>
    /// <param name="LeftX">Mean of the left edge block's centroid shifts.</param>
    /// <param name="RightX">Mean of the right edge block's centroid shifts.</param>
    /// <param name="TopY">Mean of the top edge block's centroid shifts.</param>
    /// <param name="BottomY">Mean of the bottom edge block's centroid shifts.</param>
    /// <param name="SampleX">Every repeat's x movement, in capture order.</param>
    /// <param name="SampleY">Every repeat's y movement, in capture order.</param>
    /// <param name="MultiRowMeanX">D1: the mean over repeats of the 21-row cardinal estimate, x.</param>
    /// <param name="MultiRowMeanY">D1: the mean over repeats of the 21-column cardinal estimate, y.</param>
    internal sealed record RowMeasurement(
        string Condition,
        double Fraction,
        int Repeats,
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
        double MultiRowMeanX,
        double MultiRowMeanY);

    /// <summary>One diagnostic row's measurement, labelled for the output (never judged).</summary>
    /// <param name="Id">"D1" / "D2" / "D3".</param>
    /// <param name="Label">What the diagnostic is for.</param>
    /// <param name="Row">The measured row.</param>
    internal sealed record DiagnosticMeasurement(string Id, string Label, RowMeasurement Row);

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
            failures.Add(new RequirementFailure("(c) positive control", control.Reason));

        // With nothing to round, A and B must draw at the same place (R1's first assertion).
        var referenceOffset = ProbeRingCaptureAnalysis.MeasureMirroredShift(
            inputs.ReferenceA, inputs.ReferenceB, inputs.Geometry);
        if (Math.Abs(referenceOffset.MeanX) > TolerancePx || Math.Abs(referenceOffset.MeanY) > TolerancePx)
        {
            failures.Add(new RequirementFailure("(e) reference A-B",
                "With nothing to round, A and B must draw at the same place; measured "
                + $"({referenceOffset.MeanX:0.0000}, {referenceOffset.MeanY:0.0000}) px."));
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
            ? MeasureRow(ConditionB, 0.0, inputs.ReferenceBRepeats, inputs.ReferenceB, inputs.Geometry, 0.0, 0.0)
            : null;

        var assertionRows = new List<RowMeasurement>();
        foreach (var row in inputs.AssertionRows)
        {
            var reference = row.Condition == ConditionA ? inputs.ReferenceA : inputs.ReferenceB;
            var expected = row.Condition == ConditionA
                ? Math.Round(row.Fraction, MidpointRounding.AwayFromZero)
                : row.Fraction;
            var measured = MeasureRow(row.Condition, row.Fraction, row.Repeats, reference, inputs.Geometry, expected, expected);
            assertionRows.Add(measured);

            // F1 (orchestrator decision): EVERY repeat must be within ±0.05 — the run-1 rule applied
            // to each capture. The mean and spread stay in the report; judging on the mean would
            // loosen the pre-registered gate after a failure.
            for (var i = 0; i < measured.SampleX.Count; i++)
            {
                if (row.Condition == ConditionB)
                {
                    if (Math.Abs(measured.SampleX[i] - row.Fraction) > TolerancePx)
                    {
                        failures.Add(new RequirementFailure("(e) condition B",
                            $"Condition B must render fractionally on the hardware path: f = {row.Fraction:0.00} px "
                            + $"but repeat {i + 1} of {measured.Repeats} moved ({measured.SampleX[i]:0.0000}, "
                            + $"{measured.SampleY[i]:0.0000}) px. If either axis is 0 or 1, something snapped the "
                            + "ring to device pixels (m1/m4's shape)."));
                    }

                    if (Math.Abs(measured.SampleY[i] - row.Fraction) > TolerancePx)
                    {
                        failures.Add(new RequirementFailure("(e) condition B",
                            $"Condition B's vertical move at f = {row.Fraction:0.00} measured "
                            + $"{measured.SampleY[i]:0.0000} px on repeat {i + 1} of {measured.Repeats}."));
                    }
                }
                else
                {
                    if (Math.Abs(measured.SampleX[i] - expected) > TolerancePx)
                    {
                        failures.Add(new RequirementFailure("(e) condition A",
                            $"Condition A must land on whole device pixels: f = {row.Fraction:0.00} should move "
                            + $"{expected:0} px, but repeat {i + 1} of {measured.Repeats} measured "
                            + $"{measured.SampleX[i]:0.0000}."));
                    }

                    if (Math.Abs(measured.SampleY[i] - expected) > TolerancePx)
                    {
                        failures.Add(new RequirementFailure("(e) condition A",
                            $"Condition A's vertical move at f = {row.Fraction:0.00} should be {expected:0} px, "
                            + $"but repeat {i + 1} of {measured.Repeats} measured {measured.SampleY[i]:0.0000}."));
                    }
                }
            }
        }

        // (f) The fine-fraction sweep: a record of how finely the real display path resolves B. It
        // has no pass/fail beyond the schedule check above.
        var sweepRows = inputs.SweepRows
            .Select(row => MeasureRow(
                ConditionB, row.Fraction, row.Repeats, inputs.ReferenceB, inputs.Geometry, row.Fraction, row.Fraction))
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
                diagnostics.Add(new DiagnosticMeasurement(group.Id, group.Label, MeasureRow(
                    ConditionB, row.Fraction, row.Repeats, group.Reference, inputs.Geometry, row.Fraction, row.FractionY)));
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
                "R2 diagnostics follow (D1 multi-row cardinal estimator, D2 solid-look control, D3 X-only "
                + "motion): measured and printed, never judged.");
            foreach (var diagnostic in report.Diagnostics)
                WriteRow(diagnostic.Row, $"DIAGNOSTIC {diagnostic.Id}", diagnostic.Id, writeLine);
        }

        writeLine($"R2 failures so far: {report.Failures.Count} (the verdict follows)");
    }

    /// <summary>
    /// Prints one row, one line per axis, with its repeats and mirrored halves, then the D1
    /// multi-row comparison. <paramref name="diagnosticId"/> is set only for the decider rows, so
    /// their lines carry the DIAGNOSTIC label the decision requires.
    /// </summary>
    private static void WriteRow(
        RowMeasurement row, string kind, string? diagnosticId, Action<string> writeLine)
    {
        var samplesX = string.Join(", ", row.SampleX.Select(value => value.ToString("0.0000", CultureInfo.InvariantCulture)));
        var samplesY = string.Join(", ", row.SampleY.Select(value => value.ToString("0.0000", CultureInfo.InvariantCulture)));

        writeLine(Invariant(
            $"R2 {kind} {row.Condition} f={row.Fraction:0.00} N={row.Repeats} X: expected={row.ExpectedX:0.0000} measured={row.MeanX:0.0000} error={row.MeanX - row.ExpectedX:+0.0000;-0.0000;+0.0000} spread={row.SpreadX:0.0000} samples=({samplesX}) left={row.LeftX:0.0000} right={row.RightX:0.0000}"));
        writeLine(Invariant(
            $"R2 {kind} {row.Condition} f={row.Fraction:0.00} N={row.Repeats} Y: expected={row.ExpectedY:0.0000} measured={row.MeanY:0.0000} error={row.MeanY - row.ExpectedY:+0.0000;-0.0000;+0.0000} spread={row.SpreadY:0.0000} samples=({samplesY}) top={row.TopY:0.0000} bottom={row.BottomY:0.0000}"));
        writeLine(Invariant(
            $"R2 DIAGNOSTIC D1 {(diagnosticId ?? kind)} {row.Condition} f={row.Fraction:0.00} N={row.Repeats} single-row=({row.MeanX:0.0000}, {row.MeanY:0.0000}) 21-row=({row.MultiRowMeanX:0.0000}, {row.MultiRowMeanY:0.0000})"));
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
                new RowSamples(ConditionA, frame.Fraction, [frame.A]),
                new RowSamples(ConditionB, frame.Fraction, [frame.B]),
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
                assertionRows.Add(new RowSamples(
                    condition,
                    fraction,
                    SamplesAt(samples, KindAssertion, condition, fraction).Select(s => s.Weights).ToList()));
            }
        }

        var sweepRows = SweepFractions
            .Select(fraction => new RowSamples(
                ConditionB,
                fraction,
                SamplesAt(samples, KindSweep, ConditionB, fraction).Select(s => s.Weights).ToList()))
            .ToList();

        // D2/D3 (never judged): each block's own f = 0 reference and its rows.
        var diagnostics = new List<DiagnosticGroup>();
        var solidReference = SamplesAt(samples, KindSolidControl, ConditionB, 0.0).FirstOrDefault();
        if (solidReference is not null)
        {
            diagnostics.Add(new DiagnosticGroup(
                "D2", "solid-look B rows (WeightedSandwich)", solidReference.Weights,
                ProbeRingCapturePlan.SolidControlFractions
                    .Select(fraction => new DiagnosticRow(
                        fraction, fraction,
                        SamplesAt(samples, KindSolidControl, ConditionB, fraction).Select(s => s.Weights).ToList()))
                    .Where(row => row.Repeats.Count > 0)
                    .ToList()));
        }

        var xOnlyReference = SamplesAt(samples, KindXOnly, ConditionB, 0.0).FirstOrDefault();
        if (xOnlyReference is not null)
        {
            diagnostics.Add(new DiagnosticGroup(
                "D3", "X-only B rows (y = 0)", xOnlyReference.Weights,
                ProbeRingCapturePlan.XOnlyFractions
                    .Select(fraction => new DiagnosticRow(
                        fraction, 0.0,
                        SamplesAt(samples, KindXOnly, ConditionB, fraction).Select(s => s.Weights).ToList()))
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
            diagnostics);
    }

    /// <summary>The samples of one row kind, condition and fraction, in repeat order.</summary>
    private static IReadOnlyList<ProbeRingCapturedSample> SamplesAt(
        IReadOnlyList<ProbeRingCapturedSample> samples, string kind, string condition, double fraction) =>
        samples
            .Where(s => s.Kind == kind && s.Condition == condition && Math.Abs(s.Fraction - fraction) < 1e-9)
            .OrderBy(s => s.Repeat)
            .ToList();

    /// <summary>
    /// Measures one row over its repeats: the per-repeat mirrored shifts, then their mean and
    /// spread, and the mean of each mirrored half.
    /// </summary>
    private static RowMeasurement MeasureRow(
        string condition,
        double fraction,
        IReadOnlyList<ProbeRaster> repeats,
        ProbeRaster reference,
        ProbeRingCaptureAnalysis.RingScanGeometry geometry,
        double expectedX,
        double expectedY)
    {
        var shifts = repeats
            .Select(frame => ProbeRingCaptureAnalysis.MeasureMirroredShift(frame, reference, geometry))
            .ToList();
        var multiRow = repeats
            .Select(frame => ProbeRingCaptureAnalysis.MeasureMultiRowShift(frame, reference, geometry))
            .ToList();

        return new RowMeasurement(
            condition,
            fraction,
            shifts.Count,
            expectedX,
            expectedY,
            shifts.Average(shift => shift.MeanX),
            shifts.Average(shift => shift.MeanY),
            shifts.Max(shift => shift.MeanX) - shifts.Min(shift => shift.MeanX),
            shifts.Max(shift => shift.MeanY) - shifts.Min(shift => shift.MeanY),
            shifts.Average(shift => shift.LeftX),
            shifts.Average(shift => shift.RightX),
            shifts.Average(shift => shift.TopY),
            shifts.Average(shift => shift.BottomY),
            shifts.Select(shift => shift.MeanX).ToList(),
            shifts.Select(shift => shift.MeanY).ToList(),
            multiRow.Average(shift => shift.MeanX),
            multiRow.Average(shift => shift.MeanY));
    }

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
