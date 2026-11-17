namespace EyeCtrl.Platform.Tests.Thumbnails;

using EyeCtrl.Core.Models;
using EyeCtrl.Platform.Thumbnails;

/// <summary>
/// S4-C's preview geometry, pure and headless: the PHYSICAL preview box (the caller reads it with
/// <c>PointToScreen</c>, the dwell path's convention) and the source window's physical size → the
/// destination rectangle a DWM thumbnail is drawn into.
///
/// <para>
/// These are the numbers the fit exists for: a wide, a tall and a square source, inside the
/// 168 × 94 DIP box at 100 % (168 × 94 px) and at the owner's 200 % (336 × 188 px). A regression
/// that treats the physical box as DIP, swaps the axes, stretches instead of fitting, or centres on
/// the unrounded box fails the assertions below. The caller-side scale is pinned by the App wiring
/// test at 2.0 (a real registration pass through an off-screen 2.0 render chain).
/// </para>
/// </summary>
public class ThumbnailGeometryTests
{
    private const double BoxWidth = 168;
    private const double BoxHeight = 94;

    [Theory]
    // 100 %: the 168 x 94 box in physical pixels.
    [InlineData(0, 0, 168, 94, 1920, 1080, 1, 0, 167, 94)]     // wide 16:9
    [InlineData(0, 0, 168, 94, 1080, 1920, 58, 0, 53, 94)]     // tall 9:16
    [InlineData(0, 0, 168, 94, 1000, 1000, 37, 0, 94, 94)]     // square
    // 200 %: the same design box as the render chain reports it (336 x 188 physical).
    [InlineData(0, 0, 336, 188, 1920, 1080, 1, 0, 334, 188)]
    [InlineData(0, 0, 336, 188, 1080, 1920, 115, 0, 106, 188)]
    [InlineData(0, 0, 336, 188, 1000, 1000, 74, 0, 188, 188)]
    public void FitDestination_AspectFitsAndCentres_At100And200Percent(
        double boxLeft, double boxTop, double boxWidth, double boxHeight,
        int sourceWidth, int sourceHeight,
        double expectedX, double expectedY, double expectedWidth, double expectedHeight)
    {
        var fitted = ThumbnailGeometry.FitDestination(
            boxLeft, boxTop, boxWidth, boxHeight, sourceWidth, sourceHeight);

        Check.That(fitted).IsEqualTo(new PhysicalRect(
            expectedX, expectedY, expectedWidth, expectedHeight));
    }

    [Theory]
    [InlineData(168, 94, 1920, 1080)]
    [InlineData(168, 94, 1080, 1920)]
    [InlineData(168, 94, 1000, 1000)]
    [InlineData(336, 188, 1920, 1080)]
    [InlineData(336, 188, 1080, 1920)]
    [InlineData(336, 188, 1000, 1000)]
    public void FitDestination_KeepsTheAspect_StaysInsideTheBox_AndIsCentred(
        double boxWidth, double boxHeight, int sourceWidth, int sourceHeight)
    {
        var originX = 80.0;
        var originY = 272.0;
        var fitted = ThumbnailGeometry.FitDestination(
            originX, originY, boxWidth, boxHeight, sourceWidth, sourceHeight);

        var boxRight = originX + boxWidth;
        var boxBottom = originY + boxHeight;

        // Never outside the box (a thumbnail is composed above WPF; a rect that escaped the box
        // would paint over the title row or the progress bar).
        Check.That(fitted.X).IsGreaterOrEqualThan(originX - 1e-9);
        Check.That(fitted.Y).IsGreaterOrEqualThan(originY - 1e-9);
        Check.That(fitted.X + fitted.Width).IsLessOrEqualThan(boxRight + 1e-9);
        Check.That(fitted.Y + fitted.Height).IsLessOrEqualThan(boxBottom + 1e-9);

        // Aspect kept to within one rounded pixel on the fitted extent.
        var sourceAspect = (double)sourceWidth / sourceHeight;
        var fittedAspect = fitted.Width / fitted.Height;
        var allowed = Math.Max(1.0 / fitted.Width, 1.0 / fitted.Height) * 2;
        Check.WithCustomMessage(
                $"source aspect {sourceAspect:0.####}, fitted aspect {fittedAspect:0.####} (allowed ±{allowed:0.####})")
            .That(Math.Abs(fittedAspect - sourceAspect)).IsLessOrEqualThan(allowed * sourceAspect);

        // Centred on the box (half a pixel of rounding on each axis, at most).
        Check.That(Math.Abs((fitted.X + (fitted.Width / 2)) - ((originX + boxRight) / 2)))
            .IsLessOrEqualThan(1.0);
        Check.That(Math.Abs((fitted.Y + (fitted.Height / 2)) - ((originY + boxBottom) / 2)))
            .IsLessOrEqualThan(1.0);

        // Whole physical pixels: DwmUpdateThumbnailProperties takes an integer RECT.
        Check.That(fitted.X % 1).IsEqualTo(0);
        Check.That(fitted.Y % 1).IsEqualTo(0);
        Check.That(fitted.Width % 1).IsEqualTo(0);
        Check.That(fitted.Height % 1).IsEqualTo(0);
        Check.That(fitted.Width).IsStrictlyGreaterThan(0);
        Check.That(fitted.Height).IsStrictlyGreaterThan(0);
    }

    [Fact]
    public void FitDestination_IsRelativeToTheWindow_NotTheScreen()
    {
        // A box 40.5 px from the window's left and 20.25 px from its top: every edge is rounded
        // once, away from zero. The rect must follow the box — the switcher covering (0,0) is a
        // coincidence, never a contract (the ticket: "do not depend on that").
        var fitted = ThumbnailGeometry.FitDestination(
            40.5, 20.25, BoxWidth, BoxHeight, 1000, 1000);

        // left = 41, top = 20, right = round(208.5) = 209, bottom = round(114.25) = 114.
        Check.That(fitted).IsEqualTo(new PhysicalRect(78, 20, 94, 94));
    }

    [Fact]
    public void FitDestination_WithAFractionalBox_StillLandsInsideTheRoundedEdges()
    {
        // A 252 x 141 physical box whose left/top are fractional (a 150 % render chain can report
        // such edges). A wide source keeps the full height (141) and its width rounds to 251.
        var fitted = ThumbnailGeometry.FitDestination(
            0.5, 0.25, 251.5, 140.75, 1920, 1080);

        // left = 1, top = 0, right = round(252) = 252, bottom = round(141) = 141 -> 251 x 141.
        Check.That(fitted).IsEqualTo(new PhysicalRect(1, 0, 251, 141));
        Check.That(fitted.X + fitted.Width).IsLessOrEqualThan(252);
        Check.That(fitted.Y + fitted.Height).IsLessOrEqualThan(141);
    }

    [Fact]
    public void FitDestination_RejectsADegenerateSource_SoACallerThatSkippedTheSizeCheckFailsLoudly()
    {
        ArgumentOutOfRangeException? ex = null;
        try
        {
            _ = ThumbnailGeometry.FitDestination(0, 0, BoxWidth, BoxHeight, 0, 1080);
        }
        catch (ArgumentOutOfRangeException e)
        {
            ex = e;
        }

        Check.That(ex).IsNotNull();
        Check.That(ex!.ParamName).IsEqualTo("sourceWidth");
    }
}
