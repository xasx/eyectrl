namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System;
using System.Globalization;
using System.Linq;
using EyeCtrl.App.Tests.Diagnostics;
using EyeCtrl.Core.Models;

/// <summary>
/// R2's headless half: the analysis judged on synthetic captures, so the estimator, the positive
/// control, the DPI check and the sweep evaluation are all proven — and mutation-proved — on CI,
/// with no window and no display.
///
/// <para>
/// <b>The synthetic captures are real renders.</b> Each frame is produced by the same
/// <see cref="ProbeRingVisual"/> the probe draws with, through <c>RenderTargetBitmap</c> — the R1
/// path — on the capture key background. The negative cases are the same renderer with one property
/// changed: no ring, a whole-pixel shift, the wrong size, a 96-DPI render stretched 2x. The
/// on-screen harness feeds its GDI captures through this same analysis code.
/// </para>
///
/// <para>
/// <b>Mutation proofs (a mutant must be shown red before it counts).</b> The control always true →
/// <see cref="Control_FailsWithNoRing_OnAUniformCapture"/>. The estimator's reference offset
/// dropped → <see cref="B_Ring_MovesByTheScheduledFraction"/> (and the A case). The DPI check
/// removed → <see cref="EffectiveDpi_AcceptsOnlyTheOwnerScale"/>. The flush and the gate are pinned
/// in <see cref="ProbeRingOnScreenGateTests"/>, because they live in the on-screen harness's
/// source and its gate rather than in a raster.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class ProbeRingCaptureAnalysisTests
{
    private const int CaptureSizePx = 600;
    private const int StretchedSourceSizePx = 300;

    /// <summary>The owner's stroke radius at scale 2.0: 250 DIP x 2 / 2.</summary>
    private const double HalfPx = ProbeRingCaptureScene.RingSizeDip * ProbeRingCaptureAnalysis.OwnerScale / 2.0;

    /// <summary>
    /// The tolerance the synthetic cases assert at. The synthetic captures run through
    /// <c>RenderTargetBitmap</c>, whose 1/8-px edge grid R1 measured (residual at most 0.040 px on
    /// the on-grid fractions), and the weight transform adds the same composite-edge bias R1's
    /// mirrored mean cancels. The on-screen assertion stays at R1's ±0.05; this is the headless
    /// proxy's own, measured, margin.
    /// </summary>
    private const double TolerancePx = 0.05;

    private static readonly PhysicalPoint CentreInCapture = new(300, 300);
    private static readonly PhysicalPoint StretchedSourceCentre = new(150, 150);
    private static readonly PhysicalPoint StretchedCentreInCapture = new(300, 300);

    private readonly ITestOutputHelper _output;

    public ProbeRingCaptureAnalysisTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void EffectiveDpi_AcceptsOnlyTheOwnerScale()
    {
        Check.That(ProbeRingCaptureAnalysis.CheckEffectiveDpi(ProbeRingCaptureAnalysis.OwnerScale).IsOwnerScale)
            .IsTrue();

        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.25, 3.0 })
        {
            var verdict = ProbeRingCaptureAnalysis.CheckEffectiveDpi(scale);
            Check.WithCustomMessage($"Effective scale {scale} must fail the owner-scale check: {verdict.Message}")
                .That(verdict.IsOwnerScale).IsFalse();
            Check.WithCustomMessage("The DPI failure must say what the test requires (192 DPI).")
                .That(verdict.Message).Contains("192");
        }
    }

    [Fact]
    public void Weights_MeasureDistanceFromTheCapturesOwnBackground()
    {
        var uniform = UniformFrame();
        var background = ProbeRingCaptureAnalysis.MeasureBackground(uniform, CaptureSizePx, CaptureSizePx);
        Check.That(background).IsEqualTo(
            (ProbeRingCaptureScene.KeyColour.R, ProbeRingCaptureScene.KeyColour.G, ProbeRingCaptureScene.KeyColour.B));

        var weights = ProbeRingCaptureAnalysis.ToWeights(uniform, CaptureSizePx, CaptureSizePx, background);
        Check.WithCustomMessage(
                "A uniform capture measured against its own background must carry no weight at all; "
                + "anything else and the control can pass on a frame with no ring in it.")
            .That(weights.Pixels.Where((_, i) => i % 4 == 3).All(weight => weight == 0)).IsTrue();
    }

    [StaFact]
    public void Control_PassesOnAnIndependentlyRenderedExpectedRing()
    {
        // Two separate renders of the same scene (review 01 n2): passing one frame as both sides
        // would make cosine 1.0 and offset 0 true by construction.
        var capture = Weights(FrameAt(0.0, snap: false));
        var expected = Weights(FrameAt(0.0, snap: false));

        var verdict = ProbeRingCaptureAnalysis.CheckExpectedRing(capture, expected, CentreInCapture, HalfPx);

        _output.WriteLine(verdict.Reason);
        Check.WithCustomMessage(verdict.Reason).That(verdict.Matched).IsTrue();
        Check.That(verdict.CentreOffsetPx).IsLessOrEqualThan(ProbeRingCaptureAnalysis.CentreTolerancePx);
        Check.That(Math.Abs(verdict.RadiusPx - HalfPx)).IsLessOrEqualThan(ProbeRingCaptureAnalysis.RadiusTolerancePx);
        Check.That(verdict.Similarity).IsStrictlyGreaterThan(0.95);
    }

    /// <summary>
    /// The tolerance-proving positive (review 01 n2): a capture shifted by a quarter pixel must still
    /// pass every control check. Without this, the positive fixture only proves the control accepts
    /// an exact copy of itself.
    /// </summary>
    [StaFact]
    public void Control_PassesOnASubToleranceShift()
    {
        var expected = Weights(FrameAt(0.0, snap: false));
        var shifted = Weights(FrameAt(0.25, snap: false));

        var verdict = ProbeRingCaptureAnalysis.CheckExpectedRing(shifted, expected, CentreInCapture, HalfPx);

        _output.WriteLine(verdict.Reason);
        Check.WithCustomMessage(
                $"{verdict.Reason} A 0.25 px shift is inside the centre tolerance and must not fail the control.")
            .That(verdict.Matched).IsTrue();
        Check.That(verdict.CentreOffsetPx).IsStrictlyGreaterThan(0.2);
        Check.That(verdict.CentreOffsetPx).IsLessOrEqualThan(ProbeRingCaptureAnalysis.CentreTolerancePx);
        Check.WithCustomMessage("A sub-pixel shift must not dent the pattern similarity.")
            .That(verdict.Similarity).IsStrictlyGreaterThan(0.9);
    }

    /// <summary>
    /// Fixup 04 F3 (kills V1/V2): an asymmetric shift — only the columns right of the centre rolled
    /// one pixel right — must come back as RightX ≈ 1, LeftX ≈ 0 and MeanX ≈ 0.5. The halves come
    /// from the estimator itself (<c>ProbeRasterEstimator.MirroredX</c>), not from a second copy.
    /// </summary>
    [StaFact]
    public void MirroredHalves_ReportAnAsymmetricShiftSeparately()
    {
        var reference = Weights(FrameAt(0.0, snap: false));
        var moved = RollRightOfCentre(reference, (int)CentreInCapture.X);

        var shift = ProbeRingCaptureAnalysis.MeasureMirroredShift(
            moved, reference, ProbeRingCaptureAnalysis.RingScanGeometry.For(CentreInCapture, HalfPx));

        _output.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "R2 synthetic asymmetric shift: left={0:0.0000} right={1:0.0000} mean={2:0.0000}",
            shift.LeftX, shift.RightX, shift.MeanX));
        Check.WithCustomMessage($"Only the right half moved; the left block must read 0, got {shift.LeftX:0.0000}.")
            .That(shift.LeftX).IsCloseTo(0.0, TolerancePx);
        Check.WithCustomMessage($"The right half rolled one pixel right; got {shift.RightX:0.0000}.")
            .That(shift.RightX).IsCloseTo(1.0, TolerancePx);
        Check.WithCustomMessage($"The mirrored mean of 0 and 1 must be 0.5, got {shift.MeanX:0.0000}.")
            .That(shift.MeanX).IsCloseTo(0.5, TolerancePx);
    }

    /// <summary>Copies a raster, shifting every column right of <paramref name="centreX"/> one pixel right.</summary>
    private static ProbeRaster RollRightOfCentre(ProbeRaster source, int centreX)
    {
        var pixels = (byte[])source.Pixels.Clone();
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = source.Width - 1; x > centreX; x--)
            {
                var from = (y * source.Width + x - 1) * 4;
                var to = (y * source.Width + x) * 4;
                pixels[to] = source.Pixels[from];
                pixels[to + 1] = source.Pixels[from + 1];
                pixels[to + 2] = source.Pixels[from + 2];
                pixels[to + 3] = source.Pixels[from + 3];
            }
        }

        return new ProbeRaster(pixels, source.Width, source.Height);
    }

    [StaFact]
    public void Control_FailsWithNoRing_OnAUniformCapture()
    {
        var expected = Weights(FrameAt(0.0, snap: false));
        var verdict = ProbeRingCaptureAnalysis.CheckExpectedRing(
            Weights(UniformFrame()), expected, CentreInCapture, HalfPx);

        _output.WriteLine(verdict.Reason);
        Check.WithCustomMessage(
                "A capture of the desktop (or an excluded window) with no ring must fail the control; "
                + "'ring pixels present' must not be satisfiable by absence.")
            .That(verdict.Matched).IsFalse();
        Check.WithCustomMessage("The failure must name the missing ring in the expected annulus.")
            .That(verdict.Reason).Contains("No ring weight in the expected annulus");
    }

    [StaFact]
    public void Control_FailsWhenTheRingIsAWholePixelOff()
    {
        var expected = Weights(FrameAt(0.0, snap: false));
        var shifted = Weights(FrameAt(0.0, snap: false, centre: new PhysicalPoint(301, 301)));
        var verdict = ProbeRingCaptureAnalysis.CheckExpectedRing(shifted, expected, CentreInCapture, HalfPx);

        _output.WriteLine(verdict.Reason);
        Check.WithCustomMessage(
                "A ring one whole pixel from the expected place must fail the control, or a misplacement "
                + "would be measured as a rendering result.")
            .That(verdict.Matched).IsFalse();
    }

    /// <summary>
    /// The centre check's isolating fixture (review 01 M2): a 0.75 px x-shift fails the centre
    /// tolerance (0.777 &gt; 0.5) while the cosine (~0.86) and the radius stay inside theirs, so
    /// loosening the centre tolerance alone must turn this test red.
    /// </summary>
    [StaFact]
    public void Control_FailsOnACentreOnlyShift()
    {
        var expected = Weights(FrameAt(0.0, snap: false));
        var shifted = Weights(FrameAt(0.0, snap: false, centre: new PhysicalPoint(300.75, 300)));

        var verdict = ProbeRingCaptureAnalysis.CheckExpectedRing(shifted, expected, CentreInCapture, HalfPx);

        _output.WriteLine(verdict.Reason);
        Check.WithCustomMessage(
                "A 0.75 px shift is outside the centre tolerance and inside the cosine's; the centre check "
                + "must be what rejects it.")
            .That(verdict.Matched).IsFalse();
        Check.WithCustomMessage($"The failure must be the centre check's; got: {verdict.Reason}")
            .That(verdict.Reason).Contains("centre sits");
    }

    /// <summary>
    /// The cosine check's isolating fixture (review 01 M2): a 252-DIP ring keeps the centre (0.06 px)
    /// and the mean radius (1.997 px, just inside 2.0) inside their tolerances, but a 1-DIP size
    /// change re-phases the BiTonal dashes and the pattern cosine collapses to ~0.1, so loosening the
    /// cosine floor alone must turn this test red.
    /// </summary>
    [StaFact]
    public void Control_FailsOnACosineOnlyMismatch()
    {
        var settings = ProbeRingCaptureScene.OwnerLook();
        settings.CircleSize = 252;
        var expected = Weights(FrameAt(0.0, snap: false));
        var changed = Weights(ProbeRingCaptureScene.RenderCapture(
            ProbeRingCaptureAnalysis.OwnerScale, settings, CentreInCapture, snap: false, CaptureSizePx));

        var verdict = ProbeRingCaptureAnalysis.CheckExpectedRing(changed, expected, CentreInCapture, HalfPx);

        _output.WriteLine(verdict.Reason);
        Check.WithCustomMessage(
                "A 252-DIP ring is inside the centre and radius tolerances but is not the expected pattern; "
                + "the cosine check must be what rejects it.")
            .That(verdict.Matched).IsFalse();
        Check.WithCustomMessage($"The failure must be the cosine check's; got: {verdict.Reason}")
            .That(verdict.Reason).Contains("cosine");
    }

    [StaFact]
    public void Control_FailsOnTheWrongRingSize()
    {
        var settings = ProbeRingCaptureScene.OwnerLook();
        settings.CircleSize = 240;
        var expected = Weights(FrameAt(0.0, snap: false));
        var wrongSize = Weights(ProbeRingCaptureScene.RenderCapture(
            ProbeRingCaptureAnalysis.OwnerScale, settings, CentreInCapture, snap: false, CaptureSizePx));

        var verdict = ProbeRingCaptureAnalysis.CheckExpectedRing(wrongSize, expected, CentreInCapture, HalfPx);

        _output.WriteLine(verdict.Reason);
        Check.WithCustomMessage(
                "A 240 DIP ring captured against the expected 250 DIP ring must fail the control; the A/B "
                + "measurement would otherwise run on the wrong object.")
            .That(verdict.Matched).IsFalse();
    }

    [StaTheory]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.75)]
    public void B_Ring_MovesByTheScheduledFraction(double fraction)
    {
        var reference = Weights(FrameAt(0.0, snap: false));
        var moved = Weights(FrameAt(fraction, snap: false));

        var (x, y) = ProbeRingCaptureAnalysis.MeasureShift(
            moved, reference, ProbeRingCaptureAnalysis.RingScanGeometry.For(CentreInCapture, HalfPx));

        _output.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "R2 synthetic B f={0:0.00}: measured=({1:0.0000}, {2:0.0000})", fraction, x, y));
        Check.WithCustomMessage(
                $"Condition B must move by the scheduled fraction: f = {fraction} measured ({x:0.0000}, {y:0.0000}). "
                + "If this measures 0 or 1, the estimator lost the reference offset and the on-screen "
                + "assertion would be blind.")
            .That(x).IsCloseTo(fraction, TolerancePx);
        Check.WithCustomMessage($"Condition B's vertical movement must track f = {fraction}, measured {y:0.0000}.")
            .That(y).IsCloseTo(fraction, TolerancePx);
    }

    [StaTheory]
    [InlineData(0.25, 0.0)]
    [InlineData(0.5, 1.0)]
    [InlineData(0.75, 1.0)]
    public void A_Ring_LandsOnWholePixels(double fraction, double expected)
    {
        var reference = Weights(FrameAt(0.0, snap: true));
        var moved = Weights(FrameAt(fraction, snap: true));

        var (x, y) = ProbeRingCaptureAnalysis.MeasureShift(
            moved, reference, ProbeRingCaptureAnalysis.RingScanGeometry.For(CentreInCapture, HalfPx));

        _output.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "R2 synthetic A f={0:0.00}: measured=({1:0.0000}, {2:0.0000}) expected {3:0.00}", fraction, x, y, expected));
        Check.WithCustomMessage(
                $"Condition A must land on whole device pixels (half away from zero): f = {fraction} should "
                + $"move {expected:0} px, measured {x:0.0000}.")
            .That(x).IsCloseTo(expected, TolerancePx);
        Check.WithCustomMessage(
                $"Condition A's vertical move for f = {fraction} should be {expected:0} px, measured {y:0.0000}.")
            .That(y).IsCloseTo(expected, TolerancePx);
    }

    /// <summary>
    /// The on-screen mutant m1's shape, on the synthetic path: a capture that was snapped when it
    /// should have been fractional measures no movement at f = 0.25. This is the property assertion
    /// (e) stands on — if the analysis returned the scheduled value instead of measuring the frame,
    /// this test would pass while the on-screen test proved nothing.
    /// </summary>
    [StaFact]
    public void ASnappedB_MeasuresNoMovement_SoTheFractionAssertionWouldFail()
    {
        var reference = Weights(FrameAt(0.0, snap: false));
        var snapped = Weights(FrameAt(0.25, snap: true));

        var (x, y) = ProbeRingCaptureAnalysis.MeasureShift(
            snapped, reference, ProbeRingCaptureAnalysis.RingScanGeometry.For(CentreInCapture, HalfPx));

        _output.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "R2 synthetic snapped-B: measured=({0:0.0000}, {1:0.0000}) against a scheduled 0.25", x, y));
        Check.WithCustomMessage(
                "A snapped B capture must measure no movement — the on-screen test's ±0.05 assertion is what "
                + "turns that into a red test.")
            .That(x).IsCloseTo(0.0, TolerancePx);
        Check.WithCustomMessage(
                "A snapped B must not measure the scheduled 0.25; if it did, the analysis would be reporting "
                + "the schedule, not the pixels.")
            .That(Math.Abs(x - 0.25)).IsStrictlyGreaterThan(TolerancePx);
    }

    [StaFact]
    public void A96DpiStretchedRing_DoublesTheFraction_SoTheAssertionWouldFail()
    {
        var reference = Weights(ProbeRingCaptureScene.Stretch2x(
            FrameAt(0.0, snap: false, centre: StretchedSourceCentre, scale: 1.0, size: StretchedSourceSizePx),
            StretchedSourceSizePx, StretchedSourceSizePx));
        var moved = Weights(ProbeRingCaptureScene.Stretch2x(
            FrameAt(0.25, snap: false, centre: StretchedSourceCentre, scale: 1.0, size: StretchedSourceSizePx),
            StretchedSourceSizePx, StretchedSourceSizePx));

        var (x, y) = ProbeRingCaptureAnalysis.MeasureShift(
            moved, reference, ProbeRingCaptureAnalysis.RingScanGeometry.For(StretchedCentreInCapture, HalfPx));

        _output.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "R2 synthetic 96-DPI-stretched ring at f=0.25: measured=({0:0.0000}, {1:0.0000})", x, y));
        Check.WithCustomMessage(
                "A 96-DPI render stretched to the 192-DPI display must measure twice the scheduled fraction: "
                + $"0.25 in DIPs lands on 0.5 device pixels, but measured ({x:0.0000}, {y:0.0000}).")
            .That(x).IsCloseTo(0.5, 0.1);
        Check.WithCustomMessage(
                "The stretched capture must not measure the scheduled 0.25; a pass at that reading would be "
                + "the DWM bitmap-stretch the DPI check exists to catch.")
            .That(Math.Abs(x - 0.25)).IsStrictlyGreaterThan(TolerancePx);
    }

    [Fact]
    public void Sweep_Evaluation_ReportsTheWorstError_AndResolvesTheFinestStep()
    {
        var perfect = Enumerable.Range(0, 20)
            .Select(i => new ProbeRingCaptureAnalysis.SweepRow(i * 0.05, i * 0.05, i * 0.05))
            .ToArray();
        var perfectSummary = ProbeRingCaptureAnalysis.EvaluateSweep(perfect);
        Check.That(perfectSummary.MaxAbsErrorPx).IsCloseTo(0.0, 1e-9);
        Check.That(perfectSummary.ResolvedStepPx).IsEqualTo(0.05);

        var quarterSnapped = Enumerable.Range(0, 20)
            .Select(i =>
            {
                var fraction = i * 0.05;
                var measured = Math.Round(fraction * 4.0) / 4.0;
                return new ProbeRingCaptureAnalysis.SweepRow(fraction, measured, measured);
            })
            .ToArray();
        var quarterSummary = ProbeRingCaptureAnalysis.EvaluateSweep(quarterSnapped);
        Check.WithCustomMessage(
                "A path that lands only on quarter pixels cannot resolve 0.05 or 0.10 steps; the finest it "
                + $"resolves is 0.25, but the evaluation said {quarterSummary.ResolvedStepPx}. {quarterSummary.Note}")
            .That(quarterSummary.ResolvedStepPx).IsEqualTo(0.25);
    }

    // ── frame helpers ──

    private static byte[] FrameAt(
        double fraction,
        bool snap,
        PhysicalPoint? centre = null,
        double scale = ProbeRingCaptureAnalysis.OwnerScale,
        int size = CaptureSizePx)
    {
        var start = centre ?? CentreInCapture;
        return ProbeRingCaptureScene.RenderCapture(
            scale, ProbeRingCaptureScene.OwnerLook(),
            new PhysicalPoint(start.X + fraction, start.Y + fraction), snap, size);
    }

    private static ProbeRaster Weights(byte[] bgra)
    {
        var size = (int)Math.Round(Math.Sqrt(bgra.Length / 4.0));
        var background = ProbeRingCaptureAnalysis.MeasureBackground(bgra, size, size);
        return ProbeRingCaptureAnalysis.ToWeights(bgra, size, size, background);
    }

    private static byte[] UniformFrame()
    {
        var frame = new byte[CaptureSizePx * CaptureSizePx * 4];
        for (var i = 0; i < frame.Length; i += 4)
        {
            frame[i] = ProbeRingCaptureScene.KeyColour.B;
            frame[i + 1] = ProbeRingCaptureScene.KeyColour.G;
            frame[i + 2] = ProbeRingCaptureScene.KeyColour.R;
            frame[i + 3] = 0xFF;
        }

        return frame;
    }
}
