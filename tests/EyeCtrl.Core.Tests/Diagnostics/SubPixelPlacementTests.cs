namespace EyeCtrl.Core.Tests.Diagnostics;

using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;

/// <summary>
/// U3 — C1 placement: the physical top-left is rounded, not the centre, and exact .5 cases round
/// half away from zero (not to even).
/// </summary>
public class SubPixelPlacementTests
{
    [Fact]
    public void TopLeft_WhenNotSnapped_KeepsTheExactSubPixelPosition()
    {
        // Condition B. A half-size of 9.75 px (13 DIP × 1.5) leaves a fractional top-left that the
        // exact condition must pass through untouched — including a .5 midpoint.
        var fractional = SubPixelPlacement.TopLeft(new PhysicalPoint(100.25, 200.75), sizeDip: 13, scale: 1.5, snap: false);
        Check.That(fractional.X).IsCloseTo(90.5, 1e-12);
        Check.That(fractional.Y).IsCloseTo(191.0, 1e-12);

        var midpoint = SubPixelPlacement.TopLeft(new PhysicalPoint(16.5, 20.5), sizeDip: 12, scale: 1.0, snap: false);
        Check.That(midpoint).IsEqualTo(new PhysicalPoint(10.5, 14.5));
    }

    [Fact]
    public void TopLeft_WhenSnapped_RoundsTheTopLeftNotTheCentre()
    {
        // The half-size is non-integer in both cases (13 DIP × 1 → 6.5 px, 13 DIP × 1.5 → 9.75 px),
        // so rounding the centre instead of the top-left cannot land on the same answer.
        // Review 01 V6: the owner's size is 250 DIP today, but the setting is a double.
        var wholeCentre = SubPixelPlacement.TopLeft(new PhysicalPoint(100, 200), sizeDip: 13, scale: 1.0, snap: true);
        Check.That(wholeCentre).IsEqualTo(new PhysicalPoint(94, 194));    // 93.5/193.5 → 94/194

        var fractionalCentre = SubPixelPlacement.TopLeft(new PhysicalPoint(100.4, 200.6), sizeDip: 13, scale: 1.0, snap: true);
        Check.That(fractionalCentre).IsEqualTo(new PhysicalPoint(94, 194)); // 93.9/194.1 → 94/194

        var scaled = SubPixelPlacement.TopLeft(new PhysicalPoint(100, 200), sizeDip: 13, scale: 1.5, snap: true);
        Check.That(scaled).IsEqualTo(new PhysicalPoint(90, 190));          // 90.25/190.25 → 90/190
    }

    [Fact]
    public void TopLeft_WhenSnapped_RoundsHalfAwayFromZero()
    {
        // A top-left of exactly x.5 rounds away from zero, not to the even neighbour. The first case
        // is chosen so ToEven would round DOWN to an even 10; the negative case pins the away-from-
        // zero direction (a floor-based implementation would go to −23/−27 only by accident of sign).
        var downToEven = SubPixelPlacement.TopLeft(new PhysicalPoint(16.5, 20.5), sizeDip: 12, scale: 1.0, snap: true);
        Check.That(downToEven).IsEqualTo(new PhysicalPoint(11, 15));       // 10.5/14.5 → 11/15 (ToEven: 10/14)

        var upToOdd = SubPixelPlacement.TopLeft(new PhysicalPoint(18.5, 22.5), sizeDip: 12, scale: 1.0, snap: true);
        Check.That(upToOdd).IsEqualTo(new PhysicalPoint(13, 17));          // 12.5/16.5 → 13/17 (ToEven: 12/16)

        var negative = SubPixelPlacement.TopLeft(new PhysicalPoint(-16.5, -20.5), sizeDip: 12, scale: 1.0, snap: true);
        Check.That(negative).IsEqualTo(new PhysicalPoint(-23, -27));       // −22.5/−26.5 → −23/−27 (ToEven: −22/−26)
    }

    [Fact]
    public void TopLeft_WhenSnapped_LeavesWholePixelPositionsAlone()
    {
        var topLeft = SubPixelPlacement.TopLeft(new PhysicalPoint(100, 200), sizeDip: 12, scale: 1.0, snap: true);

        Check.That(topLeft).IsEqualTo(new PhysicalPoint(94, 194));
    }
}
