namespace EyeCtrl.Platform.Tests.Thumbnails;

using EyeCtrl.Core.Models;
using EyeCtrl.Platform.Thumbnails;

/// <summary>
/// S4-C's preview geometry, pure and headless: one 168 × 94 DIP box (the Option C board), the live
/// DPI scale and the source window's physical size → the destination rectangle a DWM thumbnail is
/// drawn into.
///
/// <para>
/// These are the numbers the fit exists for: a wide, a tall and a square source, at 100 % and at the
/// owner's 200 %. A regression from DIP to physical (the ticket's mutant c) halves every size at
/// 2.0 and fails the 2.0 rows; swapping the axes, stretching instead of fitting, or centring on the
/// unrounded box fails the assertions below.
/// </para>
/// </summary>
public class ThumbnailGeometryTests
{
    private const double BoxWidth = 168;
    private const double BoxHeight = 94;

    [Theory]
    // 100 %: the 168 x 94 box in physical pixels.
    [InlineData(1.0, 0, 0, 1920, 1080, 1, 0, 167, 94)]     // wide 16:9
    [InlineData(1.0, 0, 0, 1080, 1920, 58, 0, 53, 94)]     // tall 9:16
    [InlineData(1.0, 0, 0, 1000, 1000, 37, 0, 94, 94)]     // square
    // 200 %: every edge is doubled; the fit is the same shape at twice the pixels.
    [InlineData(2.0, 0, 0, 1920, 1080, 1, 0, 334, 188)]
    [InlineData(2.0, 0, 0, 1080, 1920, 115, 0, 106, 188)]
    [InlineData(2.0, 0, 0, 1000, 1000, 74, 0, 188, 188)]
    public void FitDestination_AspectFitsAndCentres_At100And200Percent(
        double scale,
        double boxLeft, double boxTop,
        int sourceWidth, int sourceHeight,
        double expectedX, double expectedY, double expectedWidth, double expectedHeight)
    {
        var fitted = ThumbnailGeometry.FitDestination(
            boxLeft, boxTop, BoxWidth, BoxHeight, scale, scale, sourceWidth, sourceHeight);

        Check.That(fitted).IsEqualTo(new PhysicalRect(
            expectedX, expectedY, expectedWidth, expectedHeight));
    }

    [Theory]
    [InlineData(1.0, 1920, 1080)]
    [InlineData(1.0, 1080, 1920)]
    [InlineData(1.0, 1000, 1000)]
    [InlineData(2.0, 1920, 1080)]
    [InlineData(2.0, 1080, 1920)]
    [InlineData(2.0, 1000, 1000)]
    public void FitDestination_KeepsTheAspect_StaysInsideTheBox_AndIsCentred(
        double scale, int sourceWidth, int sourceHeight)
    {
        var originX = 40.0;
        var originY = 136.0;
        var fitted = ThumbnailGeometry.FitDestination(
            originX, originY, BoxWidth, BoxHeight, scale, scale, sourceWidth, sourceHeight);

        var boxLeft = originX * scale;
        var boxTop = originY * scale;
        var boxRight = (originX + BoxWidth) * scale;
        var boxBottom = (originY + BoxHeight) * scale;

        // Never outside the box (a thumbnail is composed above WPF; a rect that escaped the box
        // would paint over the title row or the progress bar).
        Check.That(fitted.X).IsGreaterOrEqualThan(boxLeft - 1e-9);
        Check.That(fitted.Y).IsGreaterOrEqualThan(boxTop - 1e-9);
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
        Check.That(Math.Abs((fitted.X + (fitted.Width / 2)) - ((boxLeft + boxRight) / 2)))
            .IsLessOrEqualThan(1.0);
        Check.That(Math.Abs((fitted.Y + (fitted.Height / 2)) - ((boxTop + boxBottom) / 2)))
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
        // A box 40.5 DIP from the window's left and 20.25 DIP from its top, at 100 %: every edge is
        // rounded once, away from zero. The rect must follow the box — the switcher covering (0,0)
        // is a coincidence, never a contract (the ticket: "do not depend on that").
        var fitted = ThumbnailGeometry.FitDestination(
            40.5, 20.25, BoxWidth, BoxHeight, 1.0, 1.0, 1000, 1000);

        // left = 41, top = 20, right = round(208.5) = 209, bottom = round(114.25) = 114.
        Check.That(fitted).IsEqualTo(new PhysicalRect(78, 20, 94, 94));
    }

    [Fact]
    public void FitDestination_AtAFractionalScale_StillLandsInsideTheRoundedBox()
    {
        // 150 %: edges 0, 0, 252, 141. A wide source keeps the full height (141) and its width
        // rounds to 251.
        var fitted = ThumbnailGeometry.FitDestination(
            0, 0, BoxWidth, BoxHeight, 1.5, 1.5, 1920, 1080);

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
            _ = ThumbnailGeometry.FitDestination(0, 0, BoxWidth, BoxHeight, 1.0, 1.0, 0, 1080);
        }
        catch (ArgumentOutOfRangeException e)
        {
            ex = e;
        }

        Check.That(ex).IsNotNull();
        Check.That(ex!.ParamName).IsEqualTo("sourceWidth");
    }
}
