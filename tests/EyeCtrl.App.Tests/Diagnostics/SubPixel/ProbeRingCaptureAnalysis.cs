namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EyeCtrl.App.Tests.Diagnostics;
using EyeCtrl.Core.Models;

/// <summary>
/// R2's pure analysis: everything the on-screen raster test judges a captured frame with, in code
/// that runs headlessly. It is deliberately free of window or desktop access — the harness feeds it
/// byte rasters and geometry — so the whole judgement can be unit-tested and mutation-proved on
/// synthetic captures, on CI, without a display.
///
/// <para>
/// <b>The measured channel.</b> A GDI <c>CopyFromScreen</c> capture carries no meaningful alpha, so
/// every raster is converted to a <i>weight</i> before measurement: the per-pixel Euclidean RGB
/// distance from the capture's own background, which is linear in the ring's coverage where a band
/// blends with the background. The background is measured from the capture's four corner blocks
/// (never a hard-coded colour), so display colour management cannot void the transform. The
/// mirrored-block estimator then measures those weights exactly as R1 measures a render's alpha —
/// one shared implementation (<see cref="ProbeRasterEstimator"/>).
/// </para>
///
/// <para>
/// <b>What the positive control is for (review 01 R2 (c)).</b> "Ring pixels present" can pass on
/// the desktop seen through an excluded window, so the control demands an actual ring: weight in
/// the expected annulus (a ring that is there at all), a centre within
/// <see cref="CentreTolerancePx"/> (catches a whole-pixel placement error), a mean stroke radius
/// within <see cref="RadiusTolerancePx"/> (catches the wrong size), and a cosine similarity
/// against the expected pattern of at least <see cref="PatternSimilarityFloor"/> (catches anything
/// else that merely has ring-shaped weight somewhere). If the control fails, the test fails.
/// </para>
///
/// <para>
/// <b>The radius check is a diagnostic, not an independent fourth check.</b> On the owner's dashed
/// BiTonal look, every ≥ 2 px radius error also drives the pattern cosine to ~0 — a size change
/// moves the dash phase with the circumference — so no radius-only fixture exists (review 01 M2,
/// measured). It stays because it names the wrong-size case in one sentence, but a green control
/// must not be read as four independent confirmations.
/// </para>
///
/// <para>
/// <b>Why the control compares against the expected render, not against the schedule.</b> The
/// owner's BiTonalSegments look is dashed, and its dash cycle does not close exactly on the
/// rendered path (15.94 cycles, not 16 — the dash length is computed from the nominal diameter,
/// and the rendered path sits half a stroke inside it), so an absolute centroid carries a seam
/// bias of about half a pixel. R1 never met it: R1 only ever asserts <i>shifts</i>, which cancel
/// the bias differentially. The control is the first absolute check, so it measures over the whole
/// annulus (which dilutes the seam) and compares the capture's statistics against the expected
/// pattern's own — two renders of the same geometry, with the seam in the same place relative to
/// the ring — while a whole-pixel misplacement of the captured ring still shows up as a whole
/// pixel of difference. The fractional measurement keeps using R1's windows.
/// </para>
/// </summary>
internal static class ProbeRingCaptureAnalysis
{
    /// <summary>The owner's display scale: 192 DPI, R2's one supported configuration (requirement a).</summary>
    internal const double OwnerScale = 2.0;

    /// <summary>How far the measured ring centre may sit from the expected one, in pixels.</summary>
    internal const double CentreTolerancePx = 0.5;

    /// <summary>How far the measured mean stroke radius may sit from the expected one, in pixels.</summary>
    internal const double RadiusTolerancePx = 2.0;

    /// <summary>The smallest expected-vs-captured pattern cosine the control accepts.</summary>
    internal const double PatternSimilarityFloor = 0.75;

    /// <summary>The fineness ladder the sweep report resolves the path to (requirement f).</summary>
    internal static readonly double[] SweepResolutionSteps = [0.05, 0.10, 0.20, 0.25];

    /// <summary>The largest possible Euclidean RGB distance (black to white on every channel).</summary>
    private static readonly double MaxRgbDistance = Math.Sqrt(3.0) * 255.0;

    /// <summary>Half the annulus band the control's presence and geometry statistics are taken over, in pixels.</summary>
    private const double AnnulusHalfWidthPx = 12.0;

