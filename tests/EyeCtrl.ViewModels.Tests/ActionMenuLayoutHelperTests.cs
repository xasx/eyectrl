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
[Collection("DpiHelper Tests (ViewModels)")]
public class ActionMenuLayoutHelperTests : IDisposable
{
    private const double ScreenWidth = 1920;
    private const double ScreenHeight = 1080;

    /// <summary>
    /// Restores the process-global scale after every test in this class. The collection stops a
    /// write from landing mid-test in another class, but not from being LEFT behind: xUnit does
    /// not guarantee which [Theory] case runs last, so without this the next class in the
    /// collection could start at 1.25 or 2.0 instead of 1.0.
    /// </summary>
    public void Dispose() => DpiHelper.Initialize(1.0, 1.0);

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

    /// <summary>
    /// A screen too small to hold the button ring must still open the menu.
    ///
    /// The margin is <c>ringRadius + ActionMenuButtonRadius</c> scaled to PHYSICAL pixels, and it
    /// is passed to <c>Math.Clamp</c> as the minimum against <c>screen - margin</c> as the maximum.
    /// Once the margin passes half the screen those two invert, and Math.Clamp does not saturate —
    /// it THROWS ArgumentException. The throw lands in GazeEngineAdapter.OnShowActionMenu on the
    /// dispatcher, and App.xaml.cs deliberately leaves DispatcherUnhandledException unhandled so
    /// the process dies and the supervisor respawns it. For a user whose only input channel is
    /// this app, opening the action menu must never be able to do that.
    ///
    /// 1366x768 at 125% is a stock laptop panel, and this fires there at the DEFAULT CircleSize:
    /// margin 325 x 1.25 = 406.25 against a 384 half-height. It was reachable only above
    /// CircleSize ~360 before the ring grew on 2026-08-26, which is why no test caught it —
    /// every other test in the suite runs at scale 1.0 on 1920x1080, where 325 fits easily.
    /// </summary>
    [Theory]
    [InlineData(1.25, 1366, 768, 250.0)]   // stock laptop panel, default CircleSize
    [InlineData(2.0, 1920, 1080, 250.0)]   // 1920x1080 at 200%
    [InlineData(1.0, 800, 600, 500.0)]     // max CircleSize on a small screen
    public void AScreenTooSmallForTheRing_StillOpensTheMenu(
        double scale, double screenWidth, double screenHeight, double circleSize)
    {
        DpiHelper.Initialize(scale, scale);

        var clamped = ActionMenuLayoutHelper.CalculateDisplayPoint(
            new PhysicalPoint(screenWidth / 2, screenHeight / 2),
            ViewPosition.AtGaze,
            circleSize,
            screenWidth,
            screenHeight);

        // Degraded but alive: the menu pins to the screen centre rather than throwing.
        Check.That(clamped.X).IsEqualTo(screenWidth / 2);
        Check.That(clamped.Y).IsEqualTo(screenHeight / 2);
    }

    /// <summary>
    /// The cap is per-axis. A wide screen that is too short must still clamp X normally rather
    /// than collapsing both axes to the centre — otherwise a letterboxed display would throw away
    /// horizontal placement it could perfectly well honour.
    /// </summary>
    [Fact]
    public void TheFitCap_IsAppliedPerAxis_NotToBothAtOnce()
    {
        DpiHelper.Initialize(1.0, 1.0);

        // 2000 wide (margin 325 fits) x 500 tall (margin 325 does not).
        var clamped = ActionMenuLayoutHelper.CalculateDisplayPoint(
            new PhysicalPoint(0, 0),
            ViewPosition.AtGaze,
            circleSize: 250,
            screenWidthPhysical: 2000,
            screenHeightPhysical: 500);

        var ring = 250 / 2 + LayoutConstants.ActionMenuButtonRadius + LayoutConstants.ActionMenuButtonOuterGap;
        Check.That(clamped.X).IsEqualTo(ring + LayoutConstants.ActionMenuButtonRadius);
        Check.That(clamped.Y).IsEqualTo(250.0);
    }
}
