namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

/// <summary>
/// Ticket 7, part C: the pure placement function, at the user's geometry (3840×2400, ScaleX 2.0, ring
/// 250, the shipped 55 DIP offset). One test per rule the plan gives it; each names the production
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
        // Mutation: drop the ring's half-size from the offset (place at 55 DIP only) → the distance
        // becomes 110 px instead of 360, red.
        var result = Place(new PhysicalPoint(1000, 1000));

        Check.That(result.Placed).IsTrue();
        var b = result.ButtonCenter;
        Check.That(b.DistanceTo(new PhysicalPoint(1000, 1000))).IsCloseTo(360.0, 1e-9);
        // Ring 250/2 + 55 = 180 DIP, doubled by the scale; both axes carry 1/√2 of it.
        Check.That(b.X).IsCloseTo(1000 + (360 / Math.Sqrt(2)), 1e-9);
        Check.That(b.Y).IsCloseTo(1000 + (360 / Math.Sqrt(2)), 1e-9);
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
        // Mutation: return the unclamped centre → the flipped candidate below lands at (-54.6, -154.6)
        // instead of on the screen margin, red.
        var p = new PhysicalPoint(1000, 100);   // near the top edge; the preferred side faces down-right
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
    public void Place_UsesTheShippedOffsetConstant()
    {
        // The 55 DIP offset moved to LayoutConstants (N9); if someone re-tunes it, this pins the
        // placement's distance to the same number the constants tests read.
        Check.That(LayoutConstants.ActivationButtonOffset).IsEqualTo(55.0);
        Check.That(Place(new PhysicalPoint(1000, 1000)).ButtonCenter
            .DistanceTo(new PhysicalPoint(1000, 1000)))
            .IsCloseTo(((RingDip / 2) + LayoutConstants.ActivationButtonOffset) * Scale, 1e-9);
    }
}
