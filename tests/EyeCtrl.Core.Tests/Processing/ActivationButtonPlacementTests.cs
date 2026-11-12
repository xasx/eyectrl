namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

/// <summary>
/// Ticket 7, part C: the pure placement function, at the user's geometry (3840×2400, ScaleX 2.0, ring
/// 250, the shipped 137.5 DIP offset; 55 before the 7th sitting, 2026-11-12). One test per rule the plan gives it; each names the production
/// one-liner mutation that turns it red.
/// </summary>
public class ActivationButtonPlacementTests
{
    private static readonly PhysicalRect Screen = new(0, 0, 3840, 2400);
    private const double Scale = 2.0;
    private const double RingDip = 250;
    private const double HitPx = LayoutConstants.ActivationButtonHitRadiusPx;
    private const double FixationR = 75;

    private static ActivationButtonPlacement.Result Place(
        PhysicalPoint point, IReadOnlyList<PhysicalRect>? chrome = null)
        => ActivationButtonPlacement.Place(
            point, RingDip, LayoutConstants.ActivationButtonOffset, Scale, HitPx, FixationR, Screen, chrome);

    [Fact]
    public void Place_IsRingEdgePlusOffsetFromP_OnTheDiagonalTowardTheScreenCentre()
    {
        // Mutation: drop the ring's half-size from the offset (place at 137.5 DIP only) → the distance
        // becomes 275 px instead of 525, red. (525 at ring 250; 360 before the 7th sitting's option B.)
        var result = Place(new PhysicalPoint(1000, 1000));

        Check.That(result.Placed).IsTrue();
        var b = result.ButtonCenter;
        Check.That(b.DistanceTo(new PhysicalPoint(1000, 1000))).IsCloseTo(525.0, 1e-9);
        // Ring 250/2 + 137.5 = 262.5 DIP, doubled by the scale; both axes carry 1/√2 of it.
        Check.That(b.X).IsCloseTo(1000 + (525 / Math.Sqrt(2)), 1e-9);
        Check.That(b.Y).IsCloseTo(1000 + (525 / Math.Sqrt(2)), 1e-9);
    }

    [Fact]
    public void Place_GoesTowardTheCentre_OnAllFourQuadrants()
    {
        // Mutation: fix the direction (always down-right) → the other three quadrants' assertions red.
        var topLeft = Place(new PhysicalPoint(1000, 1000)).ButtonCenter;
        Check.That(topLeft.X).IsStrictlyGreaterThan(1000);
        Check.That(topLeft.Y).IsStrictlyGreaterThan(1000);

        var topRight = Place(new PhysicalPoint(2800, 1000)).ButtonCenter;
        Check.That(topRight.X).IsStrictlyLessThan(2800);
        Check.That(topRight.Y).IsStrictlyGreaterThan(1000);

        var bottomLeft = Place(new PhysicalPoint(1000, 1800)).ButtonCenter;
        Check.That(bottomLeft.X).IsStrictlyGreaterThan(1000);
        Check.That(bottomLeft.Y).IsStrictlyLessThan(1800);

        var bottomRight = Place(new PhysicalPoint(2800, 1800)).ButtonCenter;
        Check.That(bottomRight.X).IsStrictlyLessThan(2800);
        Check.That(bottomRight.Y).IsStrictlyLessThan(1800);
    }

    [Fact]
    public void Place_FlipsToTheOppositeSideOnce_WhenThePreferredSideIntersectsChrome()
    {
        // Mutation: never flip (drop the second candidate) → Placed is false, red.
        var p = new PhysicalPoint(1000, 1000);
        var preferred = Place(p).ButtonCenter;

        // A zone covering the preferred button only.
        var zone = new PhysicalRect(preferred.X - 20, preferred.Y - 20, 40, 40);
        var result = Place(p, new[] { zone });

        Check.That(result.Placed).IsTrue();
        Check.That(result.ButtonCenter.DistanceTo(preferred)).IsStrictlyGreaterThan(1.0);
        // The flipped side is the mirror of the preferred one about P.
        Check.That(result.ButtonCenter.X).IsCloseTo((2 * p.X) - preferred.X, 1e-9);
        Check.That(result.ButtonCenter.Y).IsCloseTo((2 * p.Y) - preferred.Y, 1e-9);
    }