    private const int BackgroundBlockPx = 16;

    /// <summary>Requirement (a): the captured window's effective scale, judged against 192 DPI.</summary>
    /// <param name="effectiveScale">The window's measured device-pixels-per-DIP scale.</param>
    /// <returns>The verdict and the message a failure carries.</returns>
    internal static DpiVerdict CheckEffectiveDpi(double effectiveScale)
    {
        if (Math.Abs(effectiveScale - OwnerScale) <= 1e-6)
        {
            return new DpiVerdict(true,
                $"Effective window scale {effectiveScale.ToString("0.###", CultureInfo.InvariantCulture)} "
                + "is the owner's 200% (192 DPI).");
        }

        return new DpiVerdict(false,
            $"The window's effective scale is {effectiveScale.ToString("0.###", CultureInfo.InvariantCulture)}, "
            + "not 2.0 (192 DPI). R2 proves sub-pixel rendering on the hardware path only at the owner's "
            + "200%: at any other effective scale DWM bitmap-stretching would fake a blurred edge, so an "
            + "A/B reading here would mean nothing. This must fail, not pass and not skip — run R2 on the "
            + "owner's 3840x2400 @ 200% display.");
    }

    /// <summary>
    /// The capture's background colour: the per-channel median of the four corner blocks, which sit
    /// at the capture's corners and so outside every possible ring position. Measured rather than
    /// declared, so a display that colour-grades the window cannot void the weight transform.
    /// </summary>
    /// <param name="bgra">The captured pixels, BGRA32, row-major.</param>
    /// <param name="width">The frame width in pixels.</param>
    /// <param name="height">The frame height in pixels.</param>
    internal static (byte R, byte G, byte B) MeasureBackground(byte[] bgra, int width, int height)
    {
        if (width < 2 * BackgroundBlockPx || height < 2 * BackgroundBlockPx)
            throw new ArgumentException($"A {width}x{height} frame cannot hold four {BackgroundBlockPx}px corner blocks.");

        var r = new byte[4 * BackgroundBlockPx * BackgroundBlockPx];
        var g = new byte[r.Length];
        var b = new byte[r.Length];
        var i = 0;
        foreach (var (blockX, blockY) in new[]
                 {
                     (0, 0), (width - BackgroundBlockPx, 0),
                     (0, height - BackgroundBlockPx), (width - BackgroundBlockPx, height - BackgroundBlockPx),
                 })
        {
            for (var y = blockY; y < blockY + BackgroundBlockPx; y++)
            {
                for (var x = blockX; x < blockX + BackgroundBlockPx; x++)
                {
                    var p = (y * width + x) * 4;
                    b[i] = bgra[p];
                    g[i] = bgra[p + 1];
                    r[i] = bgra[p + 2];
                    i++;
                }
            }
        }

        return (Median(r), Median(g), Median(b));
    }

    /// <summary>
    /// Converts a capture to the estimator's measured channel: one weight per pixel, the Euclidean
    /// RGB distance from <paramref name="background"/>, scaled so the maximum possible distance is
    /// 255. Linear in coverage wherever one band blends with the background, and invariant to any
    /// constant factor because the estimator's centroid divides by the total weight.
    /// </summary>
    /// <param name="bgra">The captured pixels, BGRA32, row-major.</param>
    /// <param name="width">The frame width in pixels.</param>
    /// <param name="height">The frame height in pixels.</param>
    /// <param name="background">The measured background colour.</param>
    internal static ProbeRaster ToWeights(byte[] bgra, int width, int height, (byte R, byte G, byte B) background)
    {
        if (bgra.Length != width * height * 4)
            throw new ArgumentException($"Pixels length {bgra.Length} does not match a {width}x{height} BGRA frame.", nameof(bgra));

        var weights = new byte[width * height];
        for (var i = 0; i < weights.Length; i++)
        {
            double dr = bgra[i * 4 + 2] - background.R;
            double dg = bgra[i * 4 + 1] - background.G;
            double db = bgra[i * 4] - background.B;
            var distance = Math.Sqrt(dr * dr + dg * dg + db * db) / MaxRgbDistance * 255.0;
            weights[i] = (byte)Math.Clamp(Math.Round(distance), 0.0, 255.0);
        }

        return ProbeRaster.FromWeights(weights, width, height);
    }

