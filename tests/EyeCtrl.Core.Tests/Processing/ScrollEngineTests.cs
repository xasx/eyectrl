namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

public class ScrollEngineTests
{
    [Fact]
    public void InsideDeadZone_ReturnsZero()
    {
        var anchor = new PhysicalPoint(1000, 1000);
        var gaze = new PhysicalPoint(1010, 1005);
        var (vx, vy) = ScrollEngine.ComputeVelocity(gaze, anchor, 1.0);
        Check.That(vx).IsCloseTo(0, 1e-6);
        Check.That(vy).IsCloseTo(0, 1e-6);
    }

    [Fact]
    public void StraightUp_ProducesVerticalOnly()
    {
        var anchor = new PhysicalPoint(1000, 1000);
        var gaze = new PhysicalPoint(1000, 700);
        var (vx, vy) = ScrollEngine.ComputeVelocity(gaze, anchor, 1.0);
        Check.That(vx).IsCloseTo(0, 1e-6);
        Check.That(vy).IsStrictlyGreaterThan(0);
    }

    [Fact]
    public void Diagonal_ProducesBothAxes()
    {
        var anchor = new PhysicalPoint(1000, 1000);
        var gaze = new PhysicalPoint(1400, 700);
        var (vx, vy) = ScrollEngine.ComputeVelocity(gaze, anchor, 1.0);
        Check.That(vx).IsStrictlyGreaterThan(0);
        Check.That(vy).IsStrictlyGreaterThan(0);
    }

    [Fact]
    public void SensitivityMultiplierScalesVelocity()
    {
        var anchor = new PhysicalPoint(1000, 1000);
        var gaze = new PhysicalPoint(1000, 900);
        var (vx1, vy1) = ScrollEngine.ComputeVelocity(gaze, anchor, 1.0);
        var (vx2, vy2) = ScrollEngine.ComputeVelocity(gaze, anchor, 2.0);
        Check.That(vx1).IsCloseTo(0, 1e-6);
        Check.That(vy2).IsCloseTo(vy1 * 2, 1e-3);
    }

    [Fact]
    public void NegativeOffset_ProducesNegativeVelocity()
    {
        var anchor = new PhysicalPoint(1000, 1000);
        var gaze = new PhysicalPoint(1000, 1400);
        var (vx, vy) = ScrollEngine.ComputeVelocity(gaze, anchor, 1.0);
        Check.That(vx).IsCloseTo(0, 1e-6);
        Check.That(vy).IsStrictlyLessThan(0);
    }

    [Fact]
    public void GazeAtAnchor_ReturnsZero()
    {
        var pt = new PhysicalPoint(1000, 1000);
        var (vx, vy) = ScrollEngine.ComputeVelocity(pt, pt, 1.0);
        Check.That(vx).IsCloseTo(0, 1e-6);
        Check.That(vy).IsCloseTo(0, 1e-6);
    }

    [Fact]
    public void GazeInsideStopZone_ReturnsZero()
    {
        var anchor = new PhysicalPoint(1000, 1000);
        var stopCenter = new PhysicalPoint(2000, 600);
        var gaze = new PhysicalPoint(2020, 610);
        var (vx, vy) = ScrollEngine.ComputeVelocity(gaze, anchor, 1.0, stopCenter, 50);
        Check.That(vx).IsCloseTo(0, 1e-6);
        Check.That(vy).IsCloseTo(0, 1e-6);
    }

    // ── G7 (scroll start fix, F-S3): the units overload ──────────────────────

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void TheDeadZone_IsPhysicalPx_AtEveryScale(double pxPerDip)
    {
        // A 70-px offset inside an 80-px dead zone is inside it at every scale: the zone is
        // physical px, never DPI-converted. Mutation: the dead zone back to the constant 30.
        var anchor = new PhysicalPoint(1000, 1000);
        var gaze = new PhysicalPoint(1070, 1000);
        var (vx, vy) = ScrollEngine.ComputeVelocity(
            gaze, anchor, 1.0, deadZonePx: 80, pxPerDip, stopZoneCenter: null, stopZoneRadiusPx: 0);
        Check.That(vx).IsCloseTo(0, 1e-6);
        Check.That(vy).IsCloseTo(0, 1e-6);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void TheGain_IsPerDip_SoTheSameDipsGiveTheSameVelocityAtEveryScale(double pxPerDip)
    {
        // A 100-DIP effective offset gives the same notches/sec at every scale: the proportional
        // band is in DIP. Mutation: drop the / pxPerDip → the magnitude scales with the DPI.
        var anchor = new PhysicalPoint(1000, 1000);
        const double deadZonePx = 80;
        const double effectiveDip = 100;
        var gaze = new PhysicalPoint(
            anchor.X + deadZonePx + (effectiveDip * pxPerDip), anchor.Y);
        var (vx, vy) = ScrollEngine.ComputeVelocity(
            gaze, anchor, 1.0, deadZonePx, pxPerDip, null, 0);
        double magnitude = Math.Sqrt((vx * vx) + (vy * vy));
        Check.That(magnitude).IsCloseTo(effectiveDip * ScrollEngine.DefaultPixelsToNotchesFactor, 1e-9);
        Check.That(vx).IsStrictlyGreaterThan(0);
        Check.That(vy).IsCloseTo(0, 1e-6);
    }

    [Fact]
    public void TheStopZone_TakesTheCallersRadius()
    {
        // 90 px from the stop centre with a 100-px radius (the button's own, at scale 2) is inside
        // it — the outer half of the button. Mutation: the stop zone back to 50.
        var anchor = new PhysicalPoint(1000, 1000);
        var (vx, vy) = ScrollEngine.ComputeVelocity(
            new PhysicalPoint(2090, 1000), anchor, 1.0, deadZonePx: 80, pxPerDip: 2.0,
            stopZoneCenter: new PhysicalPoint(2000, 1000), stopZoneRadiusPx: 100);
        Check.That(vx).IsCloseTo(0, 1e-6);
        Check.That(vy).IsCloseTo(0, 1e-6);
    }

    [Fact]
    public void TheLegacyOverload_KeepsTheLegacyUnits()
    {
        // The 3-/5-arg overload delegates with the 30-px dead zone at pxPerDip 1, so every existing
        // caller and test keeps its meaning.
        var anchor = new PhysicalPoint(1000, 1000);
        var (vx, vy) = ScrollEngine.ComputeVelocity(new PhysicalPoint(1031, 1000), anchor, 1.0);
        double expected = (31 - ScrollEngine.DeadZoneRadiusPixels) * ScrollEngine.DefaultPixelsToNotchesFactor;
        Check.That(Math.Sqrt((vx * vx) + (vy * vy))).IsCloseTo(expected, 1e-9);
    }
}
