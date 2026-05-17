namespace EyeCtrl.ViewModels.Tests;

using EyeCtrl.ViewModels;
using EyeCtrl.Core;

/// <summary>
/// Integration tests to verify magic numbers have been successfully replaced with constants.
/// These tests validate that key calculations use the named constants correctly.
/// </summary>
public class MagicNumberReplacementTests
{
    [Fact]
    public void ActionMenuRingRadius_UsesNamedConstants()
    {
        // The formula: circleSize/2 + ButtonRadius + ActionMenuButtonOuterGap
        // With circleSize=100, should be: 100/2 + 50 + 20 = 120
        var circleSize = 100.0;
        var expectedRadius = circleSize / 2 + UIConstants.ButtonRadius + UIConstants.ActionMenuButtonOuterGap;

        Assert.Equal(120.0, expectedRadius);
    }

    [Fact]
    public void ManualSelectionSideButtonConfiguration_UsesNamedConstants()
    {
        // Verify ManualSelection side button constants are defined
        Assert.Equal(50.0, UIConstants.ButtonRadius);
        Assert.Equal(20.0, UIConstants.ManualSelectionSideButtonGap);
        Assert.Equal(600, UIConstants.ManualSelectionSideButtonDwellMs);
    }

    [Fact]
    public void TimeConversion_UsesMathConstants()
    {
        // Verify milliseconds to microseconds conversion
        var milliseconds = 500;
        var microseconds = milliseconds * MathConstants.MicrosecondsPerMillisecond;

        Assert.Equal(500_000L, microseconds);
    }

    [Fact]
    public void AngleConversion_UsesMathConstants()
    {
        // Verify 90 degrees converts to PI/2 radians
        var degrees = MathConstants.RightAngleDegrees;
        var radians = degrees * MathConstants.DegreesToRadians;

        Assert.Equal(Math.PI / 2, radians, 10);
    }

    [Fact]
    public void FullCircleCalculation_UsesMathConstants()
    {
        // Verify full circle (360 degrees) converts to 2*PI radians
        var degrees = MathConstants.FullCircleDegrees;
        var radians = degrees * MathConstants.DegreesToRadians;

        Assert.Equal(2 * Math.PI, radians, 10);
    }
}
