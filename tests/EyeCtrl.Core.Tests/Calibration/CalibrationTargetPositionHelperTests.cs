namespace EyeCtrl.Core.Tests.Calibration;

using System.Linq;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Models;

public class CalibrationTargetPositionHelperTests
{
    [Fact]
    public void GetPositions_3Points_ReturnsTL_TR_BC()
    {
        var screen = new PhysicalPoint(1920, 1080);
        var positions = CalibrationTargetPositionHelper.GetPositions(3, screen);

        Check.That(positions).HasSize(3);

        var margin = 0.2;
        var expectedTL = new PhysicalPoint(screen.X * margin, screen.Y * margin);
        var expectedTR = new PhysicalPoint(screen.X * (1 - margin), screen.Y * margin);
        var expectedBC = new PhysicalPoint(screen.X * 0.5, screen.Y * (1 - margin));

        Check.That(positions).HasElementThatMatches(p => Dist(p, expectedTL) < 1);
        Check.That(positions).HasElementThatMatches(p => Dist(p, expectedTR) < 1);
        Check.That(positions).HasElementThatMatches(p => Dist(p, expectedBC) < 1);
    }

    [Fact]
    public void GetPositions_5Points_ReturnsFourCornersAndCenter()
    {
        var screen = new PhysicalPoint(1920, 1080);
        var positions = CalibrationTargetPositionHelper.GetPositions(5, screen);

        Check.That(positions).HasSize(5);
        Check.That(positions).HasElementThatMatches(p => Dist(p, new PhysicalPoint(384, 216)) < 1);
        Check.That(positions).HasElementThatMatches(p => Dist(p, new PhysicalPoint(1536, 216)) < 1);
        Check.That(positions).HasElementThatMatches(p => Dist(p, new PhysicalPoint(960, 540)) < 1);
        Check.That(positions).HasElementThatMatches(p => Dist(p, new PhysicalPoint(384, 864)) < 1);
        Check.That(positions).HasElementThatMatches(p => Dist(p, new PhysicalPoint(1536, 864)) < 1);
    }

    [Fact]
    public void GetPositions_9Points_Returns3x3Grid()
    {
        var positions = CalibrationTargetPositionHelper.GetPositions(9, new PhysicalPoint(1920, 1080));
        Check.That(positions).HasSize(9);
    }

    [Fact]
    public void GetPositions_AllHaveEdgeMargin()
    {
        var screen = new PhysicalPoint(1920, 1080);
        var margin = 0.2;
        for (int count = 3; count <= 16; count++)
        {
            var positions = CalibrationTargetPositionHelper.GetPositions(count, screen);
            foreach (var p in positions)
            {
                Check.That(p.X >= screen.X * margin - 0.01).IsTrue();
                Check.That(p.X <= screen.X * (1 - margin) + 0.01).IsTrue();
                Check.That(p.Y >= screen.Y * margin - 0.01).IsTrue();
                Check.That(p.Y <= screen.Y * (1 - margin) + 0.01).IsTrue();
            }
        }
    }

    [Fact]
    public void GetPositions_5To16Points_AlwaysCoverAllFourScreenExtremes()
    {
        var screen = new PhysicalPoint(1920, 1080);
        var minX = screen.X * 0.2; var maxX = screen.X * 0.8;
        var minY = screen.Y * 0.2; var maxY = screen.Y * 0.8;

        for (int count = 5; count <= 16; count++)
        {
            var positions = CalibrationTargetPositionHelper.GetPositions(count, screen);

            Check.WithCustomMessage($"count={count}: top row (minY) missing")
                .That(positions.Any(p => Math.Abs(p.Y - minY) < 1)).IsTrue();
            Check.WithCustomMessage($"count={count}: bottom row (maxY) missing")
                .That(positions.Any(p => Math.Abs(p.Y - maxY) < 1)).IsTrue();
            Check.WithCustomMessage($"count={count}: left column (minX) missing")
                .That(positions.Any(p => Math.Abs(p.X - minX) < 1)).IsTrue();
            Check.WithCustomMessage($"count={count}: right column (maxX) missing")
                .That(positions.Any(p => Math.Abs(p.X - maxX) < 1)).IsTrue();
        }
    }

    [Fact]
    public void GetPositions_IsDeterministic()
    {
        var screen = new PhysicalPoint(1920, 1080);
        for (int count = 3; count <= 16; count++)
        {
            var a = CalibrationTargetPositionHelper.GetPositions(count, screen);
            var b = CalibrationTargetPositionHelper.GetPositions(count, screen);
            Check.WithCustomMessage($"count={count} not deterministic")
                .That(a.SequenceEqual(b)).IsTrue();
        }
    }

    private static double Dist(PhysicalPoint a, PhysicalPoint b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