    [Fact]
    public void Place_DropsWhenTheFlippedSideIntersectsChromeToo()
    {
        // Mutation: place anyway after the flip fails → Placed is true, red.
        var p = new PhysicalPoint(1000, 1000);
        var preferred = Place(p).ButtonCenter;

        var result = Place(p, new[]
        {
            new PhysicalRect(preferred.X - 20, preferred.Y - 20, 40, 40),          // the preferred side
            new PhysicalRect((2 * p.X) - preferred.X - 20, (2 * p.Y) - preferred.Y - 20, 40, 40), // the flip
        });

        Check.That(result.Placed).IsFalse();
    }

    [Fact]
    public void Place_DropsAFlippedSideClampedOntoP()
    {
        // Review A, F1. Near a corner the flip points at the corner and the clamp pulls it back onto P:
        // B would sit inside P's hold radius, so a plain hold at P would fire the offer. Mutation:
        // remove the distance check in IsUsable (keep only the chrome test) → Placed is true, red.
        var p = new PhysicalPoint(200, 200);
        var preferred = Place(p).ButtonCenter;

        var result = Place(p, new[] { new PhysicalRect(preferred.X - 20, preferred.Y - 20, 40, 40) });

        Check.That(result.Placed).IsFalse();
        // The candidate that was rejected is the clamped corner one.
        Check.That(result.ButtonCenter.X).IsEqualTo(HitPx);
        Check.That(result.ButtonCenter.Y).IsEqualTo(HitPx);
    }

    [Fact]
    public void Place_ClampsSoTheWholeHitCircleStaysOnScreen()
    {
        // Mutation: return the unclamped centre → the flipped candidate below lands at (628.8, -321.2)
        // instead of on the screen margin, red. (Offset 525 px and hit 265 since the 7th sitting: the
        // flip clamps to 429.0 px from P, outside hit + R = 340, so it is placed.)
        var p = new PhysicalPoint(1000, 50);   // near the top edge; the preferred side faces down-right
        var preferred = Place(p).ButtonCenter;
        var result = Place(p, new[] { new PhysicalRect(preferred.X - 20, preferred.Y - 20, 40, 40) });

        Check.That(result.Placed).IsTrue();
        Check.That(result.ButtonCenter.Y).IsEqualTo(HitPx);   // clamped to the hit-radius margin
        Check.That(result.ButtonCenter.X).IsCloseTo((2 * p.X) - preferred.X, 1e-9);
    }

    [Fact]
    public void Place_OnAScreenSmallerThanTheHitCircle_CollapsesToTheCentre_WithoutThrowing()
    {
        // Math.Clamp would throw with its minimum above its maximum; a degenerate display must not take
        // the gaze pipeline down. The collapsed centre is P itself, so the drop rule then refuses the
        // offer — no throw, and no button under the hold. Mutation: restore the raw Math.Clamp → an
        // exception escapes.
        var tiny = new PhysicalRect(0, 0, 200, 200);
        var result = ActivationButtonPlacement.Place(
            new PhysicalPoint(100, 100), RingDip, LayoutConstants.ActivationButtonOffset, Scale,
            HitPx, FixationR, tiny);

        Check.That(result.Placed).IsFalse();
        Check.That(result.ButtonCenter.X).IsEqualTo(100);
        Check.That(result.ButtonCenter.Y).IsEqualTo(100);
    }

    [Fact]
    public void Place_SmallRingOrLowDpi_StillPlaces_JustOutsideTheHoldZone()
    {
        // Review A, R2-1: with the drop guard alone, a small ring or a 100 % display had no placeable
        // side at all, so activation would be silently impossible there. Mutation: remove the offset
        // floor (offsetPx stays ring/2 + 55 DIP) → both placements below return Placed == false, red;
        // floor at dropDistance WITHOUT the clearance (ticket 7 fixup-02 re-check, R2) → the exact
        // distances below land on hit + R and these close-to checks go red.
        // ScaleX 1.0 with ring 250, R75: the plan's offset is 262.5 px, inside 265 + 75.
        var lowScale = ActivationButtonPlacement.Place(
            new PhysicalPoint(1000, 1000), 250, LayoutConstants.ActivationButtonOffset, 1.0,
            HitPx, 75, Screen);
        Check.That(lowScale.Placed).IsTrue();
        Check.That(lowScale.ButtonCenter.DistanceTo(new PhysicalPoint(1000, 1000)))
            .IsCloseTo(HitPx + 75 + ActivationButtonPlacement.MinimumClearancePx, 1e-6);

        // ScaleX 2.0 with ring 60, R90: the plan's offset is 335 px, inside 265 + 90.
        var smallRing = ActivationButtonPlacement.Place(
            new PhysicalPoint(1000, 1000), 60, LayoutConstants.ActivationButtonOffset, 2.0,
            HitPx, 90, Screen);
        Check.That(smallRing.Placed).IsTrue();
        Check.That(smallRing.ButtonCenter.DistanceTo(new PhysicalPoint(1000, 1000)))
            .IsCloseTo(HitPx + 90 + ActivationButtonPlacement.MinimumClearancePx, 1e-6);

        // Ring 250, ScaleX 2.0, R90 is above the floor (385) and unchanged: 525 px since the 7th sitting.
        var user = ActivationButtonPlacement.Place(
            new PhysicalPoint(1000, 1000), 250, LayoutConstants.ActivationButtonOffset, 2.0,
            HitPx, 90, Screen);
        Check.That(user.ButtonCenter.DistanceTo(new PhysicalPoint(1000, 1000))).IsCloseTo(525.0, 1e-9);
    }

