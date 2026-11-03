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
    private readonly IEyeCareBanner _banner;
    private readonly IEyeCareExercise _exerciseWindow;
    private readonly PhysicalPoint _screenCenter;
    private readonly int _bannerAutoSnoozeSeconds;
    private readonly BlockedInputGate? _blockedInput;

    // While an overlay is on screen this points at its OnGaze; null otherwise.
    private Action<PhysicalPoint, long>? _gazeSink;

    // C10 fixup-01 F3: a break due while blocked waits on the gate. This source lets eye-care off
    // (CancelActive) or a new block (CancelBanner) abandon that wait instead of showing late.
    private TaskCompletionSource? _cancelPresent;

    public EyeCareHooks(
        IMessenger messenger,
        Dispatcher dispatcher,
        IEyeCareBanner banner,
        IEyeCareExercise exerciseWindow,
        PhysicalPoint screenCenter,
        int bannerAutoSnoozeSeconds = 15,
        BlockedInputGate? blockedInput = null)
    {
        _dispatcher = dispatcher;
        _banner = banner;
        _exerciseWindow = exerciseWindow;
        _screenCenter = screenCenter;
        _bannerAutoSnoozeSeconds = bannerAutoSnoozeSeconds;
        _blockedInput = blockedInput;

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
            // C10 fixup-01 F3, coordinator decision: the blocked-input banner wins. A break due
            // while Windows discards every click waits for the clear, then shows; a new block
            // before that re-waits. CancelBanner/CancelActive abandon the wait (return false →
            // the service snoozes), so a disabled eye-care feature never pops a stale prompt.
            while (_blockedInput is { IsBlocked: true })
            {
                var cancel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _cancelPresent = cancel;
                try
                {
                    if (await Task.WhenAny(_blockedInput.WaitForClearAsync(), cancel.Task) == cancel.Task)
                        return false;
                }
                finally
                {
                    if (ReferenceEquals(_cancelPresent, cancel))
                        _cancelPresent = null;
                }
            }

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
        return _dispatcher.InvokeAsync(async () =>
        {
            _gazeSink = _exerciseWindow.OnGaze;   // route live gaze into the exercise overlay
            try { await _exerciseWindow.RunAsync(exerciseName, durationMicroseconds, getTargetAt, recordGaze); }
            finally { _gazeSink = null; }
        }).Task.Unwrap();
    }

    /// <inheritdoc />
    public Task ShowSummaryAsync(EyeCareExerciseOutcome outcome)
    {
        return _dispatcher.InvokeAsync(() => _exerciseWindow.ShowSummaryAsync(outcome)).Task.Unwrap();
    }

    /// <inheritdoc />
    public void CancelActive()
    {
        OnDispatcher(() =>
        {
            AbandonBannerWait();
            _gazeSink = null;
            _banner.Cancel();
            _exerciseWindow.Cancel();
        });
    }

    /// <inheritdoc />
    public void CancelBanner()
    {
        OnDispatcher(() =>
        {
            // Only the banner: a running exercise keeps its gaze sink and its Skip exit.
            AbandonBannerWait();
            _banner.Cancel();
        });
    }

    /// <summary>Runs a callback on the UI thread, inline when already there (the coordinator's path)
    /// so a cancellation never waits on a dispatcher that is currently running it.</summary>
    private void OnDispatcher(Action action)
    {
        if (_dispatcher.CheckAccess())
            action();
        else
            _dispatcher.Invoke(action);
    }

    private void AbandonBannerWait()
    {
        var pending = _cancelPresent;
        _cancelPresent = null;
        pending?.TrySetResult();
    }
}