    /// <summary>
    /// Requirement (c): the positive control. The capture must hold the expected ring — weight in
    /// the expected annulus, a centre and mean stroke radius matching the expected pattern, and a
    /// pattern close to the expected render. A capture of a desktop region instead of the window
    /// fails these checks except by coincidence; a uniform region (the plain excluded-window case)
    /// fails the presence check outright because the annulus is empty.
    ///
    /// <para>
    /// <b>Why the comparison is capture-vs-expected, not capture-vs-schedule.</b> The owner's
    /// BiTonal look is dashed, and its dash cycle does not close exactly on the rendered path
    /// (15.94 cycles, not 16 — the dash length is computed from the nominal diameter, the rendered
    /// path sits half a stroke inside it), so any absolute centroid carries a seam bias of about
    /// half a pixel. The expected render has the same seam in the same place relative to the ring,
    /// so comparing the two patterns' statistics cancels it — while a whole-pixel misplacement of
    /// the captured ring still shows up as a whole pixel of difference. R1 only ever asserted
    /// shifts, which cancel the same bias differentially; the control is the first absolute
    /// check, and this is what it takes to make it unbiased.
    /// </para>
    /// </summary>
    /// <param name="captureWeights">The capture, converted with <see cref="ToWeights"/>.</param>
    /// <param name="expectedWeights">The expected pattern, rendered offscreen and converted with the same background.</param>
    /// <param name="expectedCentreInCapture">The ring centre the harness scheduled, in capture-local pixels.</param>
    /// <param name="expectedHalfPx">The expected stroke radius, in pixels.</param>
    internal static ControlVerdict CheckExpectedRing(
        ProbeRaster captureWeights,
        ProbeRaster expectedWeights,
        PhysicalPoint expectedCentreInCapture,
        double expectedHalfPx)
    {
        var capture = MeasureAnnulus(captureWeights, expectedCentreInCapture, expectedHalfPx);
        if (capture.Mass <= 0.0)
        {
            return ControlVerdict.Failed(
                $"No ring weight in the expected annulus (radius {expectedHalfPx:0.0} ± {AnnulusHalfWidthPx:0.0} px "
                + $"around ({expectedCentreInCapture.X:0.000}, {expectedCentreInCapture.Y:0.000})). This is the "
                + "desktop-behind-the-window shape: the capture shows no ring at all — the window may be excluded "
                + "from capture, or the ring is not drawn. 'Ring pixels present' cannot pass here.",
                double.NaN);
        }

        var expected = MeasureAnnulus(expectedWeights, expectedCentreInCapture, expectedHalfPx);
        if (expected.Mass <= 0.0)
        {
            throw new InvalidOperationException(
                "The expected pattern carries no weight in its own annulus; the expected frame was rendered "
                + "or converted wrongly. This is a harness fault, not a capture result.");
        }

        var offsetX = capture.CentreX - expected.CentreX;
        var offsetY = capture.CentreY - expected.CentreY;
        var centreOffset = Math.Sqrt(offsetX * offsetX + offsetY * offsetY);
        if (centreOffset > CentreTolerancePx)
        {
            return ControlVerdict.Failed(
                $"The captured ring's centre sits ({offsetX:0.000}, {offsetY:0.000}) px from the expected "
                + $"pattern's centre — {centreOffset:0.000} px away, outside the {CentreTolerancePx:0.00} px "
                + "tolerance. A whole-pixel placement error shows up exactly like this.",
                capture.MeanRadiusPx);
        }

        var radiusDelta = Math.Abs(capture.MeanRadiusPx - expected.MeanRadiusPx);
        if (radiusDelta > RadiusTolerancePx)
        {
            return ControlVerdict.Failed(
                $"The captured mean stroke radius is {capture.MeanRadiusPx:0.000} px against the expected "
                + $"pattern's {expected.MeanRadiusPx:0.000} — {radiusDelta:0.000} px apart, outside the "
                + $"{RadiusTolerancePx:0.00} px tolerance. The window does not hold the expected ring; a wrong "
                + "size reads like this, and the A/B measurement would be measured on the wrong object.",
                capture.MeanRadiusPx);
        }

        var similarity = Cosine(captureWeights, expectedWeights);
        if (similarity < PatternSimilarityFloor)
        {
            return ControlVerdict.Failed(
                $"The captured pattern is only {similarity:0.000} similar (cosine) to the expected ring "
                + $"(floor {PatternSimilarityFloor:0.00}). Weight sits at the right annulus, but what is drawn "
                + "there is not the expected pattern — a capture of desktop content can look like this.",
                capture.MeanRadiusPx);
        }

        return ControlVerdict.Passed(centreOffset, capture.MeanRadiusPx, similarity);
    }

