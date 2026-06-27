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
}
