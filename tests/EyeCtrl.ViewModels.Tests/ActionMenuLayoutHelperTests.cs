namespace EyeCtrl.ViewModels.Tests;

using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels.Helpers;

/// <summary>
/// Covers <see cref="ActionMenuLayoutHelper"/>, which had no tests until 2026-08-26 even though it
/// decides how far from a screen edge the menu is torn away from the point the user was looking at.
///
/// The clamp margin is <c>ringRadius + ActionMenuButtonRadius</c>, so the button radius enters the
/// figure twice — once inside the ring radius and once again as the outermost half-button. Raising
/// the radius by 20 therefore costs 40 DIP of margin, not 20, and that compounding is the thing
/// most likely to be got wrong by inspection.
/// </summary>
public class ActionMenuLayoutHelperTests
{
    private const double ScreenWidth = 1920;
    private const double ScreenHeight = 1080;

    [Fact]
    public void ClampMargin_CountsTheButtonRadiusTwice()
    {
        DpiHelper.Initialize(1.0, 1.0);

        // A gaze point hard against the top-left corner is clamped to exactly the margin.
        var clamped = ActionMenuLayoutHelper.CalculateDisplayPoint(
            new PhysicalPoint(0, 0),
            ViewPosition.AtGaze,
            circleSize: 250,
            ScreenWidth,
            ScreenHeight);

        var ring = 250 / 2 + LayoutConstants.ActionMenuButtonRadius + LayoutConstants.ActionMenuButtonOuterGap;
        var expected = ring + LayoutConstants.ActionMenuButtonRadius;

        Check.That(clamped.X).IsEqualTo(expected);
        Check.That(clamped.Y).IsEqualTo(expected);
    }

    [Fact]
    public void ClampMargin_IsSymmetric_AtTheBottomRight()
    {
        DpiHelper.Initialize(1.0, 1.0);

        var clamped = ActionMenuLayoutHelper.CalculateDisplayPoint(
            new PhysicalPoint(ScreenWidth, ScreenHeight),
            ViewPosition.AtGaze,
            circleSize: 250,
            ScreenWidth,
            ScreenHeight);

        var ring = 250 / 2 + LayoutConstants.ActionMenuButtonRadius + LayoutConstants.ActionMenuButtonOuterGap;
        var expected = ring + LayoutConstants.ActionMenuButtonRadius;

        Check.That(clamped.X).IsEqualTo(ScreenWidth - expected);
        Check.That(clamped.Y).IsEqualTo(ScreenHeight - expected);
    }

    [Fact]
    public void AGazePointWellInsideTheMargins_IsNotMoved()
    {
        DpiHelper.Initialize(1.0, 1.0);

        var clamped = ActionMenuLayoutHelper.CalculateDisplayPoint(
            new PhysicalPoint(960, 540),
            ViewPosition.AtGaze,
            circleSize: 250,
            ScreenWidth,
            ScreenHeight);

        Check.That(clamped.X).IsEqualTo(960.0);
        Check.That(clamped.Y).IsEqualTo(540.0);
    }

    [Fact]
    public void AtCenter_IgnoresTheGazePoint()
    {
        DpiHelper.Initialize(1.0, 1.0);

        var clamped = ActionMenuLayoutHelper.CalculateDisplayPoint(
            new PhysicalPoint(10, 10),
            ViewPosition.AtCenter,
            circleSize: 250,
            ScreenWidth,
            ScreenHeight);

        Check.That(clamped.X).IsEqualTo(ScreenWidth / 2);
        Check.That(clamped.Y).IsEqualTo(ScreenHeight / 2);
    }
}
