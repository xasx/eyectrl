namespace EyeCtrl.App.Tests.Diagnostics;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EyeCtrl.App.Diagnostics.SubPixel;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>
/// R1 — the headless raster check for the sub-pixel probe ring (build-plan C4). It renders the
/// probe through <see cref="RenderTargetBitmap"/> on an STA thread; no window is shown and no input
/// is simulated, so it carries no UI category and CI runs it.
///
/// <para>
/// <b>What it proves.</b> At 192 DPI (scale 2.0) and 96 DPI, for all three styles: condition B's
/// ring moves by the applied fraction (±0.05 px), while condition A's ring lands on the whole pixel
/// C1's away-from-zero rounding predicts (0 / 1 / 1 px for f = 0.25 / 0.5 / 0.75, against its own
/// f = 0 reference). A second test handles the static park's non-integer half-size contract (250
/// DIP at scale 1.5), and a third reports the A-vs-B partial-coverage ("edge spread") count at the
/// owner's geometry.
/// </para>
///
/// <para>
/// <b>Measurement.</b> The ring moved is estimated by <see cref="ProbeRasterEstimator"/>, the
/// mirrored-block centroid estimator this class and R2 share — extracted here so the on-screen
/// test measures with the same code rather than a fork of it.
/// </para>
///
/// <para>
/// <b>Resolution.</b> <see cref="RenderTargetBitmap"/>'s software rasterizer places edges on a
/// <b>1/8-device-pixel grid</b> (review 01 measured a rectangle-edge sweep: the edge pixel's alpha
/// takes only the values 255/223/191/159/128/96/64/32/0), so R1 is evidence at 1/8-px resolution,
/// not continuity. The sweep's 0.25/0.5/0.75 fractions sit exactly on that grid, so their residual
/// is the estimator bias alone — at most 0.040 px across every measured style/DPI case, inside the
/// ±0.05 criterion. Off-grid fractions render on the grid (a scheduled 0.4 renders as 0.375, which
/// the sub-case measures as 0.3774), and on this rasterizer a scheduled 0.3 renders at 0.25; the
/// continuous 0.3-px floor can therefore only be shown on the hardware path, which is R2's job
/// (sweep fine fractions, not just 0.25/0.5/0.75). The per-case numbers are written to the test
/// output.
/// </para>
///
/// <para>
/// <b>Mutation proofs (applied to the real <see cref="ProbeRingVisual"/>, each confirmed red).</b>
/// m1: A never snaps (the <c>snap</c> argument is short-circuited to false) → A goes fractional.
/// m2: B always snaps → B stops moving by the fraction. m3: the injected scale is replaced by
/// <c>DpiHelper.ScaleX</c> (1.0 in the test process) → at 192 DPI the shift doubles (2f). m4:
/// <c>SnapsToDevicePixels = true</c> on the container → B renders snapped (measured 0/1/1).
/// m4′: positioning by <c>Canvas.Left</c>/<c>Top</c> with <c>UseLayoutRounding = true</c> instead
/// of the translate transform → B renders snapped (measured 0/0/1). m5: <c>CacheMode =
/// new BitmapCache()</c> → killed by the 96-DPI cases and the sub-case (the cache quantizes at DIP
/// resolution, i.e. the full device pixel at 96 DPI; at 192 DPI its half-pixel grid stays just
/// inside this estimator's 0.05 px tolerance). The extra re-anchor mutation: dropping
/// <see cref="ProbeRingVisual.PlaceStaticPark"/>'s re-anchoring collapses the 250-DIP-at-1.5 A–B
/// offset to 0.08 px (below the 0.3 px floor) → red here.
/// </para>
///
/// <para>
/// <b>Inherited flag pin (fixup 01, F1).</b> The scene parent is hostile by construction: it sets
/// <c>SnapsToDevicePixels = true</c> and <c>UseLayoutRounding = true</c>, both of which inherit, so
/// the probe's own explicit <c>false</c> values are the only thing keeping B fractional. Deleting
/// <see cref="ProbeRingVisual"/>'s <c>SnapsToDevicePixels = false;</c> with that parent present
/// turns B into A (0.000/0.993/0.993) and is confirmed red — without the parent the deletion is
/// undetectable, which is how the review caught it.
/// </para>
///
/// <para>
/// <b>Equivalent mutants (documented, not used).</b> Flipping <c>UseLayoutRounding = true</c>
/// <i>alone</i>, keeping the translate transform, does not change the rendering: layout rounding
/// quantizes the layout slot, and a render transform is applied after layout (review 01 measured
/// the shifts unchanged). The size floor is equivalent for the same reason as in
/// <c>ProbeRingParityTests</c> — <see cref="CursorSettings.CircleSize"/> cannot be constructed
/// below 10.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class ProbeRingRasterTests
{
    /// <summary>The rendered scene's size in DIPs; the 250-DIP ring plus margin on every side.</summary>
    private const double SceneDip = 320.0;

    private const double RingSizeDip = 250.0;
    private const double RingThickness = 1.0;
    private const double RingOpacity = 1.0;
    private const string RingColour = "#A5F3FC";

    /// <summary>The drift/live look; review 01's feasibility measurements were taken at this opacity.</summary>
    private const double SweepLookOpacity = ProbeRingVisual.QuietLookOpacity;

    /// <summary>R1's stated tolerance: ±0.05 px.</summary>
    private const double TolerancePixels = 0.05;

    private static readonly double[] Fractions = [0.25, 0.5, 0.75];

    private readonly ITestOutputHelper _output;

    public ProbeRingRasterTests(ITestOutputHelper output) => _output = output;

    public static TheoryData<CursorRingStyle, double> StyleAndDpiCases()
    {
        var cases = new TheoryData<CursorRingStyle, double>();
        foreach (var style in Enum.GetValues<CursorRingStyle>())
        {
            cases.Add(style, 192.0);
            cases.Add(style, 96.0);
        }
        return cases;
    }

    [StaTheory]
    [MemberData(nameof(StyleAndDpiCases))]
    public void ConditionB_MovesByTheFraction_AndConditionA_LandsOnWholePixels(CursorRingStyle style, double dpi)
    {
        var scale = dpi / 96.0;
        var half = RingSizeDip * scale / 2.0;

        // The f = 0 reference. At a whole-pixel physical half-size the reference top-left is a
        // whole pixel, so A and B must render their ring at the same place here.
        var basePoint = new PhysicalPoint(160.0 * scale, 160.0 * scale);
        var referenceB = Render(style, scale, basePoint, snap: false, staticPark: false);
        var referenceA = Render(style, scale, basePoint, snap: true, staticPark: false);
        var referenceRow = ProbeRasterEstimator.RoundToWholePixel(basePoint.Y);
        var referenceColumn = ProbeRasterEstimator.RoundToWholePixel(basePoint.X);
        var referenceShift = ProbeRasterEstimator.Shift(referenceB, referenceB, referenceRow, referenceColumn, basePoint, half);
        var referenceShiftA = ProbeRasterEstimator.Shift(referenceA, referenceB, referenceRow, referenceColumn, basePoint, half);

        Check.WithCustomMessage(
                $"With nothing to round, A and B must draw at the same place. Style {style} at {dpi:0} DPI: "
                + $"A sits at ({referenceShiftA.X:0.0000}, {referenceShiftA.Y:0.0000}) px from B.")
            .That(Math.Abs(referenceShiftA.X) + Math.Abs(referenceShiftA.Y))
            .IsCloseTo(0.0, TolerancePixels);

        foreach (var fraction in Fractions)
        {
            var centre = new PhysicalPoint(basePoint.X + fraction, basePoint.Y + fraction);
            var b = Render(style, scale, centre, snap: false, staticPark: false);
            var a = Render(style, scale, centre, snap: true, staticPark: false);
            var row = ProbeRasterEstimator.RoundToWholePixel(centre.Y);
            var column = ProbeRasterEstimator.RoundToWholePixel(centre.X);

            var shiftB = ProbeRasterEstimator.Shift(b, referenceB, row, column, basePoint, half);
            var shiftA = ProbeRasterEstimator.Shift(a, referenceA, row, column, basePoint, half);

            _output.WriteLine(
                $"R1 {style} dpi={dpi:0} f={fraction:0.00}: "
                + $"B=({shiftB.X:0.0000}, {shiftB.Y:0.0000}) A=({shiftA.X:0.0000}, {shiftA.Y:0.0000})");

            Check.WithCustomMessage(
                    $"Condition B must render fractionally: the ring should move by f = {fraction:0.00} px "
                    + $"at {dpi:0} DPI, style {style}, but moved ({shiftB.X:0.0000}, {shiftB.Y:0.0000}) px. "
                    + "If either axis is 0 or 1, something is snapping the ring to device pixels.")
                .That(shiftB.X).IsCloseTo(fraction, TolerancePixels);
            Check.WithCustomMessage(
                    $"Condition B must render fractionally on both axes: f = {fraction:0.00} at {dpi:0} DPI, "
                    + $"style {style}, but the vertical move measured {shiftB.Y:0.0000} px.")
                .That(shiftB.Y).IsCloseTo(fraction, TolerancePixels);
            // C1 rounds half away from zero, and at this whole-pixel half-size the top-left's
            // fraction is exactly f, so A's move is 0 / 1 / 1 for 0.25 / 0.5 / 0.75 (F4: pinning
            // the rounding mode, not just "a whole pixel").
            var expectedShiftA = Math.Round(fraction, MidpointRounding.AwayFromZero);
            Check.WithCustomMessage(
                    $"Condition A must land on whole device pixels, rounding half away from zero: its left edge "
                    + $"should move {expectedShiftA:0} px for f = {fraction:0.00} at {dpi:0} DPI, style {style}, "
                    + $"but moved {shiftA.X:0.0000} px.")
                .That(shiftA.X).IsCloseTo(expectedShiftA, TolerancePixels);
            Check.WithCustomMessage(
                    $"Condition A must land on whole device pixels, rounding half away from zero: its top edge "
                    + $"should move {expectedShiftA:0} px for f = {fraction:0.00} at {dpi:0} DPI, style {style}, "
                    + $"but moved {shiftA.Y:0.0000} px.")
                .That(shiftA.Y).IsCloseTo(expectedShiftA, TolerancePixels);
        }
    }

    /// <summary>
    /// The static park at a non-integer physical half-size: 250 DIP at scale 1.5 gives a half-size
    /// of 187.5 px, where a fraction set on the <b>centre</b> would not survive to the top-left that
    /// A rounds. At a 0.4 fraction the exact top-left sits on a 0.9 fraction (A and B only 0.1 px
    /// apart — a trial that tests nothing); the re-anchoring must put the scheduled fraction on the
    /// top-left instead, so B moves by f and A stays min(f, 1−f) away on each axis — SP-1's
    /// 0.3–0.5 px guarantee. 0.4 and 0.7 are the two fractions exercised: one where A rounds down
    /// and one where it rounds up. (The measured error at these two points is ≤0.033 px; the larger
    /// error seen at a 0.6 fraction is mostly RenderTargetBitmap's 1/8-px edge grid — 0.6 renders
    /// as 0.625 — plus the thin band's composite-edge gain. The grid is visible on-grid too: the
    /// 0.4 fraction renders at 0.375, which is why B's measured move is 0.3774, not 0.4. On this
    /// rasterizer a scheduled 0.3 renders at 0.25, so the design's 0.3-px floor is only provable on
    /// the hardware path — R2 should sweep fine fractions.)
    /// </summary>
    [StaFact]
    public void StaticPark_AtANonIntegerHalfSize_PutsTheScheduledFractionOnTheTopLeft()
    {
        const double scale = 1.5;
        var half = RingSizeDip * scale / 2.0;

        foreach (var fraction in new[] { 0.4, 0.7 })
        {
            var scheduled = new PhysicalPoint(240.0 + fraction, 240.0 + fraction);
            var anchor = new PhysicalPoint(Math.Floor(scheduled.X - half), Math.Floor(scheduled.Y - half));

            // C4 parks the top-left at anchor + fraction, so the park's centre — and the windows
            // the cardinal edges are measured in — sits at anchor + fraction + half.
            var parkedCentre = new PhysicalPoint(anchor.X + fraction + half, anchor.Y + fraction + half);
            var row = ProbeRasterEstimator.RoundToWholePixel(parkedCentre.Y);
            var column = ProbeRasterEstimator.RoundToWholePixel(parkedCentre.X);

            var b = Render(style: CursorRingStyle.BiTonalSegments, scale, scheduled, snap: false, staticPark: true);
            var a = Render(style: CursorRingStyle.BiTonalSegments, scale, scheduled, snap: true, staticPark: true);

            // References with fraction 0 at the same anchor: only one axis moves, so the other
            // axis' edges (and hence the scan row/column and its windows) stay put. A centre with a
            // whole fraction whose top-left floors to the anchor is the next integer at or above
            // anchor + half.
            var fractionZeroX = Render(
                style: CursorRingStyle.BiTonalSegments, scale,
                new PhysicalPoint(Math.Ceiling(anchor.X + half), scheduled.Y), snap: false, staticPark: true);
            var fractionZeroY = Render(
                style: CursorRingStyle.BiTonalSegments, scale,
                new PhysicalPoint(scheduled.X, Math.Ceiling(anchor.Y + half)), snap: false, staticPark: true);

            var shiftBX = ProbeRasterEstimator.ShiftX(b, fractionZeroX, row, parkedCentre.X, half);
            var shiftBY = ProbeRasterEstimator.ShiftY(b, fractionZeroY, column, parkedCentre.Y, half);
            var offsetX = Math.Abs(ProbeRasterEstimator.ShiftX(a, b, row, parkedCentre.X, half));
            var offsetY = Math.Abs(ProbeRasterEstimator.ShiftY(a, b, column, parkedCentre.Y, half));

            // What the naive centre-anchored placement would have produced at this geometry, for
            // the record: the top-left lands on a fraction 0.5 lower and the A-B gap collapses
            // below the 0.3 px floor the design depends on.
            var naiveB = Render(style: CursorRingStyle.BiTonalSegments, scale, scheduled, snap: false, staticPark: false);
            var naiveA = Render(style: CursorRingStyle.BiTonalSegments, scale, scheduled, snap: true, staticPark: false);
            var naiveOffset = Math.Abs(ProbeRasterEstimator.ShiftX(naiveA, naiveB, row, scheduled.X, half));

            var expectedOffset = Math.Min(fraction, 1.0 - fraction);
            _output.WriteLine(
                $"R1 static park 250 DIP @ scale 1.5, scheduled fraction {fraction:0.00}: "
                + $"B move=({shiftBX:0.0000}, {shiftBY:0.0000}) expected=({fraction:0.00}, {fraction:0.00}); "
                + $"A-B offset=({offsetX:0.0000}, {offsetY:0.0000}) expected {expectedOffset:0.00}; "
                + $"naive A-B offset={naiveOffset:0.0000}");

            // The A-B offset is checked first: it is the decisive discriminator against a
            // centre-anchored park (which would collapse it to ~0.1 px), and it does not depend on
            // the fraction-zero reference below sharing the park's anchoring.
            Check.WithCustomMessage(
                    $"A must stay min(f, 1-f) = {expectedOffset:0.00} px from B on x at a non-integer half-size, "
                    + $"inside SP-1's 0.3-0.5 px guarantee; measured {offsetX:0.0000} px.")
                .That(offsetX).IsCloseTo(expectedOffset, TolerancePixels);
            Check.WithCustomMessage(
                    $"A must stay min(f, 1-f) = {expectedOffset:0.00} px from B on y at a non-integer half-size, "
                    + $"inside SP-1's 0.3-0.5 px guarantee; measured {offsetY:0.0000} px.")
                .That(offsetY).IsCloseTo(expectedOffset, TolerancePixels);
            Check.WithCustomMessage(
                    $"The scheduled fraction must land on the top-left: B should move by {fraction:0.00} px in x "
                    + $"at a 187.5 px half-size, but moved {shiftBX:0.0000} px. If this tracks the naive offset "
                    + $"({naiveOffset:0.0000}), the fraction was anchored on the centre (the pre-contract behaviour).")
                .That(shiftBX).IsCloseTo(fraction, TolerancePixels);
            Check.WithCustomMessage(
                    $"The scheduled fraction must land on the top-left: B should move by {fraction:0.00} px in y, "
                    + $"but moved {shiftBY:0.0000} px.")
                .That(shiftBY).IsCloseTo(fraction, TolerancePixels);
        }
    }

    /// <summary>
    /// The re-anchoring must be a no-op at the owner's geometry (250 DIP at scale 2.0, a whole-pixel
    /// 250 px half-size) — the case the whole test design is validated on. The two placement paths
    /// must produce byte-identical rasters there.
    /// </summary>
    [StaFact]
    public void StaticPark_AtTheOwnerGeometry_IsIdenticalToPlainPlacement()
    {
        var scheduled = new PhysicalPoint(320.5, 320.5);

        var parked = Render(
            style: CursorRingStyle.BiTonalSegments, scale: 2.0, scheduled, snap: false, staticPark: true);
        var plain = Render(
            style: CursorRingStyle.BiTonalSegments, scale: 2.0, scheduled, snap: false, staticPark: false);

        var differing = DifferingPixels(parked, plain);
        Check.WithCustomMessage(
                $"At the owner's geometry the static park and the plain placement must be the same placement "
                + $"({differing} of {parked.Width * parked.Height} pixels differ).")
            .That(differing).IsEqualTo(0);
    }

    /// <summary>
    /// Reports the A-vs-B edge spread at the owner's geometry (review 01 V1's record): the count of
    /// pixels with partial alpha — neither transparent nor at the image's maximum — over the whole
    /// render, at the static block's pinned look. It is a report, with only a non-vacuity
    /// assertion: the numbers go to the test output for the session record.
    /// </summary>
    [StaFact]
    public void EdgeSpread_AtTheOwnerGeometry_IsReported()
    {
        foreach (var style in Enum.GetValues<CursorRingStyle>())
        {
            var a = Render(
                style, scale: 2.0, new PhysicalPoint(320.0, 320.0),
                snap: true, staticPark: true, lookOpacity: ProbeRingVisual.PinnedLookOpacity);
            var counts = new List<int> { CountPartialAlphaPixels(a) };

            foreach (var fraction in new[] { 0.3, 0.5, 0.7 })
            {
                var b = Render(
                    style, scale: 2.0, new PhysicalPoint(320.0 + fraction, 320.0 + fraction),
                    snap: false, staticPark: true, lookOpacity: ProbeRingVisual.PinnedLookOpacity);
                counts.Add(CountPartialAlphaPixels(b));
            }

            _output.WriteLine(
                $"R1 edge spread {style} (pinned look, 250 DIP @ 2.0, partial-alpha px of {a.Width * a.Height}): "
                + $"A={counts[0]} B(0.3)={counts[1]} B(0.5)={counts[2]} B(0.7)={counts[3]}");

            Check.WithCustomMessage(
                    $"The {style} edge-spread report must not be vacuous: a partial-alpha count of 0 means "
                    + "the render has no antialiased edge at all.")
                .That(counts).ContainsOnlyElementsThatMatch(c => c > 0);
        }
    }

    // ── rendering ──

    private static ProbeRaster Render(
        CursorRingStyle style,
        double scale,
        PhysicalPoint centrePhys,
        bool snap,
        bool staticPark,
        double lookOpacity = SweepLookOpacity)
    {
        var settings = new CursorSettings
        {
            RingStyle = style,
            CircleSize = RingSizeDip,
            CircleThickness = RingThickness,
            CircleOpacity = RingOpacity,
            CircleColor = RingColour,
        };

        var probe = new ProbeRingVisual(scale, settings, lookOpacity);
        if (staticPark)
            probe.PlaceStaticPark(centrePhys, snap);
        else
            probe.Place(centrePhys, snap);

        // The parent is hostile by construction (F1): both flags inherit, so the probe's own
        // explicit false values are the only thing keeping B fractional. A parent that turns
        // SnapsToDevicePixels on — an ancestor in SP-4b's real window could — must not silently
        // make the A/B test A/A.
        var scene = new Canvas
        {
            Width = SceneDip,
            Height = SceneDip,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
        };
        scene.Children.Add(probe);
        scene.Measure(new Size(SceneDip, SceneDip));
        scene.Arrange(new Rect(0, 0, SceneDip, SceneDip));
        scene.UpdateLayout();

        var pixelWidth = (int)Math.Round(SceneDip * scale);
        var pixelHeight = (int)Math.Round(SceneDip * scale);
        var bitmap = new RenderTargetBitmap(
            pixelWidth, pixelHeight, 96.0 * scale, 96.0 * scale, PixelFormats.Pbgra32);
        bitmap.Render(scene);

        var stride = pixelWidth * 4;
        var pixels = new byte[stride * pixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return new ProbeRaster(pixels, pixelWidth, pixelHeight);
    }

    /// <summary>Pixels with alpha strictly between 0 and the image's maximum: partial coverage only.</summary>
    private static int CountPartialAlphaPixels(ProbeRaster raster)
    {
        byte maximum = 0;
        for (var i = 3; i < raster.Pixels.Length; i += 4)
        {
            if (raster.Pixels[i] > maximum)
                maximum = raster.Pixels[i];
        }

        var partial = 0;
        for (var i = 3; i < raster.Pixels.Length; i += 4)
        {
            if (raster.Pixels[i] > 0 && raster.Pixels[i] < maximum)
                partial++;
        }

        return partial;
    }

    private static int DifferingPixels(ProbeRaster a, ProbeRaster b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
            throw new InvalidOperationException(
                $"Rasters differ in size: {a.Width}x{a.Height} vs {b.Width}x{b.Height}.");

        var differing = 0;
        for (var i = 0; i < a.Pixels.Length; i += 4)
        {
            if (a.Pixels[i] != b.Pixels[i]
                || a.Pixels[i + 1] != b.Pixels[i + 1]
                || a.Pixels[i + 2] != b.Pixels[i + 2]
                || a.Pixels[i + 3] != b.Pixels[i + 3])
            {
                differing++;
            }
        }

        return differing;
    }
}
