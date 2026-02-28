namespace EyeCtrl.App.Overlays;

using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EyeCtrl.Core.Models;

public partial class ZoomOverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private ScreenPoint _originalCenter = new(0, 0);
    private int _captureRadius;
    private double _magnification;

    public ZoomOverlayWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        var extStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, extStyle | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW);
    }

    public void Show(ScreenPoint center, int radius, double magnification)
    {
        _originalCenter = center;
        _captureRadius = radius;
        _magnification = magnification;

        var displayDiameter = (int)(radius * 2 * magnification);
        Width = displayDiameter;
        Height = displayDiameter;

        Left = center.X - displayDiameter / 2.0;
        Top = center.Y - displayDiameter / 2.0;

        CaptureAndDisplay(center, radius, magnification);
        Show();
    }

    private void CaptureAndDisplay(ScreenPoint center, int radius, double magnification)
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
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            var displaySize = (int)(captureSize * magnification);

            ZoomBrush.ImageSource = source;
            ZoomBrush.Viewbox = new Rect(0, 0, captureSize, captureSize);
            ZoomBrush.ViewboxUnits = BrushMappingMode.Absolute;
            ZoomBrush.Viewport = new Rect(0, 0, displaySize, displaySize);
            ZoomBrush.ViewportUnits = BrushMappingMode.Absolute;
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
