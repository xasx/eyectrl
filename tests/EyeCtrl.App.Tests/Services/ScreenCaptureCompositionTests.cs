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
/// <item>leave the off-screen part at alpha 0 (it shows the dark under-fill, not stretched or
/// other-monitor content);</item>
/// <item>never ask the grab for an off-screen rect (on a multi-monitor desk that returns the OTHER
/// monitor's pixels);</item>
/// <item>keep today's fast path, centre and image for an interior P.</item>
/// </list>
///
/// <para>No screen pixel is read; the grab is the injected seam.</para>
/// </summary>
public class ScreenCaptureCompositionTests
{
    private const int ScreenWidth = 3840;
    private const int ScreenHeight = 2400;
    private const int Radius = 340;
    private const int Size = 2 * Radius;

    [StaFact]
    public void EdgeCapture_ComposesTheOnScreenPart_AndLeavesTheRestTransparent()
    {
        var calls = new List<(int X, int Y, int W, int H)>();
        var result = ScreenCaptureService.CaptureRegion(
            new PhysicalPoint(3803, 85), Radius, ScreenWidth, ScreenHeight,
            (x, y, w, h) =>
            {
                calls.Add((x, y, w, h));
                return SyntheticScreenGrab.Grab(x, y, w, h);
            });

        Check.That(result).IsNotNull();
        var capture = result!.Value;
        Check.That(capture.Size).IsEqualTo(Size);
        Check.WithCustomMessage(
                $"the capture's effective centre is ({capture.EffectiveCenter.X},{capture.EffectiveCenter.Y}), not P.")
            .That(capture.EffectiveCenter).IsEqualTo(new PhysicalPoint(3803, 85));

        Check.WithCustomMessage(
                $"the grab was called {calls.Count} times; the on-screen source is one rect.")
            .That(calls).HasSize(1);
        var (callX, callY, callW, callH) = calls[0];
        Check.WithCustomMessage($"the grab rect ({callX},{callY},{callW},{callH}) is not the placement's source.")
            .That((callX, callY, callW, callH)).IsEqualTo((3463, 0, 377, 425));

        Check.WithCustomMessage("the grab was asked for an off-screen rect; it must never be.")
            .That(callX).IsGreaterOrEqualThan(0);
        Check.That(callY).IsGreaterOrEqualThan(0);
        Check.That(callX + callW).IsLessOrEqualThan(ScreenWidth);
        Check.That(callY + callH).IsLessOrEqualThan(ScreenHeight);

        var (pixels, stride) = ReadBgra(capture.Image);

        // The image centre is P: pixel (340,340) of the square decodes to (3803,85).
        Check.WithCustomMessage("the composed image's centre pixel is not P.")
            .That(SyntheticScreenGrab.Decode(pixels, stride, 340, 340)).IsEqualTo((3803, 85));

        // Above P (y = −255) and right of the screen (x = 4142): transparent, never wrapped pixels.
        Check.WithCustomMessage("the off-screen top part is not transparent.")
            .That(SyntheticScreenGrab.AlphaAt(pixels, stride, 340, 0)).IsEqualTo(0);
        Check.WithCustomMessage("the off-screen right part is not transparent.")
            .That(SyntheticScreenGrab.AlphaAt(pixels, stride, Size - 1, 340)).IsEqualTo(0);
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
