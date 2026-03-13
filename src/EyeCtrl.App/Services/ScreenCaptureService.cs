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
    /// Captures a square region of the screen centered on the given point.
    /// Returns a BitmapSource suitable for WPF ImageBrush.
    /// </summary>
    public static BitmapSource? CaptureRegion(ScreenPoint center, int radius)
    {
        var captureX = (int)center.X - radius;
        var captureY = (int)center.Y - radius;
        var captureSize = radius * 2;

        if (captureSize <= 0) return null;

        // Clamp to screen bounds
        var screenWidth = (int)DpiHelper.LogicalToPhysicalX(SystemParameters.PrimaryScreenWidth);
        var screenHeight = (int)DpiHelper.LogicalToPhysicalY(SystemParameters.PrimaryScreenHeight);
        captureX = Math.Max(0, Math.Min(captureX, screenWidth - captureSize));
        captureY = Math.Max(0, Math.Min(captureY, screenHeight - captureSize));

        using var bitmap = new Bitmap(captureSize, captureSize);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.CopyFromScreen(captureX, captureY, 0, 0, new System.Drawing.Size(captureSize, captureSize));
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
}
