namespace EyeCtrl.App.Overlays;

using System.Windows;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using Serilog;

public partial class ManualSelectionOverlayWindow : OverlayWindow, IManualSelectionOverlay
{
    private const int CaptureFailureLogWindowSeconds = 30;

    private readonly HandlerFaultReporter _captureFaults = new(
        (_, exception) => Log.Warning("Magnifier capture FAILED: {ExceptionType:l}: {Message:l}",
            exception.GetType().Name, exception.Message),
        () => Environment.TickCount64 / 1000,
        CaptureFailureLogWindowSeconds);

    private PhysicalPoint? _captureCenter;
    private PhysicalPoint? _effectiveCaptureCenter;
    private double _magnification;

    /// <summary>
    /// The capture seam. Production captures the screen with GDI; tests inject a synthetic image so
    /// the show path can be exercised headlessly, and so a failure can be forced deterministically.
    /// </summary>
    internal Func<PhysicalPoint, int, CaptureResult?> Capture { get; set; } = ScreenCaptureService.CaptureRegion;

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
            CaptureNow(_captureCenter!.Value, physicalRadius);
        }
    }

    /// <summary>
    /// Captures and assigns the picture SYNCHRONOUSLY, so the window's first visible frame is this
    /// session's target. The old async path (a 100 ms <c>Task.Delay</c> "to let WPF render") showed
    /// the previous session's picture — or, in the first session of a process, an empty see-through
    /// circle — until the continuation ran. Every overlay that is on screen at this moment is already
    /// excluded from screen capture, so the delay protected nothing.
    ///
    /// <para>
    /// On failure (a null result or a throw) the picture is CLEARED, never left stale: an old
    /// screenshot presented as this target's is worse than an empty circle, because it is a wrong
    /// answer rather than an absent one.
    /// </para>
    /// </summary>
    private void CaptureNow(PhysicalPoint center, int radius)
    {
        try
        {
            var capture = Capture(center, radius);
            if (capture is null)
            {
                ClearPicture();
                _captureFaults.Report("Magnifier capture failed",
                    new InvalidOperationException("the capture returned no image"));
                return;
            }

            // The capture is clamped to the screen, so its centre is the effective one. Written
            // here, with the image, and never when the capture is requested: a request-time value
            // would describe an image the user is not yet looking at.
            _effectiveCaptureCenter = capture.Value.EffectiveCenter;
            ManualSelectionBrush.ImageSource = capture.Value.Image;
        }
        catch (Exception exception)
        {
            // GDI+ capture fails transiently on the secure desktop (UAC), session lock and RDP
            // transitions. This runs on the UI thread on the show path: an escaping throw would be
            // process-fatal, and for a gaze-only user the process is the input channel.
            ClearPicture();
            // One line per 30 s: a capture that keeps failing must not become a per-show log flood,
            // but it must never be silent either — the field report "didn't magnify" needs an answer.
            _captureFaults.Report("Magnifier capture failed", exception);
        }
    }

    /// <summary>Forgets the displayed picture and its effective centre; never leaves the old one up.</summary>
    private void ClearPicture()
    {
        ManualSelectionBrush.ImageSource = null;
        _effectiveCaptureCenter = null;
    }

    public void Recapture()
    {
        // Width is in DIPs; derive the DIP radius, then scale to physical for capture
        var dipRadius = (int)(Width / _magnification / 2);
        var physicalRadius = (int)(dipRadius * DpiHelper.ScaleX);
        CaptureNow(_captureCenter!.Value, physicalRadius);
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
