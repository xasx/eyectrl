namespace EyeCtrl.App.Overlays;

using System.Threading.Tasks;
using System.Windows;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

public partial class ManualSelectionOverlayWindow : OverlayWindow
{
    // UI Constants
    private const int ScreenCaptureDelayMs = 100; // Delay before capturing screen to let WPF render

    private PhysicalPoint? _captureCenter;
    private double _magnification;

    public ManualSelectionOverlayWindow()
    {
        IsClickThrough = true;
        ExcludeFromScreenCapture = true; // the magnifier must not capture itself
        InitializeComponent();
    }

    public void Show(PhysicalPoint center, int radius, double magnification, PhysicalPoint? gazePoint = null)
    {
        UpdateCenter(center, radius, magnification, gazePoint);
        Show();
    }

    public void UpdateCenter(PhysicalPoint center, int radius, double magnification, PhysicalPoint? gazePoint = null)
    {
        // Only update _captureCenter if gazePoint is explicitly provided
        // This preserves the capture center when realigning the display position
        if (gazePoint.HasValue)
            _captureCenter = gazePoint.Value;
        else if (!_captureCenter.HasValue)
            // First call without gazePoint - initialize to center
            _captureCenter = center;
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
        CaptureAndDisplay(_captureCenter!.Value, physicalRadius);
    }

    private async void CaptureAndDisplay(PhysicalPoint center, int radius)
    {
        await Task.Delay(ScreenCaptureDelayMs);
        var source = ScreenCaptureService.CaptureRegion(center, radius);
        if (source != null)
            ManualSelectionBrush.ImageSource = source;
    }

    public void Recapture()
    {
        // Width is in DIPs; derive the DIP radius, then scale to physical for capture
        var dipRadius = (int)(Width / _magnification / 2);
        var physicalRadius = (int)(dipRadius * DpiHelper.ScaleX);
        CaptureAndDisplay(_captureCenter!.Value, physicalRadius);
    }

    public PhysicalPoint GetRealPhysicalPoint(PhysicalPoint gazeOnScreen)
    {
        // Window Left/Width are DIPs; convert back to physical to match gazeOnScreen
        var windowCenterX = DpiHelper.LogicalToPhysicalX(Left + Width / 2);
        var windowCenterY = DpiHelper.LogicalToPhysicalY(Top + Height / 2);

        var offsetX = gazeOnScreen.X - windowCenterX;
        var offsetY = gazeOnScreen.Y - windowCenterY;

        // Scale back from magnified to original coordinates
        var realX = _captureCenter!.Value.X + offsetX / _magnification;
        var realY = _captureCenter!.Value.Y + offsetY / _magnification;

        return new PhysicalPoint(realX, realY);
    }
}
