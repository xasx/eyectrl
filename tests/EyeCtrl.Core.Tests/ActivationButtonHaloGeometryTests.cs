namespace EyeCtrl.Core.Tests;

using EyeCtrl.Core.Models;

/// <summary>
/// The activation button's halo geometry at the user's ScaleX 2.0 (ticket 8: a symmetric halo whose
/// radius runs from half the hit radius to the full hit radius, no moving arc head, outer bound at or
/// inside the counted area). The production window divides the live scale out of the PHYSICAL hit
/// radius; using the radius as DIP is the mutation these tests kill — at 2.0 it would double every
/// drawn radius, putting the feedback outside the area the dwell is counted within.
/// </summary>
public class ActivationButtonHaloGeometryTests
{
    private const double HitPx = LayoutConstants.ActivationButtonHitRadiusPx;

    [Fact]
    public void InnerAndOuterRadius_AtScaleX2_AreHalfAndFullHitRadiusInDip()
    {
        // 150 px hit radius at ScaleX 2.0: 37.5 DIP at progress 0, 75 DIP at progress 1.
        Check.That(ActivationButtonHaloGeometry.InnerRadiusDip(HitPx, 2.0)).IsCloseTo(37.5, 1e-10);
        Check.That(ActivationButtonHaloGeometry.OuterRadiusDip(HitPx, 2.0)).IsCloseTo(75.0, 1e-10);
    }

    [Theory]
    [InlineData(0.0, 37.5)]
    [InlineData(0.25, 46.875)]
    [InlineData(0.36, 51.0)]
    [InlineData(0.5, 56.25)]
    [InlineData(1.0, 75.0)]
    public void Radius_InterpolatesInnerToOuter(double progress, double expectedDip)
    {
        Check.That(ActivationButtonHaloGeometry.RadiusDip(HitPx, 2.0, progress)).IsCloseTo(expectedDip, 1e-10);
    }

    [Fact]
    public void Radius_ClampsProgressToThePinnedRange()
    {
        // The progress scalar comes from Core's button-dwell ledger; a numeric overshoot must not grow
        // the halo past the hit radius, and a negative must not shrink it below the inner floor.
        Check.That(ActivationButtonHaloGeometry.RadiusDip(HitPx, 2.0, -0.5)).IsCloseTo(37.5, 1e-10);
        Check.That(ActivationButtonHaloGeometry.RadiusDip(HitPx, 2.0, 1.5)).IsCloseTo(75.0, 1e-10);
    }

    [Fact]
    public void Radius_NeverShrinksBelowTheInnerFloor_AtAnyProgress()
    {
        // The advocate's bias correction: a halo that contracts toward a point converges on the
        // measured point and lures the eye off the target. Half the hit radius is the floor.
        for (double p = 0; p <= 1.0; p += 0.05)
            Check.That(ActivationButtonHaloGeometry.RadiusDip(HitPx, 2.0, p))
                .IsGreaterOrEqualThan(ActivationButtonHaloGeometry.InnerRadiusDip(HitPx, 2.0) - 1e-10);
    }

    [Fact]
    public void OuterRadius_NeverExceedsTheHitRadius_InPhysicalPixels()
    {
        // The acceptance row: at ScaleX 2.0 the halo's outer radius ≤ the hit radius. Drawn in DIP
        // and multiplied back by the scale, the outer bound is exactly the count area's edge.
        for (double scale = 1.0; scale <= 3.0; scale += 0.25)
            Check.That(ActivationButtonHaloGeometry.OuterRadiusDip(HitPx, scale) * scale)
                .IsLessOrEqualThan(HitPx);
    }

    [Fact]
    public void Radius_AtScaleX1_DoublesAcrossTheProgressRange()
    {
        Check.That(ActivationButtonHaloGeometry.RadiusDip(HitPx, 1.0, 0.0)).IsCloseTo(75.0, 1e-10);
        Check.That(ActivationButtonHaloGeometry.RadiusDip(HitPx, 1.0, 1.0)).IsCloseTo(150.0, 1e-10);
    }

    [Theory]
    [InlineData(0.0, 2.0)]
    [InlineData(-1.0, 2.0)]
    [InlineData(150.0, 0.0)]
    [InlineData(150.0, -2.0)]
    public void Radius_RejectsNonPositiveInputs(double radiusPixels, double scaleX)
    {
        ArgumentOutOfRangeException? ex = null;
        try { ActivationButtonHaloGeometry.RadiusDip(radiusPixels, scaleX, 0.5); }
        catch (ArgumentOutOfRangeException e) { ex = e; }
        Check.That(ex).IsNotNull();
    }
}
