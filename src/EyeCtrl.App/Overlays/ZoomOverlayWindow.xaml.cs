namespace EyeCtrl.App.Overlays;

using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EyeCtrl.Core.Models;

public partial class ZoomOverlayWindow : OverlayWindow
{
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private ScreenPoint _originalCenter = new(0, 0);
    private double _magnification;

    public ZoomOverlayWindow()
    {
        IsClickThrough = true;
        InitializeComponent();
    }

    public void Show(ScreenPoint center, int radius, double magnification)
    {
        UpdateCenter(center, radius, magnification);
        Show();
    }

    public void UpdateCenter(ScreenPoint center, int radius, double magnification)
    {
        _originalCenter = center;
        _magnification = magnification;

        var displayDiameter = (int)(radius * 2 * magnification);
        Width = displayDiameter;
        Height = displayDiameter;

        Left = center.X - displayDiameter / 2.0;
        Top = center.Y - displayDiameter / 2.0;

        CaptureAndDisplay(center, radius);
    }

    private void CaptureAndDisplay(ScreenPoint center, int radius)
    {
        var captureX = (int)center.X - radius;
        var captureY = (int)center.Y - radius;
        var captureSize = radius * 2;

        // Clamp to screen bounds
        var screenWidth = (int)SystemParameters.PrimaryScreenWidth;
        var screenHeight = (int)SystemParameters.PrimaryScreenHeight;
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
            ZoomBrush.ImageSource = Imaging.CreateBitmapSourceFromHBitmap(
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

    public ScreenPoint GetRealScreenPoint(ScreenPoint gazeOnScreen)
    {
        // Convert screen gaze coordinates to position within the zoom window
        var windowCenterX = Left + Width / 2;
        var windowCenterY = Top + Height / 2;

        var offsetX = gazeOnScreen.X - windowCenterX;
        var offsetY = gazeOnScreen.Y - windowCenterY;

        // Scale back from magnified to original coordinates
        var realX = _originalCenter.X + offsetX / _magnification;
        var realY = _originalCenter.Y + offsetY / _magnification;

        return new ScreenPoint(realX, realY);
    }
}
