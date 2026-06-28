namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Threading;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;

public partial class EyeCareBannerWindow : OverlayWindow
{
    private readonly DwellDetector _dwell;
    private readonly int _dwellMs;
    private PhysicalPoint _startCenter = new(0, 0);
    private PhysicalPoint _snoozeCenter = new(0, 0);
    private DispatcherTimer? _autoSnooze;
    private TaskCompletionSource<bool>? _tcs;

    // Reuse the app's activation dwell time so the buttons feel consistent.
    public EyeCareBannerWindow(int dwellMs = 700)
    {
        InitializeComponent();
        IsClickThrough = true; // set here (read in OverlayWindow.OnSourceInitialized), NOT in XAML
        _dwellMs = dwellMs;
        _dwell = new DwellDetector(cooldownMs: 0);
        _dwell.TargetActivated += OnTargetActivated;
    }

    /// <summary>Shows the banner and resolves to true=accept, false=snooze/auto-dismiss.</summary>
    public Task<bool> ShowAsync(string message, PhysicalPoint screenCenter, int autoSnoozeSeconds)
    {
        MessageText.Text = $"👁 {message}";
        // Position the two dwell circles at top-right, ~64px diameter, centered vertically in the 80px banner.
        // ActualWidth and SystemParameters.PrimaryScreenWidth are in DIPs; DwellTarget expects physical pixels.
        var screenDipWidth = ActualWidth > 0 ? ActualWidth : SystemParameters.PrimaryScreenWidth;
        var screenPhysWidth = DpiHelper.LogicalToPhysicalX(screenDipWidth);
        var physTop = DpiHelper.LogicalToPhysicalY(40);
        _startCenter = new PhysicalPoint(screenPhysWidth - 60, physTop);
        _snoozeCenter = new PhysicalPoint(screenPhysWidth - 140, physTop);

        _dwell.ClearTargets();
        // DwellTarget ctor is (string id, PhysicalPoint center, double radius, int dwellTimeMs) — ms, not µs.
        _dwell.AddTarget(new DwellTarget("start", _startCenter, radius: 60, dwellTimeMs: _dwellMs));
        _dwell.AddTarget(new DwellTarget("snooze", _snoozeCenter, radius: 60, dwellTimeMs: _dwellMs));

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
