namespace EyeCtrl.App.Services;

using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

public static class ScreenCaptureService
{
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    /// <summary>
    /// Captures a square region of the screen centered on the given point. The request is clamped
    /// to the screen, so the returned rectangle is the effective one — near an edge its centre is
    /// not the requested centre.
    /// </summary>
    public static CaptureResult? CaptureRegion(PhysicalPoint center, int radius)
    {
        var (screenWidth, screenHeight) = PhysicalScreenSize();

        var region = CaptureRegionMath.Compute((int)center.X, (int)center.Y, radius, screenWidth, screenHeight);
        if (region == null) return null;

        var (captureX, captureY, captureSize) = region.Value;

        using var bitmap = new Bitmap(captureSize, captureSize);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.CopyFromScreen(captureX, captureY, 0, 0, new System.Drawing.Size(captureSize, captureSize));
        }

        var hBitmap = bitmap.GetHbitmap();
        try
        {
            var image = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            return new CaptureResult(image, captureX, captureY, captureSize);
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }

    /// <summary>
    /// Captures the whole primary screen, 1:1 in physical pixels — the sub-pixel probe's backdrop
    /// (build plan C6). Unlike <see cref="CaptureRegion"/> this covers the screen exactly, so the
    /// probe window can draw it at one image pixel per device pixel.
    /// </summary>
    public static System.Windows.Media.Imaging.BitmapSource? CaptureFullScreen()
    {
        var (width, height) = PhysicalScreenSize();
        if (width <= 0 || height <= 0) return null;

        using var bitmap = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.CopyFromScreen(0, 0, 0, 0, new System.Drawing.Size(width, height));
        }

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

    /// <summary>
    /// The centre, in physical pixels, of the image a capture of <paramref name="radius"/> around
    /// <paramref name="center"/> will produce. Available before the capture runs, for callers that
    /// must place something against the image before it arrives.
    /// </summary>
    public static PhysicalPoint GetEffectiveCenter(PhysicalPoint center, int radius)
    {
        var (screenWidth, screenHeight) = PhysicalScreenSize();
        return CaptureViewMapping.EffectiveCaptureCenter(center, radius, screenWidth, screenHeight);
    }

    private static (int Width, int Height) PhysicalScreenSize()
        => ((int)DpiHelper.LogicalToPhysicalX(SystemParameters.PrimaryScreenWidth),
            (int)DpiHelper.LogicalToPhysicalY(SystemParameters.PrimaryScreenHeight));
}
