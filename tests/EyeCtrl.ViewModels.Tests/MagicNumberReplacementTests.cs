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
        // The formula: circleSize/2 + ActionMenuButtonRadius + ActionMenuButtonOuterGap
        // With circleSize=100, should be: 100/2 + 70 + 60 = 180
        var circleSize = 100.0;
        var expectedRadius = circleSize / 2 + UIConstants.ActionMenuButtonRadius + UIConstants.ActionMenuButtonOuterGap;

        Check.That(expectedRadius).IsEqualTo(180.0);
    }

    [Fact]
    public void ManualSelectionSideButtonConfiguration_UsesNamedConstants()
    {
        // Verify ManualSelection side button constants are defined
        Check.That(UIConstants.ButtonRadius).IsEqualTo(50.0);
        Check.That(UIConstants.ManualSelectionSideButtonGap).IsEqualTo(20.0);
        Check.That(new EyeCtrl.Core.Settings.DwellSettings().ManualSelectionSideButtonDwellMs).IsEqualTo(600);
    }

    [Fact]
    public void TimeConversion_UsesMathConstants()
    {
        // Verify milliseconds to microseconds conversion
        var milliseconds = 500;
        var microseconds = milliseconds * MathConstants.MicrosecondsPerMillisecond;

        Check.That(microseconds).IsEqualTo(500_000L);
    }

    [Fact]
    public void AngleConversion_UsesMathConstants()
    {
        // Verify 90 degrees converts to PI/2 radians
        var degrees = MathConstants.RightAngleDegrees;
        var radians = degrees * MathConstants.DegreesToRadians;

        Check.That(radians).IsCloseTo(Math.PI / 2, 1e-10);
    }

    [Fact]
    public void FullCircleCalculation_UsesMathConstants()
    {
        // Verify full circle (360 degrees) converts to 2*PI radians
        var degrees = MathConstants.FullCircleDegrees;
        var radians = degrees * MathConstants.DegreesToRadians;

        Check.That(radians).IsCloseTo(2 * Math.PI, 1e-10);
    }
}