    /// <summary>
    /// Requirement (e): the ring's movement between two captures, measured with
    /// <see cref="ProbeRasterEstimator"/> over the geometry of the reference frame.
    /// </summary>
    /// <param name="moved">The capture of the moved ring.</param>
    /// <param name="reference">The capture of the reference (whole-pixel) ring.</param>
    /// <param name="geometry">The scan geometry, in the reference capture's frame.</param>
    internal static (double X, double Y) MeasureShift(ProbeRaster moved, ProbeRaster reference, RingScanGeometry geometry)
    {
        var shift = MeasureMirroredShift(moved, reference, geometry);
        return (shift.MeanX, shift.MeanY);
    }

    /// <summary>
    /// The same measurement with its mirrored halves kept: the left/right block centroid shifts
    /// separately (x) and the top/bottom pair (y), as well as their mean. Fixup 03's diagnosis
    /// needs the halves: an asymmetric horizontal anti-aliasing shows up as left ≠ right, while a
    /// constant measurement offset shows up as a stable bias in both.
    /// </summary>
    /// <param name="moved">The capture of the moved ring.</param>
    /// <param name="reference">The capture of the reference (whole-pixel) ring.</param>
    /// <param name="geometry">The scan geometry, in the reference capture's frame.</param>
    internal static MirroredShift MeasureMirroredShift(
        ProbeRaster moved, ProbeRaster reference, RingScanGeometry geometry)
    {
        var left = ProbeRasterEstimator.ScanRow(
            moved, geometry.ScanRow, geometry.ReferenceCentreX - geometry.HalfPx)
            - ProbeRasterEstimator.ScanRow(
                reference, geometry.ScanRow, geometry.ReferenceCentreX - geometry.HalfPx);
        var right = ProbeRasterEstimator.ScanRow(
            moved, geometry.ScanRow, geometry.ReferenceCentreX + geometry.HalfPx)
            - ProbeRasterEstimator.ScanRow(
                reference, geometry.ScanRow, geometry.ReferenceCentreX + geometry.HalfPx);
        var top = ProbeRasterEstimator.ScanColumn(
            moved, geometry.ScanColumn, geometry.ReferenceCentreY - geometry.HalfPx)
            - ProbeRasterEstimator.ScanColumn(
                reference, geometry.ScanColumn, geometry.ReferenceCentreY - geometry.HalfPx);
        var bottom = ProbeRasterEstimator.ScanColumn(
            moved, geometry.ScanColumn, geometry.ReferenceCentreY + geometry.HalfPx)
            - ProbeRasterEstimator.ScanColumn(
                reference, geometry.ScanColumn, geometry.ReferenceCentreY + geometry.HalfPx);

        return new MirroredShift((left + right) / 2.0, (top + bottom) / 2.0, left, right, top, bottom);
    }

    /// <summary>
    /// A measured shift with its mirrored halves: <see cref="MeanX"/> is the mean of
    /// <see cref="LeftX"/> and <see cref="RightX"/> (the mirrored-block estimator's cancellation),
    /// and <see cref="MeanY"/> of <see cref="TopY"/> and <see cref="BottomY"/>.
    /// </summary>
    /// <param name="MeanX">The x movement: the mean of the left and right block shifts.</param>
    /// <param name="MeanY">The y movement: the mean of the top and bottom block shifts.</param>
    /// <param name="LeftX">The left edge block's centroid shift, x.</param>
    /// <param name="RightX">The right edge block's centroid shift, x.</param>
    /// <param name="TopY">The top edge block's centroid shift, y.</param>
    /// <param name="BottomY">The bottom edge block's centroid shift, y.</param>
    internal sealed record MirroredShift(
        double MeanX, double MeanY, double LeftX, double RightX, double TopY, double BottomY);

