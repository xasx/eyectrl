namespace EyeCtrl.App.Services;

using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

public static class ScreenCaptureService
{
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    /// <summary>
    /// Captures a square region of the screen centred on the given point. The capture is always
    /// centred on <paramref name="center"/>; near an edge the off-screen part of the square is
    /// transparent, so the image centre stays the requested point.
    /// </summary>
    public static CaptureResult? CaptureRegion(PhysicalPoint center, int radius)
    {
        var (screenWidth, screenHeight) = PhysicalScreenSize();
        return CaptureRegion(center, radius, screenWidth, screenHeight, GdiGrab);
    }

    /// <summary>
    /// The capture seam: production supplies <see cref="GdiGrab"/>; tests supply a synthetic grab
    /// so placement and composition are exercised without reading a screen pixel.
    ///
    /// <para>
    /// Only the on-screen source rect is ever passed to <paramref name="grab"/>. An off-screen
    /// <c>CopyFromScreen</c> returns the OTHER monitor's pixels on a multi-monitor desk, so the
    /// off-screen part is composed as transparent padding instead — it shows the dark under-fill.
    /// </para>
    /// </summary>
    internal static CaptureResult? CaptureRegion(
        PhysicalPoint center,
        int radius,
        int screenWidth,
        int screenHeight,
        Func<int, int, int, int, Bitmap> grab)
    {
        var placement = CaptureRegionMath.Place(
            (int)center.X, (int)center.Y, radius, screenWidth, screenHeight);
        if (placement is not { } p) return null;

        using var bitmap = grab(p.SourceX, p.SourceY, p.SourceW, p.SourceH);
        var source = ToBitmapSource(bitmap);

        if (p.SourceW == p.Size && p.SourceH == p.Size)
            return new CaptureResult(source, p.OriginX, p.OriginY, p.Size);

        // Transparent padding cannot go through System.Drawing: a GDI round-trip composites the
        // transparent pixels over a background colour, so the off-screen part would not stay
        // transparent. A zero-initialised WriteableBitmap keeps it alpha 0.
        var composed = new WriteableBitmap(p.Size, p.Size, 96, 96, PixelFormats.Bgra32, null);
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = p.SourceW * 4;
        var pixels = new byte[stride * p.SourceH];
        bgra.CopyPixels(pixels, stride, 0);
        composed.WritePixels(
            new Int32Rect(p.DestX, p.DestY, p.SourceW, p.SourceH), pixels, stride, 0);
        composed.Freeze();

        return new CaptureResult(composed, p.OriginX, p.OriginY, p.Size);
    }

    /// <summary>
    /// Captures the whole primary screen, 1:1 in physical pixels — the sub-pixel probe's backdrop
    /// (build plan C6). Unlike <see cref="CaptureRegion(PhysicalPoint, int)"/> this covers the
    /// screen exactly, so the probe window can draw it at one image pixel per device pixel.
    /// </summary>
    public static BitmapSource? CaptureFullScreen()
    {
        var (width, height) = PhysicalScreenSize();
        if (width <= 0 || height <= 0) return null;

        using var bitmap = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.CopyFromScreen(0, 0, 0, 0, new System.Drawing.Size(width, height));
        }

        return ToBitmapSource(bitmap);
    }

    private static Bitmap GdiGrab(int x, int y, int width, int height)
    {
        var bitmap = new Bitmap(width, height);
        try
        {
            using var g = Graphics.FromImage(bitmap);
            g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(width, height));
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static BitmapSource ToBitmapSource(Bitmap bitmap)
    {
        var hBitmap = bitmap.GetHbitmap();
        try
        {
            return Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }

    internal static (int Width, int Height) PhysicalScreenSize()
        => ((int)DpiHelper.LogicalToPhysicalX(SystemParameters.PrimaryScreenWidth),
            (int)DpiHelper.LogicalToPhysicalY(SystemParameters.PrimaryScreenHeight));
}
