namespace EyeCtrl.App.Overlays;

using System.Threading.Tasks;
using System.Windows;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

public partial class ManualSelectionOverlayWindow : OverlayWindow, IManualSelectionOverlay
{
    // UI Constants
    private const int ScreenCaptureDelayMs = 100; // Delay before capturing screen to let WPF render

    private PhysicalPoint? _captureCenter;
    private PhysicalPoint? _effectiveCaptureCenter;
    private double _magnification;

    public ManualSelectionOverlayWindow()
    {
        IsClickThrough = true;
        ExcludeFromScreenCapture = true; // the magnifier must not capture itself
        InitializeComponent();
    }

    public void Show(PhysicalPoint center, int radius, double magnification, PhysicalPoint? gazePoint = null, bool recapture = true)
    {
        UpdateCenter(center, radius, magnification, gazePoint, recapture);
        Show();
    }

    public void UpdateCenter(PhysicalPoint center, int radius, double magnification, PhysicalPoint? gazePoint = null, bool recapture = true)
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
        // Skip the capture when reusing an existing shot (e.g. back from the action menu): re-capturing
        // there is wasteful and would grab the dimmed backdrop that is up during that transition.
        if (recapture || ManualSelectionBrush.ImageSource == null)
        {
            var physicalRadius = (int)(radius * DpiHelper.ScaleX);
            CaptureAndDisplay(_captureCenter!.Value, physicalRadius);
        }
    }

    private async void CaptureAndDisplay(PhysicalPoint center, int radius)
    {
        await Task.Delay(ScreenCaptureDelayMs);
        var capture = ScreenCaptureService.CaptureRegion(center, radius);
        if (capture != null)
        {
            // The capture is clamped to the screen, so its centre is the effective one. Written
            // here, with the image, and never when the capture is requested: a request-time value
            // would describe an image the user is not yet looking at.
            _effectiveCaptureCenter = capture.Value.EffectiveCenter;
            ManualSelectionBrush.ImageSource = capture.Value.Image;
        }
    }

    public void Recapture()
    {
        // Width is in DIPs; derive the DIP radius, then scale to physical for capture
        var dipRadius = (int)(Width / _magnification / 2);
        var physicalRadius = (int)(dipRadius * DpiHelper.ScaleX);
        CaptureAndDisplay(_captureCenter!.Value, physicalRadius);
    }

    /// <summary>
    /// The centre, in physical pixels, of the capture currently displayed — the effective centre
    /// after the capture rectangle was clamped to the screen. Falls back to the requested centre
    /// until the first image is displayed. Every view↔screen mapping reads this accessor.
    /// </summary>
    public PhysicalPoint EffectiveCaptureCenter => _effectiveCaptureCenter ?? _captureCenter!.Value;

    public PhysicalPoint GetRealPhysicalPoint(PhysicalPoint gazeOnScreen)
    {
        // Zero arithmetic here by design: the mapping owns the DIP->physical conversion of the
        // window geometry, so the units cannot drift between this call site and the mapping.
        return CaptureViewMapping.ToRealPoint(
            EffectiveCaptureCenter,
            new Rect(Left, Top, Width, Height),
            _magnification,
            gazeOnScreen);
    }
}