    [Fact]
    public void Place_DropsAClammedCandidateThatSitsBetweenHitAndHitPlusR()
    {
        // Review A, N2: the guard must use hit + R, not hit alone — a candidate 304 px from P is
        // outside the hit area (265) but inside the hold zone (340), where a hold's wobble toward B
        // could still reach it. Mutation: use buttonRadiusPx alone as the drop distance → this is
        // placed, red. (P moved from (360,360) with the 265-px hit: the flip clamps to (265,265).)
        var p = new PhysicalPoint(480, 480);
        var preferred = Place(p).ButtonCenter;
        var result = Place(p, new[] { new PhysicalRect(preferred.X - 20, preferred.Y - 20, 40, 40) });

        Check.That(result.ButtonCenter.DistanceTo(p)).IsCloseTo(304.06, 0.1);
        Check.That(result.Placed).IsFalse();
    }

    [Fact]
    public void Place_UsesTheShippedOffsetConstant()
    {
        // The offset moved to LayoutConstants (N9); if someone re-tunes it, this pins the placement's
        // distance to the same number the constants tests read. 137.5 since the 7th sitting (was 55).
        Check.That(LayoutConstants.ActivationButtonOffset).IsEqualTo(137.5);
        Check.That(Place(new PhysicalPoint(1000, 1000)).ButtonCenter
            .DistanceTo(new PhysicalPoint(1000, 1000)))
            .IsCloseTo(((RingDip / 2) + LayoutConstants.ActivationButtonOffset) * Scale, 1e-9);
    }

    // ── Screen corners (2026-10-17 corner fix, T1) ───────────────────────────
    //
    // The owner's report: no activation button in the screen corners. For an ON-SCREEN P the
    // placement is sound at every corner; this pins that property at the corner pixels and at the
    // insets the investigation measured, so the F1 clamp can only ever move P ON-screen from here.
    // Geometry per the fix ticket: 3840×2400 @2, ring 200, hit 150, R 80. |B-P| is 475 px since the 7th
    // sitting's 137.5 DIP offset (was 310).

    private const double CornerRingDip = 200;
    private const double CornerHitPx = 150;
    private const double CornerR = 80;

    /// <summary>The four corner settle points at <paramref name="inset"/> px into the screen; the
    /// corner pixels of 3840×2400 are 3839/2399.</summary>
    private static IEnumerable<(string Corner, PhysicalPoint P)> Corners(int inset)
    {
        yield return ("TL", new PhysicalPoint(inset, inset));
        yield return ("TR", new PhysicalPoint(Screen.Width - 1 - inset, inset));
        yield return ("BL", new PhysicalPoint(inset, Screen.Height - 1 - inset));
        yield return ("BR", new PhysicalPoint(Screen.Width - 1 - inset, Screen.Height - 1 - inset));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(75)]
    [InlineData(150)]
    [InlineData(300)]
    public void Place_AtEveryCornerInset_PlacesTheButton475PxInward_FullyOnScreen(int inset)
    {
        // Mutation: make the placement decline near a corner → Placed flips false.
        var toolbarZone = new PhysicalRect(1070, 0, 1700, 280);
        foreach (var (corner, p) in Corners(inset))
        {
            foreach (var zones in new IReadOnlyList<PhysicalRect>[]
                     { Array.Empty<PhysicalRect>(), new[] { toolbarZone } })
            {
                var label = $"{corner}@{inset}, {(zones.Count == 0 ? "no zones" : "toolbar zone")}";
                var result = ActivationButtonPlacement.Place(
                    p, CornerRingDip, LayoutConstants.ActivationButtonOffset, Scale,
                    CornerHitPx, CornerR, Screen, zones);

                Check.WithCustomMessage($"{label}: not placed").That(result.Placed).IsTrue();
                Check.WithCustomMessage($"{label}: |B-P|")
                    .That(result.ButtonCenter.DistanceTo(p)).IsCloseTo(475.0, 0.5);
                var b = result.ButtonCenter;
                Check.WithCustomMessage($"{label}: the hit circle is off-screen")
                    .That(b.X >= CornerHitPx && b.X <= Screen.Width - CornerHitPx
                       && b.Y >= CornerHitPx && b.Y <= Screen.Height - CornerHitPx).IsTrue();
                Check.WithCustomMessage($"{label}: the hit circle intersects a zone")
                    .That(ActivationButtonPlacement.IntersectsAnyZone(b, CornerHitPx, zones)).IsFalse();
            }
        }
    }

