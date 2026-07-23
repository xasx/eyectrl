namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Threading;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;

public partial class EyeCareBannerWindow : OverlayWindow
{
    private const double StartCenterFromRightDip = 52;
    private const double SnoozeCenterFromRightDip = 132;
    private const double ButtonCenterFromTopDip = 40;
    private const double HitRadiusDip = 40;

    private readonly DwellDetector _dwell;
    private readonly int _dwellMs;
    private PhysicalPoint _startCenter = new(0, 0);
    private PhysicalPoint _snoozeCenter = new(0, 0);
    private DispatcherTimer? _autoSnooze;
    private TaskCompletionSource<bool>? _tcs;

    // Reuse the app's activation dwell time so the buttons feel consistent.
    public EyeCareBannerWindow(int dwellMs = 700, IStruggleTelemetrySink? sink = null)
    {
        InitializeComponent();
        IsClickThrough = true; // set here (read in OverlayWindow.OnSourceInitialized), NOT in XAML
        _dwellMs = dwellMs;
        _dwell = new DwellDetector(cooldownMs: 0, context: DwellContext.EyeCare, sink: sink);
        _dwell.TargetActivated += OnTargetActivated;
    }

    public static (PhysicalPoint Start, PhysicalPoint Snooze, double Radius) ComputeDwellGeometry(
        double screenDipWidth, double scaleX, double scaleY)
    {
        var screenPhysWidth = screenDipWidth * scaleX;
        var physTop = ButtonCenterFromTopDip * scaleY;
        var start = new PhysicalPoint(screenPhysWidth - StartCenterFromRightDip * scaleX, physTop);
        var snooze = new PhysicalPoint(screenPhysWidth - SnoozeCenterFromRightDip * scaleX, physTop);
        return (start, snooze, HitRadiusDip * scaleX);
    }

    /// <summary>Shows the banner and resolves to true=accept, false=snooze/auto-dismiss.</summary>
    public Task<bool> ShowAsync(string message, PhysicalPoint screenCenter, int autoSnoozeSeconds)
    {
        MessageText.Text = $"👁 {message}";
        // Position the two dwell circles at top-right, ~64px diameter, centered vertically in the 80px banner.
        // ActualWidth and SystemParameters.PrimaryScreenWidth are in DIPs; DwellTarget expects physical pixels.
        var screenDipWidth = ActualWidth > 0 ? ActualWidth : SystemParameters.PrimaryScreenWidth;
        var geometry = ComputeDwellGeometry(screenDipWidth, DpiHelper.ScaleX, DpiHelper.ScaleY);
        _startCenter = geometry.Start;
        _snoozeCenter = geometry.Snooze;

        _dwell.ClearTargets();
        _dwell.AddTarget(new DwellTarget("start", _startCenter, geometry.Radius, dwellTimeMs: _dwellMs));
        _dwell.AddTarget(new DwellTarget("snooze", _snoozeCenter, geometry.Radius, dwellTimeMs: _dwellMs));

        WorkDim.Visibility = Visibility.Visible;
        Show();

        _tcs = new TaskCompletionSource<bool>();
        _autoSnooze = new DispatcherTimer(
            TimeSpan.FromSeconds(autoSnoozeSeconds), DispatcherPriority.Normal,
            (_, _) => Resolve(false), Dispatcher);
        _autoSnooze.Start();
        return _tcs.Task;
    }

    /// <summary>Feeds gaze to the dwell detector (called by EyeCareHooks from the gaze stream).</summary>
    public void OnGaze(PhysicalPoint gaze, long timestampUs) => _dwell.Update(gaze, timestampUs);

    /// <summary>Force-dismisses the banner (e.g. eye-care toggled off). Resolves as not-accepted.
    /// No-op when nothing is showing.</summary>
    public void Cancel()
    {
        if (_tcs is not null) Resolve(false);
    }

    private void OnTargetActivated(string id) => Resolve(id == "start");

    private void Resolve(bool accepted)
    {
        _autoSnooze?.Stop();
        _autoSnooze = null;
        Hide();
        WorkDim.Visibility = Visibility.Collapsed;
        _tcs?.TrySetResult(accepted);
        _tcs = null;
    }
}
