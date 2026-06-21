namespace EyeCtrl.Core.Tests;

using EyeCtrl.Core;

/// <summary>
/// Tests to verify mathematical and conversion constants are defined.
/// </summary>
public class MathConstantsTests
{
    [Fact]
    public void DegreesToRadiansConversion_IsCorrect()
    {
        Check.That(MathConstants.DegreesToRadians).IsCloseTo(Math.PI / 180.0, 1e-10);
    }

    [Fact]
    public void FullCircleDegrees_Is360()
    {
        Check.That(MathConstants.FullCircleDegrees).IsEqualTo(360.0);
    }

    [Fact]
    public void RightAngleDegrees_Is90()
    {
        Check.That(MathConstants.RightAngleDegrees).IsEqualTo(90.0);
    }

    [Fact]
    public void StraightAngleDegrees_Is180()
    {
        Check.That(MathConstants.StraightAngleDegrees).IsEqualTo(180.0);
    }

    [Fact]
    public void MicrosecondsPerMillisecond_Is1000()
    {
        Check.That(MathConstants.MicrosecondsPerMillisecond).IsEqualTo(1000L);
    }

    [Fact]
    public void AngleConversion_WorksCorrectly()
    {
        // Test that a 90-degree angle converts to PI/2 radians
        var radians = MathConstants.RightAngleDegrees * MathConstants.DegreesToRadians;
        Check.That(radians).IsCloseTo(Math.PI / 2, 1e-10);
    }

    [Fact]
    public void FullCircleConversion_WorksCorrectly()
    {
        // Test that 360 degrees converts to 2*PI radians
        var radians = MathConstants.FullCircleDegrees * MathConstants.DegreesToRadians;
        Check.That(radians).IsCloseTo(2 * Math.PI, 1e-10);
    }
}
