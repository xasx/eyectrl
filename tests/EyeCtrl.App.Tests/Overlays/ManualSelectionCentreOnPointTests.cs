namespace EyeCtrl.App.Tests.Overlays;

using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using Xunit;

/// <summary>
/// T2 and T3 (magnifier-capture-place-investigation fix, C4): the owner's complaint as assertions.
///
/// <para>
/// <b>T2:</b> looking at the centre of the view selects P — at edges and corners, where the old
/// translate-clamp drew P off-centre (in 3 of 6 live corner sessions it was outside the disc).
/// The capture goes through the production seam with a synthetic screen, so no pixel is read and
/// no window is shown.
/// </para>
///
/// <para>
/// <b>T3:</b> the disc now extends past the screen edge, so a view point over the transparent
/// off-screen part must clamp to the screen — every commit lands on the screen — while an interior
/// point is unchanged.
/// </para>
///
/// <para>
/// The window is constructed, never shown; the DPI scale is restored in a <c>finally</c>.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class ManualSelectionCentreOnPointTests
{
    private const int ScreenWidth = 3840;
    private const int ScreenHeight = 2400;
    private const int RadiusDip = 170;
    private const double Magnification = 2.0;

    // The live session's corner and edge P values from the investigation.
    [StaTheory]
    [InlineData(3803, 85)]
    [InlineData(3688, 2395)]
    [InlineData(3839, 2399)]
    [InlineData(3682, 151)]
    [InlineData(3706, 2237)]
    public void ViewCentre_SelectsP_AtEdgesAndCorners(int pointX, int pointY)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(2.0, 2.0);
        var window = new ManualSelectionOverlayWindow();
        try
        {
            var point = new PhysicalPoint(pointX, pointY);
            window.Capture = (center, radius) =>
                ScreenCaptureService.CaptureRegion(center, radius, ScreenWidth, ScreenHeight, SyntheticScreenGrab.Grab);
            window.ScreenSize = () => (ScreenWidth, ScreenHeight);

            window.UpdateCenter(new PhysicalPoint(1920, 1200), RadiusDip, Magnification, gazePoint: point);

            var real = window.GetRealPhysicalPoint(new PhysicalPoint(1920, 1200));

            Check.WithCustomMessage(
                    $"looking at the middle of the view selected ({real.X:0.##},{real.Y:0.##}), not P "
                    + $"({pointX},{pointY}); the capture must be centred on P at every edge and corner.")
                .That(real.X).IsCloseTo(pointX, 1);
            Check.That(real.Y).IsCloseTo(pointY, 1);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
            window.Close();
        }
    }

    [StaFact]
    public void AViewPointOverTheOffScreenPart_ClampsToTheScreenEdge()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(2.0, 2.0);
        var window = new ManualSelectionOverlayWindow();
        try
        {
            var point = new PhysicalPoint(3803, 85);
            window.Capture = (center, radius) =>
                ScreenCaptureService.CaptureRegion(center, radius, ScreenWidth, ScreenHeight, SyntheticScreenGrab.Grab);
            window.ScreenSize = () => (ScreenWidth, ScreenHeight);

            window.UpdateCenter(new PhysicalPoint(1920, 1200), RadiusDip, Magnification, gazePoint: point);

            // The view centre is (1920,1200) physical. +400/−400 physical in the 2.0× view maps to
            // (4003,−115) on the screen — off it, over the transparent part.
            var real = window.GetRealPhysicalPoint(new PhysicalPoint(1920 + 400, 1200 - 400));

            Check.WithCustomMessage(
                    $"a view point over the off-screen part committed at ({real.X:0.##},{real.Y:0.##}), "
                    + "outside the screen; every selected point must land on the screen.")
                .That(real.X).IsEqualTo(ScreenWidth - 1);
            Check.That(real.Y).IsEqualTo(0);

            var interior = window.GetRealPhysicalPoint(new PhysicalPoint(1920, 1200));
            Check.WithCustomMessage("the clamp must not move an on-screen commit.")
                .That(interior).IsEqualTo(point);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
            window.Close();
        }
    }
}