    // ── Placement B (7th sitting, 2026-11-12) ─────────────────────────────────
    //
    // The owner's option B: the button 165 px farther out at 200 % (137.5 DIP from the ring edge, 475 px from
    // P at ring 200: the distance the sitting's replay measured), on the same side as before, with the hit
    // grown to 265 px. The owner's geometry: 3840×2400
    // at 2.0, ring 200, R 80, the shipped constants.

    private const double OwnerRingDip = 200;
    private const double OwnerR = 80;

    /// <summary>Every quadrant's middle and every edge band (0 / 70 / 140 px from the edge, on both halves of
    /// the other axis).</summary>
    private static IEnumerable<(string Label, PhysicalPoint P)> QuadrantsAndEdgeBands()
    {
        yield return ("TL quadrant", new PhysicalPoint(960, 600));
        yield return ("TR quadrant", new PhysicalPoint(2880, 600));
        yield return ("BL quadrant", new PhysicalPoint(960, 1800));
        yield return ("BR quadrant", new PhysicalPoint(2880, 1800));
        foreach (var d in new[] { 0.0, 70.0, 140.0 })
            foreach (var along in new[] { 0.25, 0.75 })
            {
                yield return ($"top {d}@{along}", new PhysicalPoint(Screen.Width * along, d));
                yield return ($"bottom {d}@{along}", new PhysicalPoint(Screen.Width * along, Screen.Height - 1 - d));
                yield return ($"left {d}@{along}", new PhysicalPoint(d, Screen.Height * along));
                yield return ($"right {d}@{along}", new PhysicalPoint(Screen.Width - 1 - d, Screen.Height * along));
            }
    }

    [Fact]
    public void PlacementB_InEveryQuadrantAndEdgeBand_Is475PxTowardTheCentre_WithTheHitCircleOnScreen()
    {
        // Mutations: offset back to 105 or 55 → 410 / 310 px, red; hit back to 200 → the hit-circle and
        // |B-P| checks hold but the constant pin below is red; direction fixed or flipped → the sign checks
        // are red. 335.9 px per axis is more than the 265-px margin, so at 2.0 no edge band clamps or flips.
        Check.That(LayoutConstants.ActivationButtonHitRadiusPx).IsEqualTo(265.0);
        foreach (var (label, p) in QuadrantsAndEdgeBands())
        {
            var r = ActivationButtonPlacement.Place(p, OwnerRingDip, LayoutConstants.ActivationButtonOffset, 2.0,
                LayoutConstants.ActivationButtonHitRadiusPx, OwnerR, Screen);
            var b = r.ButtonCenter;
            double dx = p.X < Screen.Width / 2 ? 1 : -1, dy = p.Y < Screen.Height / 2 ? 1 : -1;
            Check.WithCustomMessage($"{label}: not placed").That(r.Placed).IsTrue();
            Check.WithCustomMessage($"{label}: |B-P|").That(b.DistanceTo(p)).IsCloseTo(475.0, 1e-6);
            Check.WithCustomMessage($"{label}: B is not toward the centre on x").That((b.X - p.X) * dx).IsCloseTo(475 / Math.Sqrt(2), 1e-6);
            Check.WithCustomMessage($"{label}: B is not toward the centre on y").That((b.Y - p.Y) * dy).IsCloseTo(475 / Math.Sqrt(2), 1e-6);
            double hit = LayoutConstants.ActivationButtonHitRadiusPx;
            Check.WithCustomMessage($"{label}: the hit circle is off-screen")
                .That(b.X >= hit && b.X <= Screen.Width - hit && b.Y >= hit && b.Y <= Screen.Height - hit).IsTrue();
        }
    }

