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
        Assert.Equal(Math.PI / 180.0, MathConstants.DegreesToRadians, 10);
    }

    [Fact]
    public void FullCircleDegrees_Is360()
    {
        Assert.Equal(360.0, MathConstants.FullCircleDegrees);
    }

    [Fact]
    public void RightAngleDegrees_Is90()
    {
        Assert.Equal(90.0, MathConstants.RightAngleDegrees);
    }

    [Fact]
    public void StraightAngleDegrees_Is180()
    {
        Assert.Equal(180.0, MathConstants.StraightAngleDegrees);
    }

    [Fact]
    public void MicrosecondsPerMillisecond_Is1000()
    {
        Assert.Equal(1000L, MathConstants.MicrosecondsPerMillisecond);
    }

    [Fact]
    public void AngleConversion_WorksCorrectly()
    {
        // Test that a 90-degree angle converts to PI/2 radians
        var radians = MathConstants.RightAngleDegrees * MathConstants.DegreesToRadians;
        Assert.Equal(Math.PI / 2, radians, 10);
    }

    [Fact]
    public void FullCircleConversion_WorksCorrectly()
    {
        // Test that 360 degrees converts to 2*PI radians
        var radians = MathConstants.FullCircleDegrees * MathConstants.DegreesToRadians;
        Assert.Equal(2 * Math.PI, radians, 10);
    }
}
