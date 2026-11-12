namespace EyeCtrl.App.Tests.Overlays;

using EyeCtrl.Controls;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using NFluent;
using Xunit;

/// <summary>
/// B2's two static geometry promises, headless and window-free: the drawn disc never covers the
/// cursor ring's line, and it is drawn inside the area the dwell is counted in. Both are checked
/// against the LIVE production diameter (<see cref="ActivationButtonFace.Diameter"/>), so a face that
/// grew — the ticket's 120 DIP mutant — fails the ring-clearance arithmetic as well as its render.
/// </summary>
public class ActivationButtonGeometryTests
{
    private static readonly PhysicalRect Screen = new(0, 0, 3840, 2400);

    /// <summary>Mid-screen, away from every clamp and chrome zone; the owner's 3840x2400 at 2.0.</summary>
    private static readonly PhysicalPoint P = new(1920, 1200);

    [Theory]
    // The setting's full range (CursorSettings.CircleSize 10..500), at the owner's two scales.
    [InlineData(10.0, 1.0)]
    [InlineData(60.0, 1.0)]
    [InlineData(100.0, 1.0)]
    [InlineData(150.0, 1.0)]
    [InlineData(200.0, 1.0)]
    [InlineData(250.0, 1.0)]
    [InlineData(300.0, 1.0)]
    [InlineData(400.0, 1.0)]
    [InlineData(500.0, 1.0)]
    [InlineData(10.0, 2.0)]
    [InlineData(60.0, 2.0)]
    [InlineData(100.0, 2.0)]
    [InlineData(150.0, 2.0)]
    [InlineData(200.0, 2.0)]
    [InlineData(250.0, 2.0)]
    [InlineData(300.0, 2.0)]
    [InlineData(400.0, 2.0)]
    [InlineData(500.0, 2.0)]
    public void TheDiscEdge_Stays87Point5DipClearOfTheRingLine(double ringSizeDip, double scale)
    {
        // The placement puts B at ring/2 + 137.5 DIP from P (7th sitting, option B; 55 before), so the disc's
        // edge (50 DIP radius) is 87.5 DIP clear of the ring's line (5 DIP before). Where the placement floor
        // (hit + R + 30 px) pushes B farther, the gap only grows. Mutation: grow the disc, or shrink the
        // offset → the gap goes under 87.5 DIP. (Before the 7th sitting this subtracted only the disc radius
        // from |B-P|, so it measured the gap to P, not to the ring line, and a shrunk offset still passed.)
        var result = ActivationButtonPlacement.Place(
            P, ringSizeDip, LayoutConstants.ActivationButtonOffset, scale,
            LayoutConstants.ActivationButtonHitRadiusPx, fixationRadiusPx: 75, Screen);

        Check.That(result.Placed).IsTrue();
        double gapPx = result.ButtonCenter.DistanceTo(P) - (ringSizeDip / 2 * scale)
            - (ActivationButtonFace.Diameter / 2 * scale);
        Check.WithCustomMessage(
                $"ring {ringSizeDip} DIP at {scale}x: the disc edge is {gapPx / scale:F2} DIP from the ring line")
            .That(gapPx).IsGreaterOrEqualThan(87.5 * scale - 1e-9);

        // Above the floor the gap is exactly 87.5 DIP: the offset, not the floor, places B.
        double floorPx = LayoutConstants.ActivationButtonHitRadiusPx + 75 + ActivationButtonPlacement.MinimumClearancePx;
        if (((ringSizeDip / 2) + LayoutConstants.ActivationButtonOffset) * scale > floorPx)
            Check.WithCustomMessage($"ring {ringSizeDip} DIP at {scale}x").That(gapPx / scale).IsCloseTo(87.5, 1e-6);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void TheDrawnDisc_StaysInsideTheCountedHitRadius(double scale)
    {
        // Drawn < counted: the dwell still counts LayoutConstants.ActivationButtonHitRadiusPx, and
        // the drawn disc is smaller. Mutation: draw the disc at the hit radius → the 2.0 arm is
        // exactly equal (red) and the 3.0 arm's face would already have broken ring clearance.
        double drawnRadiusPx = ActivationButtonFace.Diameter / 2 * scale;
        Check.That(drawnRadiusPx)
            .IsLessOrEqualThan(LayoutConstants.ActivationButtonHitRadiusPx);
        Check.That(LayoutConstants.ActivationButtonHitRadiusPx)
            .IsStrictlyGreaterThan(ActivationButtonFace.Diameter / 2);
    }
}