    [Fact]
    public void PlacementB_AtTheTopEdge_BesideTheDockedToolbar_FlipsOrRefuses_NeverOffScreen()
    {
        // The 7th sitting's docked toolbar [1120,0 1600×280]. Beside it, in the top band, the preferred B's
        // 265-px hit circle reaches the toolbar: the flip goes up, clamps to the 265-px margin, and is placed
        // only when it is still outside hit + R of P. This pins the rule, unchanged, at the new sizes.
        var toolbar = new PhysicalRect(1120, 0, 1600, 280);
        double hit = LayoutConstants.ActivationButtonHitRadiusPx;
        foreach (var x in new[] { 900.0, 3000.0 })
            foreach (var y in new[] { 0.0, 50.0, 70.0, 100.0, 140.0 })
            {
                var p = new PhysicalPoint(x, y);
                var r = ActivationButtonPlacement.Place(p, OwnerRingDip, LayoutConstants.ActivationButtonOffset, 2.0,
                    hit, OwnerR, Screen, new[] { toolbar });
                var b = r.ButtonCenter;
                Check.WithCustomMessage($"({x},{y}): the candidate is off-screen")
                    .That(b.X >= hit && b.X <= Screen.Width - hit && b.Y >= hit && b.Y <= Screen.Height - hit).IsTrue();
                Check.WithCustomMessage($"({x},{y}): placed on the toolbar")
                    .That(r.Placed && ActivationButtonPlacement.IntersectsAnyZone(b, hit, new[] { toolbar })).IsFalse();
                Check.WithCustomMessage($"({x},{y}): placed inside hit + R of P")
                    .That(r.Placed && b.DistanceTo(p) <= hit + OwnerR).IsFalse();
                // Flipped up (away from the centre) and clamped to the top margin.
                Check.WithCustomMessage($"({x},{y}): the flipped candidate").That(b.Y).IsEqualTo(hit);
                // Placed exactly when the clamped flip clears hit + R: |B-P| = √(335.9² + (265 − y)²) > 345 —
                // at 475 px every top-band point clears it (at 410 px some were refused).
                double axis = 475 / Math.Sqrt(2);
                double expected = Math.Sqrt((axis * axis) + ((hit - y) * (hit - y)));
                Check.WithCustomMessage($"({x},{y}): |B-P|").That(b.DistanceTo(p)).IsCloseTo(expected, 0.5);
                Check.WithCustomMessage($"({x},{y}): placed").That(r.Placed).IsEqualTo(expected > hit + OwnerR);
            }
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    [InlineData(2.5)]
    [InlineData(3.0)]
    public void PlacementB_TheFloor_HoldsWithHit265_AtEveryRingSizeAndRTheSettingsAllow(double scale)
    {
        // The floor (no activation by rest): B is never within hit + R + 30 of P, at every ring size
        // (CursorSettings.CircleSize 10..500) and R (DwellSettings.FixationRadiusPixels 10..200) the settings
        // allow, with the shipped 265-px hit; above the floor B follows the ring edge + 137.5 DIP. Mutation:
        // drop the floor → the small rings at 1.0 put B at (ring/2 + 137.5) px, inside hit + R, red.
        double hit = LayoutConstants.ActivationButtonHitRadiusPx;
        var big = new PhysicalRect(0, 0, 7680, 4800);   // room for ring 500 at 3.0 without a clamp
        var p = new PhysicalPoint(3840, 2400);
        foreach (var ring in new[] { 10.0, 60.0, 100.0, 150.0, 200.0, 250.0, 300.0, 400.0, 500.0 })
            foreach (var r in new[] { 10.0, 40.0, 75.0, 80.0, 120.0, 160.0, 200.0 })
            {
                var res = ActivationButtonPlacement.Place(p, ring, LayoutConstants.ActivationButtonOffset, scale, hit, r, big);
                double floor = hit + r + ActivationButtonPlacement.MinimumClearancePx;
                double ringEdge = ((ring / 2) + LayoutConstants.ActivationButtonOffset) * scale;
                string label = $"ring {ring} R {r} @ {scale}";
                Check.WithCustomMessage($"{label}: not placed").That(res.Placed).IsTrue();
                Check.WithCustomMessage($"{label}: |B-P|").That(res.ButtonCenter.DistanceTo(p)).IsCloseTo(Math.Max(ringEdge, floor), 1e-6);
                Check.WithCustomMessage($"{label}: below the floor").That(res.ButtonCenter.DistanceTo(p)).IsGreaterOrEqualThan(floor - 1e-6);
            }
    }
}
