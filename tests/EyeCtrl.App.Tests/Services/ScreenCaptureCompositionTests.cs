namespace EyeCtrl.App.Tests.Services;

using System.Windows.Media;
using System.Windows.Media.Imaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Models;
using Xunit;

/// <summary>
/// T4 (magnifier-capture-place-investigation fix, C2): the capture seam takes only the ON-SCREEN
/// part of the centred square and composes it into a transparent 2r×2r image. With a synthetic
/// screen whose pixels encode their coordinates, an edge capture must:
///
/// <list type="bullet">
/// <item>put the source pixel at P at the image centre;</item>
/// <item>draw the source at the placement's (DestX, DestY), leaving every other pixel at alpha 0
/// (it shows the dark under-fill, not stretched or other-monitor content);</item>
/// <item>never ask the grab for an off-screen rect (on a multi-monitor desk that returns the OTHER
/// monitor's pixels);</item>
/// <item>keep today's fast path, centre and image for an interior P.</item>
/// </list>
///
/// <para>
/// The rows deliberately cover both axes at nonzero destination offsets: a top-right edge
/// (DestX 0, DestY 255), the left edge (DestX 320, DestY 0) and the top-left corner (both 340).
/// The X-only regression (the composition ignoring DestX) is the Start-side return of the owner's
/// defect, and the top-right row alone cannot see it.
/// </para>
///
/// <para>No screen pixel is read; the grab is the injected seam.</para>
/// </summary>
public class ScreenCaptureCompositionTests
{
    private const int ScreenWidth = 3840;
    private const int ScreenHeight = 2400;
    private const int Radius = 340;
    private const int Size = 2 * Radius;
    private const int Centre = Size / 2;

    [StaTheory]
    [InlineData(3803, 85, 3463, 0, 377, 425, 0, 255)]
    [InlineData(20, 1200, 0, 860, 360, 680, 320, 0)]
    [InlineData(0, 0, 0, 0, 340, 340, 340, 340)]
    public void EdgeCapture_ComposesTheOnScreenPart_AndLeavesTheRestTransparent(
        int pointX,
        int pointY,
        int grabX,
        int grabY,
        int grabW,
        int grabH,
        int destX,
        int destY)
    {
        var calls = new List<(int X, int Y, int W, int H)>();
        var result = ScreenCaptureService.CaptureRegion(
            new PhysicalPoint(pointX, pointY), Radius, ScreenWidth, ScreenHeight,
            (x, y, w, h) =>
            {
                calls.Add((x, y, w, h));
                return SyntheticScreenGrab.Grab(x, y, w, h);
            });

        Check.That(result).IsNotNull();
        var capture = result!.Value;
        Check.That(capture.Size).IsEqualTo(Size);
        Check.WithCustomMessage(
                $"the capture's effective centre is ({capture.EffectiveCenter.X},{capture.EffectiveCenter.Y}), "
                + $"not P=({pointX},{pointY}).")
            .That(capture.EffectiveCenter).IsEqualTo(new PhysicalPoint(pointX, pointY));

        Check.WithCustomMessage(
                $"the grab was called {calls.Count} times; the on-screen source is one rect.")
            .That(calls).HasSize(1);
        var (callX, callY, callW, callH) = calls[0];
        Check.WithCustomMessage(
                $"the grab rect ({callX},{callY},{callW},{callH}) is not the placement's "
                + $"({grabX},{grabY},{grabW},{grabH}).")
            .That((callX, callY, callW, callH)).IsEqualTo((grabX, grabY, grabW, grabH));

        Check.WithCustomMessage("the grab was asked for an off-screen rect; it must never be.")
            .That(callX).IsGreaterOrEqualThan(0);
        Check.That(callY).IsGreaterOrEqualThan(0);
        Check.That(callX + callW).IsLessOrEqualThan(ScreenWidth);
        Check.That(callY + callH).IsLessOrEqualThan(ScreenHeight);

        var (pixels, stride) = ReadBgra(capture.Image);

        // The image centre is P: its pixel must be drawn AND decode to the requested point.
        Check.WithCustomMessage(
                $"the composed image's centre pixel is ({SyntheticScreenGrab.Decode(pixels, stride, Centre, Centre)}), "
                + $"not P=({pointX},{pointY}); the source is drawn at the wrong destination.")
            .That(SyntheticScreenGrab.Decode(pixels, stride, Centre, Centre)).IsEqualTo((pointX, pointY));
        Check.WithCustomMessage("the composed image's centre pixel was never drawn.")
            .That(SyntheticScreenGrab.AlphaAt(pixels, stride, Centre, Centre)).IsEqualTo(255);

        // Every pixel outside the destination rect is transparent padding.
        if (destX > 0)
            Check.WithCustomMessage($"left of DestX={destX} must be transparent padding.")
                .That(SyntheticScreenGrab.AlphaAt(pixels, stride, destX - 1, Centre)).IsEqualTo(0);
        if (destX + grabW < Size)
            Check.WithCustomMessage($"right of DestX+SourceW={destX + grabW} must be transparent padding.")
                .That(SyntheticScreenGrab.AlphaAt(pixels, stride, destX + grabW, Centre)).IsEqualTo(0);
        if (destY > 0)
            Check.WithCustomMessage($"above DestY={destY} must be transparent padding.")
                .That(SyntheticScreenGrab.AlphaAt(pixels, stride, Centre, destY - 1)).IsEqualTo(0);
        if (destY + grabH < Size)
            Check.WithCustomMessage($"below DestY+SourceH={destY + grabH} must be transparent padding.")
                .That(SyntheticScreenGrab.AlphaAt(pixels, stride, Centre, destY + grabH)).IsEqualTo(0);
    }

    [StaFact]
    public void InteriorCapture_TakesTheFastPath_WithAFullOpaqueImage()
    {
        var calls = new List<(int X, int Y, int W, int H)>();
        var result = ScreenCaptureService.CaptureRegion(
            new PhysicalPoint(1920, 1200), Radius, ScreenWidth, ScreenHeight,
            (x, y, w, h) =>
            {
                calls.Add((x, y, w, h));
                return SyntheticScreenGrab.Grab(x, y, w, h);
            });

        Check.That(result).IsNotNull();
        var capture = result!.Value;
        Check.That(capture.X).IsEqualTo(1580);
        Check.That(capture.Y).IsEqualTo(860);
        Check.That(capture.Size).IsEqualTo(Size);
        Check.That(calls).HasSize(1);
        Check.WithCustomMessage("the interior fast path must grab the whole square once.")
            .That(calls[0]).IsEqualTo((1580, 860, Size, Size));

        var (pixels, stride) = ReadBgra(capture.Image);
        Check.WithCustomMessage("the fast-path image is not the full square.")
            .That((capture.Image.PixelWidth, capture.Image.PixelHeight)).IsEqualTo((Size, Size));
        Check.WithCustomMessage("the interior image's centre pixel is not P.")
            .That(SyntheticScreenGrab.Decode(pixels, stride, 340, 340)).IsEqualTo((1920, 1200));
        Check.WithCustomMessage("the interior fast-path image is not opaque.")
            .That(SyntheticScreenGrab.AlphaAt(pixels, stride, 0, 0)).IsEqualTo(255);
        Check.That(SyntheticScreenGrab.AlphaAt(pixels, stride, Size - 1, Size - 1)).IsEqualTo(255);
    }

    private static (byte[] Pixels, int Stride) ReadBgra(BitmapSource image)
    {
        BitmapSource bgra = image.Format == PixelFormats.Bgra32
            ? image
            : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        return (pixels, stride);
    }
}
