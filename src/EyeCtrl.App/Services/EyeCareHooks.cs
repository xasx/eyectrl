namespace EyeCtrl.App.Services;

using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.App.Overlays;
using EyeCtrl.ViewModels.Messages;

/// <summary>App-side implementation of IEyeCareHooks. Bridges EyeCare logic to WPF overlays
/// and routes the live gaze stream into whichever eye-care overlay is currently active.</summary>
public sealed class EyeCareHooks : IEyeCareHooks
{
    private readonly Dispatcher _dispatcher;
    private readonly EyeCareBannerWindow _banner;
    private readonly PhysicalPoint _screenCenter;
    private readonly int _bannerAutoSnoozeSeconds;

    // While an overlay is on screen this points at its OnGaze; null otherwise.
    private Action<PhysicalPoint, long>? _gazeSink;

    public EyeCareHooks(
        IMessenger messenger,
        Dispatcher dispatcher,
        EyeCareBannerWindow banner,
        PhysicalPoint screenCenter,
        int bannerAutoSnoozeSeconds = 15)
    {
        _dispatcher = dispatcher;
        _banner = banner;
        _screenCenter = screenCenter;
        _bannerAutoSnoozeSeconds = bannerAutoSnoozeSeconds;

        // CRITICAL: route gaze to the active overlay. Without this the banner's dwell buttons
        // never receive gaze (only auto-snooze works) and the exercise records nothing (all-zero
        // scores). We reuse GazeCursorMovedMessage — the same UI-thread gaze stream the cursor,
        // toolbar, and manual-selection consume (marshalled to the UI thread by GazeEngineAdapter),
        // so the overlays are touched only on the UI thread.
        messenger.Register<GazeCursorMovedMessage>(this,
            (r, m) => _gazeSink?.Invoke(m.Position, m.TimestampMicroseconds));
    }

    /// <inheritdoc />
    public Task<bool> ShowBannerAsync(string message)
    {
        return _dispatcher.InvokeAsync(async () =>
        {
            _gazeSink = _banner.OnGaze;       // start feeding gaze to the banner's DwellDetector
            try { return await _banner.ShowAsync(message, _screenCenter, _bannerAutoSnoozeSeconds); }
            finally { _gazeSink = null; }     // stop feeding once dismissed
        }).Task.Unwrap();
    }

    /// <inheritdoc />
    public Task ShowExerciseAsync(
        string exerciseName, long durationMicroseconds,
        Func<long, PhysicalPoint> getTargetAt, Action<PhysicalPoint, long> recordGaze)
    {
        // Filled in Task 17 (sets _gazeSink to the exercise window's OnGaze).
        throw new NotImplementedException("Exercise window wired in Task 17");
    }

    /// <inheritdoc />
    public Task ShowSummaryAsync(EyeCareExerciseOutcome outcome)
    {
        // Filled in Task 18
        throw new NotImplementedException("Summary wired in Task 18");
    }
}
