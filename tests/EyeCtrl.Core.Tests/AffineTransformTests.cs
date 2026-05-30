namespace EyeCtrl.Core.Tests.Calibration;

using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Models;

public class AffineTransformTests
{
    [Fact]
    public void Fit_Identity_ReturnsNearIdentity()
    {
        var points = new CalibrationPoint[]
        {
            new(new PhysicalPoint(0, 0), new PhysicalPoint(0, 0), 0, 0, 30),
            new(new PhysicalPoint(1920, 0), new PhysicalPoint(1920, 0), 0, 0, 30),
            new(new PhysicalPoint(0, 1080), new PhysicalPoint(0, 1080), 0, 0, 30),
        };

        var transform = AffineTransform.Fit(points);

        var result = transform.Apply(new PhysicalPoint(960, 540));
        Assert.True(Math.Abs(result.X - 960) < 1.0);
        Assert.True(Math.Abs(result.Y - 540) < 1.0);
    }

    [Fact]
    public void Fit_TranslationOffset_Corrects()
    {
        var points = new CalibrationPoint[]
        {
            new(new PhysicalPoint(100, 100), new PhysicalPoint(110, 80), 0, 0, 30),
            new(new PhysicalPoint(1820, 100), new PhysicalPoint(1830, 80), 0, 0, 30),
            new(new PhysicalPoint(100, 980), new PhysicalPoint(110, 960), 0, 0, 30),
        };

        var transform = AffineTransform.Fit(points);

        // Gaze at (970, 90): transform should map it near target (960, 110)
        // Calibration: TargetY = GazeY + 20, so 90 + 20 = 110
        var result = transform.Apply(new PhysicalPoint(970, 90));
        Assert.True(Math.Abs(result.X - 960) < 1.5);
        Assert.True(Math.Abs(result.Y - 110) < 1.5);
    }

    [Fact]
    public void GetParameters_SetParameters_RoundTrips()
    {
        var points = new CalibrationPoint[]
        {
            new(new PhysicalPoint(0, 0), new PhysicalPoint(10, 20), 0, 0, 30),
            new(new PhysicalPoint(1920, 0), new PhysicalPoint(1930, 20), 0, 0, 30),
            new(new PhysicalPoint(0, 1080), new PhysicalPoint(10, 1100), 0, 0, 30),
        };

        var t1 = AffineTransform.Fit(points);
        var original = t1.Apply(new PhysicalPoint(500, 500));

        var t2 = new AffineTransform();
        t2.SetParameters(t1.GetParameters());

        var roundtripped = t2.Apply(new PhysicalPoint(500, 500));
        Assert.Equal(original.X, roundtripped.X, 6);
        Assert.Equal(original.Y, roundtripped.Y, 6);
    }

    [Fact]
    public void Fit_Throws_WhenLessThan3Points()
    {
        var tooFew = new CalibrationPoint[]
        {
            new(new PhysicalPoint(0, 0), new PhysicalPoint(5, 5), 0, 0, 30),
            new(new PhysicalPoint(1920, 1080), new PhysicalPoint(1920, 1080), 0, 0, 30),
        };

        Assert.Throws<ArgumentException>(() => AffineTransform.Fit(tooFew));
    }
}