    /// <summary>
    /// Requirement (f): what the fine-fraction sweep says about the real display path. The sweep is
    /// a record, not a verdict — it reports the largest error seen and the finest fraction step the
    /// path resolves, meaning every scheduled multiple of that step measured nearer its own
    /// fraction than its neighbours'.
    /// </summary>
    /// <param name="rows">One row per captured fraction.</param>
    internal static SweepSummary EvaluateSweep(IReadOnlyList<SweepRow> rows)
    {
        if (rows.Count == 0)
            throw new ArgumentException("A sweep with no rows proves nothing.", nameof(rows));

        var maxError = rows.Max(row => Math.Max(Math.Abs(row.ErrorX), Math.Abs(row.ErrorY)));

        foreach (var step in SweepResolutionSteps)
        {
            var multiples = rows.Where(row => IsMultipleOf(row.Fraction, step)).ToList();
            if (multiples.Count < 2)
                continue;

            if (multiples.All(row =>
                    Math.Abs(row.ErrorX) <= step / 2.0 && Math.Abs(row.ErrorY) <= step / 2.0))
            {
                return new SweepSummary(maxError, step,
                    $"Every {step.ToString("0.00", CultureInfo.InvariantCulture)} px multiple of the fraction "
                    + "landed closer to its own scheduled value than to a neighbour's.");
            }
        }

        return new SweepSummary(maxError, double.NaN,
            "Not even the coarsest resolution step is resolved; the fractions are indistinguishable at this "
            + "rendering path within half a step.");
    }

    /// <summary>The scan geometry of a ring measurement, in capture-local pixel coordinates.</summary>
    /// <param name="ScanRow">The row the horizontal edge windows are scanned along.</param>
    /// <param name="ScanColumn">The column the vertical edge windows are scanned along.</param>
    /// <param name="ReferenceCentreX">The reference ring's centre x, in pixels.</param>
    /// <param name="ReferenceCentreY">The reference ring's centre y, in pixels.</param>
    /// <param name="HalfPx">The expected stroke radius, in pixels.</param>
    internal sealed record RingScanGeometry(
        int ScanRow, int ScanColumn, double ReferenceCentreX, double ReferenceCentreY, double HalfPx)
    {
        /// <summary>Builds the geometry R1 anchors its windows on: whole-pixel scan row and column through the reference centre.</summary>
        internal static RingScanGeometry For(PhysicalPoint referenceCentreInCapture, double halfPx) => new(
            ProbeRasterEstimator.RoundToWholePixel(referenceCentreInCapture.Y),
            ProbeRasterEstimator.RoundToWholePixel(referenceCentreInCapture.X),
            referenceCentreInCapture.X, referenceCentreInCapture.Y, halfPx);
    }

    /// <summary>The control's verdict, with the numbers it measured for the report.</summary>
    /// <param name="Matched">Whether the capture holds the expected ring at the expected place.</param>
    /// <param name="Reason">The failure's explanation, or the pass's summary.</param>
    /// <param name="CentreOffsetPx">The measured centre distance from expected; NaN when unmeasurable.</param>
    /// <param name="RadiusPx">The measured mean stroke radius; NaN when unmeasurable.</param>
    /// <param name="Similarity">The pattern cosine against the expected render.</param>
    internal sealed record ControlVerdict(bool Matched, string Reason, double CentreOffsetPx, double RadiusPx, double Similarity)
    {
        internal static ControlVerdict Failed(string reason, double radiusPx) =>
            new(false, reason, double.NaN, radiusPx, double.NaN);

        internal static ControlVerdict Passed(double centreOffsetPx, double radiusPx, double similarity) =>
            new(true,
                $"The capture holds the expected ring: centre offset {centreOffsetPx:0.000} px, mean stroke "
                + $"radius {radiusPx:0.000} px, pattern cosine {similarity:0.000}.",
                centreOffsetPx, radiusPx, similarity);
    }

    /// <summary>Requirement (a)'s verdict.</summary>
    /// <param name="IsOwnerScale">Whether the effective scale is the owner's 2.0.</param>
    /// <param name="Message">The explanation either way.</param>
    internal sealed record DpiVerdict(bool IsOwnerScale, string Message);

