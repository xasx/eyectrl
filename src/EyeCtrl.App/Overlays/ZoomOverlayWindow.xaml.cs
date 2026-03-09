namespace EyeCtrl.App.Overlays;

using System.Threading.Tasks;
using System.Windows;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;

public partial class ZoomOverlayWindow : OverlayWindow
{
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

    private async void CaptureAndDisplay(ScreenPoint center, int radius)
    {
        await Task.Delay(100);
        var source = ScreenCaptureService.CaptureRegion(center, radius);
        if (source != null)
            ZoomBrush.ImageSource = source;
    }

    public void Recapture()
    {
        var radius = (int)(Width / _magnification / 2);
        CaptureAndDisplay(_originalCenter, radius);
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
