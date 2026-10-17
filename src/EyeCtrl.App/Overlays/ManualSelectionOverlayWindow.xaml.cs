namespace EyeCtrl.App.Overlays;

using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media.Imaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using Serilog;

public partial class ManualSelectionOverlayWindow : OverlayWindow, IManualSelectionOverlay
{
    private const int CaptureFailureLogWindowSeconds = 30;
    private const string PictureFresh = "fresh";
    private const string PictureReused = "reused";
    private const string PictureNone = "none";

    private readonly HandlerFaultReporter _captureFaults = new(
        (_, exception) => Log.Warning("Magnifier capture FAILED: {ExceptionType:l}: {Message:l}",
            exception.GetType().Name, exception.Message),
        () => Environment.TickCount64 / 1000,
        CaptureFailureLogWindowSeconds);

    private PhysicalPoint? _captureCenter;
    private PhysicalPoint? _effectiveCaptureCenter;
    private double _magnification;

    // Field evidence for the show line. Statistics only — never pixels.
    private string _pictureState = PictureNone;
    private long _lastCaptureMs;
    private int _lastCapturedSize;
    private double _lastLumaMean;
    private double _lastLumaSd;
    private bool _lastCaptureSameAsPrevious;
    private ulong? _previousSignature;
    private int _previousSignatureSize;

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
        PrepareForShow(center, radius, magnification, gazePoint, recapture);
        Show();
    }

    /// <summary>
    /// Everything that must happen BEFORE the window becomes visible: the geometry and synchronous
    /// capture (<see cref="UpdateCenter"/>) and the one INFO shown line. Extracted from
    /// <see cref="Show"/> so the line's emission is testable without an on-screen <c>Show()</c>; the
    /// source pin (<c>ManualSelectionOverlayWindowWiringTests</c>) keeps <c>Show</c> calling it, so a
    /// deleted emit cannot stay green.
    /// </summary>
    internal void PrepareForShow(PhysicalPoint center, int radius, double magnification, PhysicalPoint? gazePoint = null, bool recapture = true)
    {
        UpdateCenter(center, radius, magnification, gazePoint, recapture);
        Log.Information("{MagnifierShown:l}", BuildShownLine(center, radius, recapture));
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
        else
        {
            // Back from the action menu: the same shot, not a new capture.
            _pictureState = PictureReused;
            _lastCaptureSameAsPrevious = true;
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
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var capture = Capture(center, radius);
            if (capture is null)
            {
                OnCaptureFailed(new InvalidOperationException("the capture returned no image"));
                return;
            }

            var image = capture.Value.Image;
            var stats = AnalyzeSafely(image);
            _lastCaptureSameAsPrevious =
                stats.Signature is ulong signature
                && signature == _previousSignature
                && capture.Value.Size == _previousSignatureSize;
            if (stats.Signature is ulong capturedSignature)
            {
                _previousSignature = capturedSignature;
                _previousSignatureSize = capture.Value.Size;
            }

            _lastCapturedSize = capture.Value.Size;
            _lastLumaMean = stats.Mean;
            _lastLumaSd = stats.StandardDeviation;
            _pictureState = PictureFresh;

            // The capture is clamped to the screen, so its centre is the effective one. Written
            // here, with the image, and never when the capture is requested: a request-time value
            // would describe an image the user is not yet looking at.
            _effectiveCaptureCenter = capture.Value.EffectiveCenter;
            ManualSelectionBrush.ImageSource = image;
        }
        catch (Exception exception)
        {
            // GDI+ capture fails transiently on the secure desktop (UAC), session lock and RDP
            // transitions. This runs on the UI thread on the show path: an escaping throw would be
            // process-fatal, and for a gaze-only user the process is the input channel.
            OnCaptureFailed(exception);
        }
        finally
        {
            _lastCaptureMs = (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        }
    }

    /// <summary>Clears the picture and records a failed capture; the warning is rate-limited to one per 30 s.</summary>
    private void OnCaptureFailed(Exception exception)
    {
        ClearPicture();
        _pictureState = PictureNone;
        _lastCapturedSize = 0;
        _lastLumaMean = 0;
        _lastLumaSd = 0;
        _lastCaptureSameAsPrevious = false;
        // A capture that keeps failing must not become a per-show log flood, but it must never be
        // silent either — the field report "didn't magnify" needs an answer.
        _captureFaults.Report("Magnifier capture failed", exception);
    }

    /// <summary>
    /// The luma/signature statistics are log-only: a failure to compute them must not cost the user
    /// the picture that did arrive.
    /// </summary>
    private static (double Mean, double StandardDeviation, ulong? Signature) AnalyzeSafely(BitmapSource image)
    {
        try
        {
            var stats = MagnifierCaptureStats.Analyze(image);
            return (stats.Mean, stats.StandardDeviation, stats.Signature);
        }
        catch
        {
            return (0, 0, null);
        }
    }

    /// <summary>
    /// The one INFO line that makes a "didn't magnify" session readable from the log: where the view
    /// is, the picture's provenance, how long the capture took, and a content signature (luma
    /// statistics and "same as the previous capture"). Statistics only — no screen content is kept.
    /// Internal so the line can be pinned headlessly without showing the window.
    /// </summary>
    internal string BuildShownLine(PhysicalPoint viewCenter, int radius, bool recapture)
    {
        var captureCenter = _captureCenter!.Value;
        return string.Create(CultureInfo.InvariantCulture, $"Magnifier shown: P=({captureCenter.X:F0},{captureCenter.Y:F0}) view=({viewCenter.X:F0},{viewCenter.Y:F0}) mag={_magnification:0.##} viewDip={radius * 2 * _magnification:F0} recapture={recapture} picture={_pictureState} captureMs={_lastCaptureMs} captured={_lastCapturedSize}x{_lastCapturedSize}px luma={_lastLumaMean:F0}\u00B1{_lastLumaSd:F0} sameAsPrevious={_lastCaptureSameAsPrevious}");
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
