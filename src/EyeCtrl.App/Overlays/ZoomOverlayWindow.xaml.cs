namespace EyeCtrl.App.Overlays;

using System.Threading.Tasks;
using System.Windows;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

public partial class ZoomOverlayWindow : OverlayWindow
{
    private ScreenPoint _originalCenter = new(0, 0);
    private ScreenPoint _captureCenter = new(0, 0);
    private double _magnification;

    public ZoomOverlayWindow()
    {
        IsClickThrough = true;
        InitializeComponent();
    }

    public void Show(ScreenPoint center, int radius, double magnification, ScreenPoint? gazePoint = null)
    {
        UpdateCenter(center, radius, magnification, gazePoint);
        Show();
    }

    public void UpdateCenter(ScreenPoint center, int radius, double magnification, ScreenPoint? gazePoint = null)
    {
        _originalCenter = center;
        _captureCenter = gazePoint ?? center;
        _magnification = magnification;

        var displayDiameter = (int)(radius * 2 * magnification);
        Width = displayDiameter;
        Height = displayDiameter;

        Left = DpiHelper.PhysicalToLogicalX(center.X) - displayDiameter / 2.0;
        Top = DpiHelper.PhysicalToLogicalY(center.Y) - displayDiameter / 2.0;

        // radius is in DIPs; CopyFromScreen needs physical pixels.
        // Scaling ensures the captured area matches the DIP display window,
        // so visual magnification equals the configured magnification.
        var physicalRadius = (int)(radius * DpiHelper.ScaleX);
        CaptureAndDisplay(_captureCenter, physicalRadius);
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
        // Width is in DIPs; derive the DIP radius, then scale to physical for capture
        var dipRadius = (int)(Width / _magnification / 2);
        var physicalRadius = (int)(dipRadius * DpiHelper.ScaleX);
        CaptureAndDisplay(_captureCenter, physicalRadius);
    }

    public ScreenPoint GetRealScreenPoint(ScreenPoint gazeOnScreen)
    {
        // Window Left/Width are DIPs; convert back to physical to match gazeOnScreen
        var windowCenterX = DpiHelper.LogicalToPhysicalX(Left + Width / 2);
        var windowCenterY = DpiHelper.LogicalToPhysicalY(Top + Height / 2);

        var offsetX = gazeOnScreen.X - windowCenterX;
        var offsetY = gazeOnScreen.Y - windowCenterY;

        // Scale back from magnified to original coordinates
        var realX = _captureCenter.X + offsetX / _magnification;
        var realY = _captureCenter.Y + offsetY / _magnification;

        return new ScreenPoint(realX, realY);
    }
}
