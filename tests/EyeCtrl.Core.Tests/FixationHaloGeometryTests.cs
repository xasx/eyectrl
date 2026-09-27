namespace EyeCtrl.Core.Tests;

using EyeCtrl.Core.Models;

/// <summary>
/// The halo geometry at the user's ScaleX 2.0 (final synthesis, step 1: a symmetric halo whose radius
/// runs R–2R DIP, no moving arc head). The production window divides the live scale out of the rule's
/// PHYSICAL radius; using R as DIP is the mutation these tests kill — at 2.0 it would double every
/// drawn radius, putting the feedback outside the hold it is supposed to mark.
/// </summary>
public class FixationHaloGeometryTests
{
    [Fact]
    public void InnerAndOuterRadius_AtScaleX2_Are37Point5And75Dip_ForR75()
    {
        Check.That(FixationHaloGeometry.InnerRadiusDip(75, 2.0)).IsCloseTo(37.5, 1e-10);
        Check.That(FixationHaloGeometry.OuterRadiusDip(75, 2.0)).IsCloseTo(75.0, 1e-10);
    }

    [Theory]
    [InlineData(0.0, 37.5)]
    [InlineData(0.25, 46.875)]
    [InlineData(0.5, 56.25)]
    [InlineData(1.0, 75.0)]
    public void Radius_InterpolatesInnerToOuter(double progress, double expectedDip)
    {
        Check.That(FixationHaloGeometry.RadiusDip(75, 2.0, progress)).IsCloseTo(expectedDip, 1e-10);
    }

    [Fact]
    public void Radius_ClampsProgressToThePinnedRange()
    {
        // The progress scalar comes from Core's age ledger; a numeric overshoot must not grow the halo
        // past the outer bound, and a negative must not shrink it below the hold radius.
        Check.That(FixationHaloGeometry.RadiusDip(75, 2.0, -0.5)).IsCloseTo(37.5, 1e-10);
        Check.That(FixationHaloGeometry.RadiusDip(75, 2.0, 1.5)).IsCloseTo(75.0, 1e-10);
    }

    [Fact]
    public void Radius_NeverShrinksBelowTheHoldRadius_AtAnyProgress()
    {
        // The advocate's bias correction: a halo that contracts toward a point converges on the
        // measured point and lures the eye off the target. R/ScaleX is the floor.
        for (double p = 0; p <= 1.0; p += 0.05)
            Check.That(FixationHaloGeometry.RadiusDip(75, 2.0, p))
                .IsGreaterOrEqualThan(FixationHaloGeometry.InnerRadiusDip(75, 2.0) - 1e-10);
    }

    [Fact]
    public void Radius_AtScaleX1_DoublesAcrossTheProgressRange()
    {
        Check.That(FixationHaloGeometry.RadiusDip(75, 1.0, 0.0)).IsCloseTo(75.0, 1e-10);
        Check.That(FixationHaloGeometry.RadiusDip(75, 1.0, 1.0)).IsCloseTo(150.0, 1e-10);
    }

    [Theory]
    [InlineData(0.0, 2.0)]
    [InlineData(-1.0, 2.0)]
    [InlineData(75.0, 0.0)]
    [InlineData(75.0, -2.0)]
    public void Radius_RejectsNonPositiveInputs(double radiusPixels, double scaleX)
    {
        ArgumentOutOfRangeException? ex = null;
        try { FixationHaloGeometry.RadiusDip(radiusPixels, scaleX, 0.5); }
        catch (ArgumentOutOfRangeException e) { ex = e; }
        Check.That(ex).IsNotNull();
    }
}
