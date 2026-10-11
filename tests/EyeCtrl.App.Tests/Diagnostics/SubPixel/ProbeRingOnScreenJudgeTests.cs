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

    /// <summary>A ring rendered with independent per-axis capture-local fractions (the Y-only fixtures).</summary>
    private static ProbeRaster RingAtAxis(double fractionX, double fractionY, bool snap) =>
        ProbeRingCaptureScene.WeightsAt(new PhysicalPoint(300 + fractionX, 300 + fractionY), snap);

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
    /// K2 (review 02 F1) and fixup 08 item 1's converse control: B off by 0.1 px must fail at the
    /// stated ±0.05. Every row of the frame is genuinely shifted by +0.1 (the whole render moves),
    /// so the 21-row average reads the shift undiluted — the new judged estimator must not blunt
    /// the test. Kills J1/J2, J5 and any mutant that neuters the judged comparison.
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

    // ── fixup 03: collect-then-judge, the full table, repeats and the noise floor ──

    /// <summary>
    /// The single-frame inputs wrapped into the row shape <see cref="Measure"/> consumes. Each
    /// judged row carries the scheduled three repeats (fixup 09 F1: a judged row must hold
    /// <see cref="ProbeRingOnScreenHarness.RowRepeats"/> captures; the three are the same frame, so
    /// the measured values are unchanged).
    /// </summary>
    private static ProbeRingOnScreenJudge.JudgeInputs Inputs(
        double scale,
        ProbeRaster expected,
        ProbeRaster referenceB,
        ProbeRaster referenceA,
        ProbeRingOnScreenJudge.AbFrame[] assertions,
        ProbeRingOnScreenJudge.SweepFrame[] sweep,
        ProbeRaster[]? referenceRepeats = null,
        ProbeRingOnScreenJudge.DiagnosticGroup[]? diagnostics = null)
    {
        var assertionRows = assertions
            .SelectMany(frame => new[]
            {
                new ProbeRingOnScreenJudge.RowSamples(
                    ProbeRingOnScreenJudge.ConditionA, frame.Fraction, [frame.A, frame.A, frame.A]),
                new ProbeRingOnScreenJudge.RowSamples(
                    ProbeRingOnScreenJudge.ConditionB, frame.Fraction, [frame.B, frame.B, frame.B]),
            })
            .ToList();
        var sweepRows = sweep
            .Select(frame => new ProbeRingOnScreenJudge.RowSamples(
                ProbeRingOnScreenJudge.ConditionB, frame.Fraction, [frame.B]))
            .ToList();

        return new ProbeRingOnScreenJudge.JudgeInputs(
            scale, expected, referenceB, referenceA, referenceRepeats ?? [referenceB], Geometry, HalfPx,
            assertionRows, sweepRows, diagnostics ?? []);
    }

    /// <summary>
    /// Fixup 03 item 1: two independent failures are <b>both</b> collected and both appear in the
    /// final summary. Kills the "stop at the first failure" mutant — the run-1 lesson.
    /// </summary>
    [StaFact]
    public void Measure_CollectsEveryFailure_AndJudgingReportsThemAll()
    {
        var (expected, referenceB, _, _, sweep) = HealthyInputs();

        // Failure 1: condition B snapped (m4's shape). Failure 2: the references disagree.
        var snappedB = ProbeRingOnScreenJudge.AssertionFractions
            .Select(fraction => new ProbeRingOnScreenJudge.AbFrame(
                fraction, RingAt(fraction, snap: true), RingAt(fraction, snap: true)))
            .ToArray();
        var referenceAOnePixelOff = RingAt(1.0, snap: true);

        var report = ProbeRingOnScreenJudge.Measure(
            Inputs(2.0, expected, referenceB, referenceAOnePixelOff, snappedB, sweep));

        Check.WithCustomMessage(
                "Both independent failures must be collected; a report that stops at the first one "
                + "hides the table the owner needs.")
            .That(report.Failures.Count).IsStrictlyGreaterThan(1);
        Check.WithCustomMessage("The collected failures must include condition B's.")
            .That(report.Failures.Any(failure => failure.Message.Contains("Condition B"))).IsTrue();
        Check.WithCustomMessage("The collected failures must include the reference A-B one.")
            .That(report.Failures.Any(failure => failure.Message.Contains("With nothing to round"))).IsTrue();

        var combined = FailureMessage(() => ProbeRingOnScreenJudge.Judge(report));
        Check.WithCustomMessage("Judge(report) must fail once with every collected failure in the summary.")
            .That(combined).IsNotNull();
        Check.That(combined!).Contains("Condition B");
        Check.That(combined!).Contains("With nothing to round");
    }

    /// <summary>
    /// Fixup 05 P4: a diagnostic whose scan window is empty must not throw out of Measure. It
    /// becomes an error entry — printed as "R2 DIAGNOSTIC … failed: …" — and the judged table and
    /// the verdict are untouched.
    /// </summary>
    [StaFact]
    public void EmptyDiagnosticWindow_LeavesTheJudgedTableAndVerdictIntact()
    {
        var (expected, referenceB, referenceA, assertions, sweep) = HealthyInputs();
        var blank = ProbeRaster.FromWeights(new byte[600 * 600], 600, 600);

        var diagnostic = new ProbeRingOnScreenJudge.DiagnosticGroup(
            "D2",
            "solid-look B rows (WeightedSandwich)",
            blank,
            [new ProbeRingOnScreenJudge.DiagnosticRow(0.25, 0.25, [blank])]);

        var report = ProbeRingOnScreenJudge.Measure(
            Inputs(2.0, expected, referenceB, referenceA, assertions, sweep, diagnostics: [diagnostic]));

        Check.WithCustomMessage("The empty diagnostic must not add a failure.")
            .That(report.Failures).IsEmpty();
        Check.WithCustomMessage("The judged rows must all still be measured.")
            .That(report.AssertionRows).HasSize(ProbeRingOnScreenJudge.AssertionFractions.Count * 2);

        var d2 = report.Diagnostics.Single();
        Check.That(d2.Row).IsNull();
        Check.WithCustomMessage("The failed diagnostic must carry its error message.").That(d2.Error).IsNotNull();

        var lines = new List<string>();
        ProbeRingOnScreenJudge.WriteReport(report, lines.Add);
        Check.WithCustomMessage("The failed diagnostic must print with the DIAGNOSTIC label.")
            .That(lines.Any(line => line.Contains("R2 DIAGNOSTIC D2 failed:"))).IsTrue();
        Check.WithCustomMessage("The judged table must still print its rows.")
            .That(lines.Any(line => line.Contains("row B f=0.25"))).IsTrue();

        // And the verdict itself is intact: the same report passes.
        ProbeRingOnScreenJudge.Judge(report);
    }

    /// <summary>
    /// Fixup 05 P4, the 21-row shape (fixup 08): a diagnostic whose judged 21-row scan is empty
    /// must not throw out of Measure. The fixture cuts a band of rows below the scan row, so the
    /// D2 diagnostic's 21-row measurement throws; it becomes an error entry — printed as
    /// "R2 DIAGNOSTIC D2 failed: …" — and the judged rows and the verdict are untouched. The judged
    /// path itself has no catch (owner's decision 2026-10-11); the containment pinned here is the
    /// diagnostic loop's.
    /// </summary>
    [StaFact]
    public void DiagnosticWhoseJudgedScanIsEmpty_LeavesTheJudgedRowsAndVerdictIntact()
    {
        var (expected, referenceB, referenceA, assertions, sweep) = HealthyInputs();
        var cut = CutRows(RingAt(0.25, snap: false), fromRow: 305, toRow: 315);

        var diagnostic = new ProbeRingOnScreenJudge.DiagnosticGroup(
            "D2",
            "solid-look B rows (WeightedSandwich)",
            referenceB,
            [new ProbeRingOnScreenJudge.DiagnosticRow(0.25, 0.25, [cut])]);

        var report = ProbeRingOnScreenJudge.Measure(
            Inputs(2.0, expected, referenceB, referenceA, assertions, sweep, diagnostics: [diagnostic]));

        var entry = report.Diagnostics.Single();
        Check.WithCustomMessage(
                "The diagnostic's judged 21-row scan hits the cut rows; the row must degrade to an error entry.")
            .That(entry.Row).IsNull();
        Check.WithCustomMessage("The failed diagnostic must carry its error message.").That(entry.Error).IsNotNull();

        Check.WithCustomMessage("The judged rows must be intact and judged as before.")
            .That(report.Failures).IsEmpty();

        var lines = new List<string>();
        ProbeRingOnScreenJudge.WriteReport(report, lines.Add);
        Check.WithCustomMessage("The failed diagnostic must print with the DIAGNOSTIC label.")
            .That(lines.Any(line => line.Contains("R2 DIAGNOSTIC D2 failed:"))).IsTrue();

        ProbeRingOnScreenJudge.Judge(report);
    }

    /// <summary>Copies a raster and zeroes every pixel in rows [fromRow, toRow].</summary>
    private static ProbeRaster CutRows(ProbeRaster source, int fromRow, int toRow)
    {
        var pixels = (byte[])source.Pixels.Clone();
        for (var y = fromRow; y <= toRow; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var p = (y * source.Width + x) * 4;
                pixels[p] = 0;
                pixels[p + 1] = 0;
                pixels[p + 2] = 0;
                pixels[p + 3] = 0;
            }
        }

        return new ProbeRaster(pixels, source.Width, source.Height);
    }

    /// <summary>
    /// Fixup 03 items 2-4: the report carries a per-axis line for every row, with expected,
    /// measured, error, spread, the repeat samples and the mirrored halves. Kills the "report only
    /// X" mutant.
    /// </summary>
    [StaFact]
    public void WriteReport_PrintsBothAxesWithRepeatsAndHalves()
    {
        var (expected, referenceB, referenceA, assertions, sweep) = HealthyInputs();
        var inputs = Inputs(2.0, expected, referenceB, referenceA, assertions, sweep);
        inputs = inputs with
        {
            AssertionRows = inputs.AssertionRows
                .Select(row => row.Condition == ProbeRingOnScreenJudge.ConditionB
                               && Math.Abs(row.Fraction - 0.5) < 1e-9
                    ? row with { Repeats = [RingAt(0.5, snap: false), RingAt(0.5, snap: false), RingAt(0.5, snap: false)] }
                    : row)
                .ToList(),
        };

        var report = ProbeRingOnScreenJudge.Measure(inputs);
        var lines = new List<string>();
        ProbeRingOnScreenJudge.WriteReport(report, lines.Add);

        var rowLines = lines
            .Where(line => line.Contains("row B f=0.50") && (line.Contains(" X: ") || line.Contains(" Y: ")))
            .ToList();
        Check.WithCustomMessage("One row must produce one line per axis.")
            .That(rowLines).HasSize(2);
        Check.WithCustomMessage(
                "The X line must carry expected, measured, error, spread, the samples and the left/right halves; "
                + $"got: {string.Join(" | ", rowLines)}")
            .That(rowLines.Any(line => line.Contains(" X: ") && line.Contains("expected=") && line.Contains("measured=")
                                       && line.Contains("error=") && line.Contains("spread=") && line.Contains("samples=(")
                                       && line.Contains("left=") && line.Contains("right="))).IsTrue();
        Check.WithCustomMessage(
                "The Y line must carry the top/bottom halves too (reporting only X is the mutant this kills).")
            .That(rowLines.Any(line => line.Contains(" Y: ") && line.Contains("top=") && line.Contains("bottom="))).IsTrue();
        Check.WithCustomMessage("The repeated row must print its repeat count.")
            .That(rowLines.All(line => line.Contains("N=3"))).IsTrue();
        Check.WithCustomMessage("The report must print the failure count before the verdict.")
            .That(lines.Any(line => line.Contains("R2 failures so far:"))).IsTrue();
        Check.WithCustomMessage("The report must name the judged estimator (the 21-row average).")
            .That(lines.Any(line => line.Contains("R2 judged estimator: the 21-row average"))).IsTrue();
        Check.WithCustomMessage(
                "The report must print the single scan row as a DIAGNOSTIC line beside the judged values.")
            .That(lines.Any(line => line.Contains("R2 DIAGNOSTIC single-row") && line.Contains("single-row=")))
            .IsTrue();
    }

    /// <summary>
    /// Fixup 03 item 3 + fixup 04 F1: a row's measured value is the <b>mean of its repeats</b> and
    /// its spread the max − min over them — but the <b>judged</b> rule is every repeat within ±0.05
    /// (the orchestrator's decision). The pre-fixup fixture (0.25 / 0.5 / 0.75 at f = 0.5) passed on
    /// its mean; it must now fail, per repeat, while the mean and spread stay in the report.
    /// </summary>
    [StaFact]
    public void TableRows_ReportMeanAndSpread_ButEveryRepeatMustPass()
    {
        var (expected, referenceB, referenceA, assertions, sweep) = HealthyInputs();
        var inputs = Inputs(2.0, expected, referenceB, referenceA, assertions, sweep);

        // The B f=0.5 row's three repeats are rendered at 0.25 / 0.5 / 0.75, so the mean is 0.5 and
        // the spread is 0.5 — visible only if every repeat is aggregated.
        inputs = inputs with
        {
            AssertionRows = inputs.AssertionRows
                .Select(row => row.Condition == ProbeRingOnScreenJudge.ConditionB
                               && Math.Abs(row.Fraction - 0.5) < 1e-9
                    ? row with
                    {
                        Repeats =
                        [
                            RingAt(0.25, snap: false),
                            RingAt(0.5, snap: false),
                            RingAt(0.75, snap: false),
                        ],
                    }
                    : row)
                .ToList(),
        };

        var report = ProbeRingOnScreenJudge.Measure(inputs);
        var row = report.AssertionRows.Single(candidate =>
            candidate.Condition == ProbeRingOnScreenJudge.ConditionB && Math.Abs(candidate.Fraction - 0.5) < 1e-9);

        Check.WithCustomMessage("Every repeat must be aggregated, not just the first.")
            .That(row.Repeats).IsEqualTo(3);
        Check.WithCustomMessage($"The row's mean must average its repeats; got {row.MeanX:0.0000}.")
            .That(row.MeanX).IsCloseTo(0.5, 0.02);
        Check.WithCustomMessage($"The row's spread must be max − min over its repeats; got {row.SpreadX:0.0000}.")
            .That(row.SpreadX).IsCloseTo(0.5, 0.02);
        Check.That(row.SampleX).HasSize(3);
        Check.WithCustomMessage("The halves must average every repeat: (left + right) / 2 == the mean.")
            .That((row.LeftX + row.RightX) / 2.0).IsCloseTo(row.MeanX, 1e-9);

        // F1: the row's mean (0.5) is exactly its scheduled fraction, and it must STILL fail — the
        // pre-registered rule is per capture.
        Check.WithCustomMessage(
                "A row whose mean passes but whose repeats do not must fail under the per-repeat rule; "
                + "judging on the mean would loosen the gate after run 1.")
            .That(report.Failures.Count(failure => failure.Message.Contains("Condition B")))
            .IsStrictlyGreaterThan(0);
    }

    /// <summary>
    /// F1's minimal shape over every position the strict rule must see (fixup 05 P1): the bad
    /// repeat may be first, second or last, in either condition, on either axis — off by 0.12 px on
    /// that axis only, the other two repeats exact, so the mean stays inside ±0.05. The failure
    /// must name the condition, the axis and "repeat k of 3". Kills F1c (A judged on the mean),
    /// F1e (only the last repeat judged) and every skip-one variant.
    /// </summary>
    [StaTheory]
    [InlineData(0, "A", true)]
    [InlineData(1, "A", true)]
    [InlineData(2, "A", true)]
    [InlineData(0, "A", false)]
    [InlineData(1, "A", false)]
    [InlineData(2, "A", false)]
    [InlineData(0, "B", true)]
    [InlineData(1, "B", true)]
    [InlineData(2, "B", true)]
    [InlineData(0, "B", false)]
    [InlineData(1, "B", false)]
    [InlineData(2, "B", false)]
    public void Judge_FailsWhenOneRepeatMisses(int badIndex, string condition, bool xAxis)
    {
        var (expected, referenceB, referenceA, assertions, sweep) = HealthyInputs();
        var inputs = Inputs(2.0, expected, referenceB, referenceA, assertions, sweep);

        const double fraction = 0.25;
        var wornBase = condition == ProbeRingOnScreenJudge.ConditionA
            ? Math.Round(fraction, MidpointRounding.AwayFromZero)
            : fraction;

        ProbeRaster Repeat(int repeat)
        {
            var offX = xAxis && repeat == badIndex ? 0.12 : 0.0;
            var offY = !xAxis && repeat == badIndex ? 0.12 : 0.0;

            // Condition A's exact repeats snap (whole pixels); the bad repeat is fractional on its
            // axis — the misplacement the judge must see. (A snapped capture cannot be 0.12 px off,
            // so the bad frame is deliberately unsnapped.)
            var snap = condition == ProbeRingOnScreenJudge.ConditionA && offX == 0.0 && offY == 0.0;
            return RingAtAxis(wornBase + offX, wornBase + offY, snap);
        }

        inputs = inputs with
        {
            AssertionRows = inputs.AssertionRows
                .Where(existing => !(existing.Condition == condition && Math.Abs(existing.Fraction - fraction) < 1e-9))
                .Append(new ProbeRingOnScreenJudge.RowSamples(condition, fraction, [Repeat(0), Repeat(1), Repeat(2)]))
                .ToList(),
        };

        var report = ProbeRingOnScreenJudge.Measure(inputs);

        var conditionText = condition == ProbeRingOnScreenJudge.ConditionA ? "Condition A" : "Condition B";
        var axisText = xAxis ? "x axis" : "y axis";
        Check.WithCustomMessage(
                $"A bad repeat {badIndex + 1} of 3 in {condition} on the {axisText} must fail and say so; "
                + $"failures: {string.Join(" | ", report.Failures.Select(failure => failure.Message))}")
            .That(report.Failures.Any(failure =>
                failure.Message.Contains(conditionText)
                && failure.Message.Contains(axisText)
                && failure.Message.Contains($"repeat {badIndex + 1} of 3"))).IsTrue();
    }

    /// <summary>
    /// Fixup 04 F3 (kills V3): B moving only vertically must fail on the vertical check. Under a
    /// mutant that deletes the Y check the row passes, because its X is exactly right.
    /// </summary>
    [StaFact]
    public void Judge_FailsWhenBIsOffVerticallyOnly()
    {
        var (expected, referenceB, referenceA, _, sweep) = HealthyInputs();

        var verticalOnly = ProbeRingOnScreenJudge.AssertionFractions
            .Select(fraction => new ProbeRingOnScreenJudge.AbFrame(
                fraction, RingAt(fraction, snap: true), RingAtAxis(fraction, fraction + 0.1, snap: false)))
            .ToArray();

        var report = ProbeRingOnScreenJudge.Measure(
            Inputs(2.0, expected, referenceB, referenceA, verticalOnly, sweep));

        Check.WithCustomMessage(
                "B off by 0.1 px vertically only must fail on the vertical check; got failures: "
                + string.Join(" | ", report.Failures.Select(failure => failure.Message)))
            .That(report.Failures.Any(failure =>
                failure.Message.Contains("Condition B") && failure.Message.Contains("(y axis)"))).IsTrue();
    }

    /// <summary>Fixup 04 F3 (kills V4): the same for condition A, one whole pixel low.</summary>
    [StaFact]
    public void Judge_FailsWhenAIsOffVerticallyOnly()
    {
        var (expected, referenceB, referenceA, _, sweep) = HealthyInputs();

        var verticalOnly = ProbeRingOnScreenJudge.AssertionFractions
            .Select(fraction => new ProbeRingOnScreenJudge.AbFrame(
                fraction,
                RingAtAxis(Math.Round(fraction, MidpointRounding.AwayFromZero), Math.Round(fraction, MidpointRounding.AwayFromZero) + 1.0, snap: true),
                RingAt(fraction, snap: false)))
            .ToArray();

        var report = ProbeRingOnScreenJudge.Measure(
            Inputs(2.0, expected, referenceB, referenceA, verticalOnly, sweep));

        Check.WithCustomMessage(
                "A one whole pixel low must fail on the vertical check; got failures: "
                + string.Join(" | ", report.Failures.Select(failure => failure.Message)))
            .That(report.Failures.Any(failure =>
                failure.Message.Contains("Condition A") && failure.Message.Contains("(y axis)"))).IsTrue();
    }

    /// <summary>
    /// The deciders are diagnostics (orchestrator decision): a wildly wrong diagnostic row is
    /// measured and printed, and must never add a failure. Kills a mutant that judges them.
    /// </summary>
    [StaFact]
    public void Diagnostics_AreMeasuredButNeverJudged()
    {
        var (expected, referenceB, referenceA, assertions, sweep) = HealthyInputs();
        var diagnostic = new ProbeRingOnScreenJudge.DiagnosticGroup(
            "D2",
            "solid-look B rows (WeightedSandwich)",
            referenceB,
            [
                new ProbeRingOnScreenJudge.DiagnosticRow(
                    0.25, 0.25, [RingAt(0.9, snap: false), RingAt(0.9, snap: false), RingAt(0.9, snap: false)]),
            ]);

        var report = ProbeRingOnScreenJudge.Measure(
            Inputs(2.0, expected, referenceB, referenceA, assertions, sweep, diagnostics: [diagnostic]));

        Check.WithCustomMessage("The diagnostic row must be measured.")
            .That(report.Diagnostics).HasSize(1);
        Check.WithCustomMessage(
                "Diagnostics are never judged; a diagnostic 0.65 px off must not add any failure.")
            .That(report.Failures).IsEmpty();

        var lines = new List<string>();
        ProbeRingOnScreenJudge.WriteReport(report, lines.Add);
        Check.WithCustomMessage("The diagnostic lines must carry the DIAGNOSTIC label.")
            .That(lines.Any(line => line.Contains("DIAGNOSTIC D2"))).IsTrue();
    }

    /// <summary>
    /// Fixup 03 item 3: the f = 0 reference repeats become the noise floor — mean and spread of the
    /// repeated reference measured against repeat 0.
    /// </summary>
    [StaFact]
    public void ReferenceNoise_IsMeasuredOverTheReferenceRepeats()
    {
        var (expected, referenceB, referenceA, assertions, sweep) = HealthyInputs();

        // Repeats 1..4 are rendered at 0.125 / 0.25 / 0.375 / 0.5 away from repeat 0.
        var jittered = new[]
        {
            referenceB,
            RingAt(0.125, snap: false),
            RingAt(0.25, snap: false),
            RingAt(0.375, snap: false),
            RingAt(0.5, snap: false),
        };

        var report = ProbeRingOnScreenJudge.Measure(
            Inputs(2.0, expected, referenceB, referenceA, assertions, sweep, jittered));

        Check.WithCustomMessage("The noise floor must aggregate every reference repeat.")
            .That(report.ReferenceNoise).IsNotNull();
        Check.That(report.ReferenceNoise!.Repeats).IsEqualTo(5);
        Check.WithCustomMessage(
                $"The noise floor's spread must show the reference jitter; got {report.ReferenceNoise.SpreadX:0.0000}.")
            .That(report.ReferenceNoise.SpreadX).IsCloseTo(0.5, 0.02);
        Check.That(report.ReferenceNoise.SampleX).HasSize(5);
    }

    // ── fixup 08 (owner's decision 2026-10-11, after run 2): judged on the 21-row average ──

    /// <summary>
    /// Fixup 08 item 1: the judged estimator is the 21-row average. The fixture perturbs only the
    /// scan row — replaced with the same row of a render 0.2 px away — so the single row reads ~0.2
    /// px off while the 21-row average (20 clean rows + 1 bad) stays inside ±0.05: the Judge must
    /// pass. A mutant that judges the single row again fails here, and a radius-0 mutant collapses
    /// the average onto the bad row the same way. The single row must still be printed, labelled
    /// DIAGNOSTIC.
    /// </summary>
    [StaFact]
    public void JudgedTwentyOneRow_AbsorbsAScanRowOnlyPerturbation()
    {
        var (expected, referenceB, referenceA, assertions, sweep) = HealthyInputs();
        var perturbed = WithRowFrom(RingAt(0.25, snap: false), RingAt(0.45, snap: false), Geometry.ScanRow);
        var inputs = Inputs(2.0, expected, referenceB, referenceA, assertions, sweep);
        inputs = inputs with
        {
            AssertionRows = inputs.AssertionRows
                .Select(row => row.Condition == ProbeRingOnScreenJudge.ConditionB
                               && Math.Abs(row.Fraction - 0.25) < 1e-9
                    ? row with { Repeats = [perturbed, perturbed, perturbed] }
                    : row)
                .ToList(),
        };

        var report = ProbeRingOnScreenJudge.Measure(inputs);
        var row = report.AssertionRows.Single(candidate =>
            candidate.Condition == ProbeRingOnScreenJudge.ConditionB && Math.Abs(candidate.Fraction - 0.25) < 1e-9);

        Check.WithCustomMessage(
                $"The 21-row average must absorb a scan-row-only 0.2 px perturbation; got judged {row.MeanX:0.0000}.")
            .That(row.MeanX).IsCloseTo(0.25, ProbeRingOnScreenJudge.TolerancePx);
        Check.WithCustomMessage(
                $"The single scan row itself must read the perturbation (its diagnostic role); got {row.SingleRowMeanX:0.0000}.")
            .That(Math.Abs(row.SingleRowMeanX - 0.25)).IsStrictlyGreaterThan(ProbeRingOnScreenJudge.TolerancePx);
        Check.WithCustomMessage(
                "The scan-row-only perturbation is exactly what a single-row judge would fail on; the judged "
                + $"path must not fail: {string.Join(" | ", report.Failures.Select(failure => failure.Message))}")
            .That(report.Failures).IsEmpty();

        var lines = new List<string>();
        ProbeRingOnScreenJudge.WriteReport(report, lines.Add);
        Check.WithCustomMessage("The scan row must still be printed as a DIAGNOSTIC single-row line.")
            .That(lines.Any(line => line.Contains("R2 DIAGNOSTIC single-row") && line.Contains("f=0.25"))).IsTrue();
    }

    /// <summary>
    /// Fixup 08 item 1: the reference A–B check is judged on the 21-row average too. The fixture
    /// perturbs only reference B's scan row by 0.2 px, so a reference offset left on the single row
    /// would read the perturbation and fail while the 21-row average stays inside ±0.05.
    /// </summary>
    [StaFact]
    public void ReferenceOffset_IsJudgedOnTheTwentyOneRowAverage()
    {
        var (expected, referenceB, referenceA, assertions, sweep) = HealthyInputs();
        var perturbedReferenceB = WithRowFrom(referenceB, RingAt(0.2, snap: false), Geometry.ScanRow);

        var report = ProbeRingOnScreenJudge.Measure(
            Inputs(2.0, expected, perturbedReferenceB, referenceA, assertions, sweep));

        Check.WithCustomMessage(
                "A scan-row-only perturbation of reference B must not fail the 21-row reference offset; got: "
                + string.Join(" | ", report.Failures.Select(failure => failure.Message)))
            .That(report.Failures.Any(failure => failure.Message.Contains("With nothing to round"))).IsFalse();
    }

    /// <summary>
    /// Fixup 08 item 1: the judged-path guard. <c>Math.Abs(NaN − f) &gt; tolerance</c> is FALSE, so a
    /// bare comparison would let a NaN judged value pass silently; <see cref="ProbeRingOnScreenJudge.ViolatesTolerance"/>
    /// must flag every non-finite value, and only then fall back to the ±0.05 rule. A mutant that
    /// drops the finiteness arm turns this red.
    /// </summary>
    [Fact]
    public void ViolatesTolerance_RejectsNonFiniteJudgedValues()
    {
        Check.WithCustomMessage("A NaN judged value can never pass.")
            .That(ProbeRingOnScreenJudge.ViolatesTolerance(double.NaN, 0.25)).IsTrue();
        Check.WithCustomMessage("Positive infinity can never pass.")
            .That(ProbeRingOnScreenJudge.ViolatesTolerance(double.PositiveInfinity, 0.25)).IsTrue();
        Check.WithCustomMessage("Negative infinity can never pass.")
            .That(ProbeRingOnScreenJudge.ViolatesTolerance(double.NegativeInfinity, 0.25)).IsTrue();
        Check.WithCustomMessage("Inside the tolerance a finite value must pass.")
            .That(ProbeRingOnScreenJudge.ViolatesTolerance(0.25 + 0.04, 0.25)).IsFalse();
        Check.WithCustomMessage("Outside the tolerance a finite value must fail.")
            .That(ProbeRingOnScreenJudge.ViolatesTolerance(0.25 + 0.1, 0.25)).IsTrue();
    }

    /// <summary>
    /// Fixup 08 item 2: a stale capture in a judged row is a failure with a "stale frame" message —
    /// never passed, never silently dropped. The fixture marks one B repeat stale (with a healthy
    /// value, so no tolerance failure can mask the stale one); the failure must name the stale
    /// frame, the excluded repeat must be counted on the row, and the report must flag it. A mutant
    /// that ignores staleness in judged rows passes quietly and turns this red.
    /// </summary>
    [StaFact]
    public void StaleRepeat_InAJudgedRow_FailsWithTheStaleFrameMessage()
    {
        var (expected, referenceB, referenceA, assertions, sweep) = HealthyInputs();
        var inputs = Inputs(2.0, expected, referenceB, referenceA, assertions, sweep);
        inputs = inputs with
        {
            AssertionRows = inputs.AssertionRows
                .Select(row => row.Condition == ProbeRingOnScreenJudge.ConditionB
                               && Math.Abs(row.Fraction - 0.25) < 1e-9
                    ? row with
                    {
                        Repeats =
                        [
                            RingAt(0.25, snap: false),
                            RingAt(0.25, snap: false),
                            RingAt(0.25, snap: false),
                        ],
                        StaleFlags = [false, true, false],
                    }
                    : row)
                .ToList(),
        };

        var report = ProbeRingOnScreenJudge.Measure(inputs);

        Check.WithCustomMessage(
                "A stale repeat in a judged row must fail with a stale-frame message; got: "
                + string.Join(" | ", report.Failures.Select(failure => failure.Message)))
            .That(report.Failures.Any(failure => failure.Message.Contains("Stale frame"))).IsTrue();

        var row = report.AssertionRows.Single(candidate =>
            candidate.Condition == ProbeRingOnScreenJudge.ConditionB && Math.Abs(candidate.Fraction - 0.25) < 1e-9);
        Check.WithCustomMessage("The stale repeat must be excluded from the judged mean.")
            .That(row.MeanX).IsCloseTo(0.25, ProbeRingOnScreenJudge.TolerancePx);
        Check.That(row.StaleRepeats).IsEqualTo(1);

        var lines = new List<string>();
        ProbeRingOnScreenJudge.WriteReport(report, lines.Add);
        Check.WithCustomMessage("The report must flag the stale repeat on the judged row's lines.")
            .That(lines.Any(line => line.Contains("R2 row B f=0.25") && line.Contains("stale=1"))).IsTrue();
    }

    /// <summary>
    /// Fixup 09 F1 (review 07 MAJOR): a judged row with ZERO repeats used to pass vacuously — NaN
    /// means, a per-repeat loop over nothing, and a schedule check that only looks at fractions.
    /// It must fail, naming the row and the count. Kills the "count check deleted" mutant, which
    /// restores the vacuous pass.
    /// </summary>
    [StaFact]
    public void JudgedRows_WithNoRepeats_Fail_NeverPassVacuously()
    {
        var (expected, referenceB, referenceA, assertions, sweep) = HealthyInputs();
        var inputs = Inputs(2.0, expected, referenceB, referenceA, assertions, sweep);
        inputs = inputs with
        {
            AssertionRows = inputs.AssertionRows
                .Select(row => row with { Repeats = [], StaleFlags = null })
                .ToList(),
        };

        var report = ProbeRingOnScreenJudge.Measure(inputs);

        Check.WithCustomMessage("An empty judged row must be a failure, not a vacuous pass.")
            .That(report.Failures).Not.IsEmpty();
        Check.WithCustomMessage(
                "The failure must name the row and its count; got: "
                + string.Join(" | ", report.Failures.Select(failure => failure.Message)))
            .That(report.Failures.Any(failure =>
                failure.Message.Contains("0 captures")
                && failure.Message.Contains("f = 0.25"))).IsTrue();

        var verdict = FailureMessage(() => ProbeRingOnScreenJudge.Judge(report));
        Check.WithCustomMessage("The verdict must fail too; an empty run cannot be green.")
            .That(verdict).IsNotNull();
    }

    /// <summary>
    /// Fixup 09 F1: the same for a short row — 2 of the 3 scheduled repeats is a failure that names
    /// the row and the count, even though the two captures it does hold measure fine.
    /// </summary>
    [StaFact]
    public void JudgedRow_WithTwoOfThreeRepeats_Fails()
    {
        var (expected, referenceB, referenceA, assertions, sweep) = HealthyInputs();
        var inputs = Inputs(2.0, expected, referenceB, referenceA, assertions, sweep);
        inputs = inputs with
        {
            AssertionRows = inputs.AssertionRows
                .Select(row => row.Condition == ProbeRingOnScreenJudge.ConditionB
                               && Math.Abs(row.Fraction - 0.5) < 1e-9
                    ? row with { Repeats = [RingAt(0.5, snap: false), RingAt(0.5, snap: false)] }
                    : row)
                .ToList(),
        };

        var report = ProbeRingOnScreenJudge.Measure(inputs);

        Check.WithCustomMessage(
                "A judged B row holding 2 of its 3 scheduled repeats must fail; got: "
                + string.Join(" | ", report.Failures.Select(failure => failure.Message)))
            .That(report.Failures.Any(failure =>
                failure.Requirement.Contains("condition B")
                && failure.Message.Contains("2 captures")
                && failure.Message.Contains("f = 0.50"))).IsTrue();
        Check.WithCustomMessage("Only the count check may fail; the row's two captures measure fine.")
            .That(report.Failures.Count(failure => failure.Message.Contains("2 captures"))).IsEqualTo(1);
    }

    /// <summary>Copies a raster and replaces one row with the same row of <paramref name="donor"/>.</summary>
    private static ProbeRaster WithRowFrom(ProbeRaster target, ProbeRaster donor, int row)
    {
        var pixels = (byte[])target.Pixels.Clone();
        Array.Copy(
            donor.Pixels, row * donor.Width * 4, pixels, row * target.Width * 4, target.Width * 4);
        return new ProbeRaster(pixels, target.Width, target.Height);
    }
}