    /// <summary>One scheduled fine fraction and how far the display path moved the ring for it.</summary>
    /// <param name="Fraction">The scheduled sub-pixel fraction, in pixels, on both axes.</param>
    /// <param name="MeasuredX">The measured x movement, in pixels.</param>
    /// <param name="MeasuredY">The measured y movement, in pixels.</param>
    internal sealed record SweepRow(double Fraction, double MeasuredX, double MeasuredY)
    {
        internal double ErrorX => MeasuredX - Fraction;

        internal double ErrorY => MeasuredY - Fraction;
    }

    /// <summary>The sweep's report: its worst error and the finest step it resolves.</summary>
    /// <param name="MaxAbsErrorPx">The largest |measured − scheduled| over both axes, in pixels.</param>
    /// <param name="ResolvedStepPx">The finest resolved step, or NaN when even the coarsest is unresolved.</param>
    /// <param name="Note">A sentence for the session record.</param>
    internal sealed record SweepSummary(double MaxAbsErrorPx, double ResolvedStepPx, string Note);

    /// <summary>
    /// The control's presence and geometry statistics, over the annulus of the expected stroke:
    /// total weight, its weighted centroid, and the weight-weighted mean radius about that
    /// centroid. Covering the whole annulus is what makes these dash-phase-independent — see the
    /// class doc.
    /// </summary>
    private static AnnulusStatistics MeasureAnnulus(ProbeRaster weights, PhysicalPoint centre, double halfPx)
    {
        var fromX = Math.Max((int)Math.Floor(centre.X - halfPx - AnnulusHalfWidthPx), 0);
        var toX = Math.Min((int)Math.Ceiling(centre.X + halfPx + AnnulusHalfWidthPx), weights.Width - 1);
        var fromY = Math.Max((int)Math.Floor(centre.Y - halfPx - AnnulusHalfWidthPx), 0);
        var toY = Math.Min((int)Math.Ceiling(centre.Y + halfPx + AnnulusHalfWidthPx), weights.Height - 1);

        double mass = 0.0, weightedX = 0.0, weightedY = 0.0;
        var outer = halfPx + AnnulusHalfWidthPx;
        var inner = halfPx - AnnulusHalfWidthPx;
        for (var y = fromY; y <= toY; y++)
        {
            for (var x = fromX; x <= toX; x++)
            {
                var weight = weights.Pixels[(y * weights.Width + x) * 4 + 3];
                if (weight == 0) continue;

                var dx = x - centre.X;
                var dy = y - centre.Y;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance < inner || distance > outer) continue;

                mass += weight;
                weightedX += weight * x;
                weightedY += weight * y;
            }
        }

        if (mass <= 0.0)
            return new AnnulusStatistics(0.0, double.NaN, double.NaN, double.NaN);

        var centreX = weightedX / mass;
        var centreY = weightedY / mass;

        double radial = 0.0;
        for (var y = fromY; y <= toY; y++)
        {
            for (var x = fromX; x <= toX; x++)
            {
                var weight = weights.Pixels[(y * weights.Width + x) * 4 + 3];
                if (weight == 0) continue;

                var dx = x - centreX;
                var dy = y - centreY;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance < inner || distance > outer) continue;

                radial += weight * distance;
            }
        }

        return new AnnulusStatistics(mass, centreX, centreY, radial / mass);
    }

    /// <summary>The annulus statistics <see cref="CheckExpectedRing"/> judges.</summary>
    private sealed record AnnulusStatistics(double Mass, double CentreX, double CentreY, double MeanRadiusPx);

    private static double Cosine(ProbeRaster a, ProbeRaster b)
    {
        double dot = 0.0, normA = 0.0, normB = 0.0;
        for (var i = 3; i < a.Pixels.Length; i += 4)
        {
            double wa = a.Pixels[i];
            double wb = b.Pixels[i];
            dot += wa * wb;
            normA += wa * wa;
            normB += wb * wb;
        }

        if (normA <= 0.0 || normB <= 0.0)
            return 0.0;

        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    private static bool IsMultipleOf(double value, double step) =>
        Math.Abs(value / step - Math.Round(value / step)) < 1e-9;

    private static byte Median(byte[] values)
    {
        Array.Sort(values);
        var middle = values.Length / 2;
        return (byte)((values[middle - 1] + values[middle]) / 2);
    }
}
