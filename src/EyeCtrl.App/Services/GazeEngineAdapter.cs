namespace EyeCtrl.App.Services;

using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using EyeCtrl.Core;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels.Messages;
using EyeCtrl.ViewModels.Helpers;

public class GazeEngineAdapter : IDisposable
{
    private readonly GazeEngine _gazeEngine;
    private readonly InteractionManager _interactionManager;
    private readonly EyeCtrlSettings _settings;
    private readonly IMessenger _messenger;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<GazeEngineAdapter> _logger;
    private readonly IControlFinder _controlFinder;

    // Instrument 1 (Stage 2), null unless DiagnosticsSettings.MeasureUiInstruments is on at startup.
    // Null means the dispatch path below is byte-for-byte what it was before the instrument existed.
    private readonly DispatchLatencyProbe? _dispatchProbe;
    private long _dispatchSamplesSinceReport;

    /// <summary>How many dispatched samples between summary log lines (~60 s at 33 Hz).</summary>
    private const long DispatchReportInterval = 2000;

    public GazeEngineAdapter(
        GazeEngine gazeEngine,
        EyeCtrlSettings settings,
        IMessenger messenger,
        Dispatcher dispatcher,
        ILogger<GazeEngineAdapter> logger,
        IControlFinder controlFinder,
        DispatchLatencyProbe? dispatchProbe = null)
    {
        _logger = logger;
        _dispatchProbe = dispatchProbe;
        _gazeEngine = gazeEngine;
        _interactionManager = gazeEngine.InteractionManager;
        _settings = settings;
        _messenger = messenger;
        _dispatcher = dispatcher;
        _controlFinder = controlFinder;

        // Core -> ViewModel (via messages, marshaled to UI thread)
        _gazeEngine.CursorMoved += OnCursorMoved;
        _interactionManager.ShowActivationButton += OnShowActivationButton;
        _interactionManager.HideActivationButton += OnHideActivationButton;
        _interactionManager.ShowActionMenu += OnShowActionMenu;
        _interactionManager.HideActionMenu += OnHideActionMenu;
        _interactionManager.StartManualSelection += OnStartManualSelection;
        _interactionManager.RestoreManualSelectionView += OnRestoreManualSelectionView;
        _interactionManager.EndManualSelection += OnEndManualSelection;
        _interactionManager.HideManualSelection += OnHideManualSelection;
        _interactionManager.StateChanged += OnStateChanged;
        _interactionManager.ActionRequested += OnActionRequested;
        _interactionManager.RepeatDwellProgressChanged += OnRepeatDwellProgress;

        // ViewModel -> Core (via message subscriptions)
        messenger.Register<ActivationCompletedMessage>(this, async (_, _) =>
        {
            try
            {
                if (_interactionManager.SelectionMode == SelectionMode.AutoSelection)
                {
                    var fixationPoint = _interactionManager.FixationPoint;
                    if (fixationPoint != null)
                    {
                        _interactionManager.AutoSelectionTarget = await ResolveTargetAsync(fixationPoint.Value);
                    }
                }
                if (_interactionManager.SelectionMode == SelectionMode.AutoSelection
                    && _interactionManager.AutoSelectionTarget == null
                    && !_settings.AutoSelection.FallbackToPixelOnNoControl)
                {
                    _interactionManager.OnFixationEnded();
                    return;
                }
                _interactionManager.OnActivationButtonDwelled();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to handle activation completed");
                // Recovery must not throw out of this async void handler, or it crashes the app.
                try { _interactionManager.OnFixationEnded(); }
                catch (Exception resetEx) { _logger.LogError(resetEx, "Failed to reset interaction state after activation error"); }
            }
        });
        // Guarded because this is the entry point to the drag. OnActionSelected(Drag) transitions
        // to Dragging and synchronously raises DragStarted, which presses the physical mouse
        // button — so a throw anywhere downstream unwinds through here into the dispatcher, where
        // the app deliberately does not set Handled and the process dies. A SendInput button-down
        // survives the process, so that crash would leave the button pressed with no app left to
        // release it. DragCoordinator releases on its own failure path; this catch is the outer
        // net for everything else the action switch can reach.
        messenger.Register<ActionSelectedMessage>(this, (_, msg) =>
        {
            try
            {
                _interactionManager.OnActionSelected(msg.Action);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to handle selected action {Action}", msg.Action);

                // Reset with the states this path can actually be IN, not the one the activation
                // handler above recovers from. OnFixationEnded is guarded on CurrentState ==
                // Fixation, and OnActionSelected is never in Fixation when it throws: it enters at
                // ActionMenuVisible/ManualSelectionActionMenuVisible and may already have moved to
                // Dragging or ScrollMode before the throw. Copying that call here looked symmetric
                // and recovered nothing — and being stuck in Dragging or ScrollMode is the worst
                // case, because TogglePause is deliberately blocked in both, so a gaze-only user
                // would be left with no cancel button and no way to pause out of it.
                //
                // All three are themselves state-guarded, so calling them in sequence is safe from
                // any state: at most one does anything, and from FreeLook none of them do.
                try
                {
                    _interactionManager.CompleteDrag();
                    _interactionManager.ExitScrollMode();
                    _interactionManager.OnActionMenuDismissed();
                }
                catch (Exception resetEx) { _logger.LogError(resetEx, "Failed to reset interaction state after action error"); }
            }
        });
        messenger.Register<BackSelectedMessage>(this, (_, _) =>
            _interactionManager.OnManualSelectionBack());
        messenger.Register<ActionMenuDismissedMessage>(this, (_, _) =>
            _interactionManager.OnActionMenuDismissed());
        messenger.Register<PauseToggledMessage>(this, (_, _) =>
            _interactionManager.TogglePause());
        messenger.Register<SelectionModeChangedMessage>(this, (_, msg) =>
        {
            _settings.SelectionMode = msg.Mode;
            _interactionManager.SelectionMode = msg.Mode;
        });

        messenger.Send(new SelectionModeChangedMessage(_settings.SelectionMode));
    }

    // The frame travels whole, gap flag included: the UI-thread dwell surfaces downstream need it to
    // decide whether the sample may advance dwell at all.
    private void OnCursorMoved(GazeFrame frame)
    {
        // Uninstrumented path, and the shipped default: exactly the single BeginInvoke it has always
        // been. The branch is one null check on a readonly field.
        if (_dispatchProbe is null)
        {
            _dispatcher.BeginInvoke(() => SendCursorMovedSafe(frame));
            return;
        }

        // The live gate, read per sample like the recorder's. The probe is constructed once at startup,
        // so without this a user who switched the setting OFF mid-session would go on being measured
        // until they restarted — which contradicts what the setting says it does. (The reverse, OFF→ON
        // mid-session, deliberately does nothing: like the recorder, a measurement that starts halfway
        // through a session is not one this instrument claims to produce.)
        if (!_settings.Diagnostics.MeasureUiInstruments)
        {
            _dispatcher.BeginInvoke(() => SendCursorMovedSafe(frame));
            return;
        }

        // Instrument 1: the timestamp rides the closure this call site already allocates, so measuring
        // costs a Stopwatch read and an interlocked increment on the gaze thread and nothing else. The
        // dispatch itself is untouched — same priority, same uncoalesced one-per-sample posting — because
        // an instrument that changed the queue would be measuring itself.
        long enqueued = _dispatchProbe.Enqueue();
        _dispatcher.BeginInvoke(() =>
        {
            // Recorded FIRST: the number wanted is the wait, not the wait plus the downstream work.
            _dispatchProbe.Execute(enqueued);
            ReportDispatchLatencyPeriodically();
            SendCursorMovedSafe(frame);
        });
    }

    // WeakReferenceMessenger delivers synchronously to every recipient, on the UI thread, at the
    // gaze rate (30-120 Hz): one throwing recipient used to reach DispatcherUnhandledException —
    // instantly burning the standalone fault budget and killing the process. Contain the send
    // itself: the fault is logged (rate-limited to one line per 30s) and the NEXT frame proceeds
    // to the same recipients, so a transient fault self-heals and a persistent one stays visible
    // in the log without stranding the user. Runs on the UI thread only, so no synchronization.
    private long _lastCursorSendFaultMs;
    private void SendCursorMovedSafe(GazeFrame frame)
    {
        try
        {
            _messenger.Send(new GazeCursorMovedMessage(frame));
        }
        catch (Exception ex)
        {
            var now = Environment.TickCount64;
            if (now - _lastCursorSendFaultMs > 30_000)
            {
                _lastCursorSendFaultMs = now;
                _logger.LogError(ex, "GazeCursorMovedMessage recipient threw (contained; further lines suppressed for 30s)");
            }
        }
    }

    // Emitted periodically rather than only at shutdown: a measurement session that ends in a crash,
    // a kill, or the user simply closing the lid would otherwise produce nothing at all.
    private void ReportDispatchLatencyPeriodically()
    {
        if (_dispatchProbe is null) return;
        if (++_dispatchSamplesSinceReport < DispatchReportInterval) return;

        _dispatchSamplesSinceReport = 0;
        _logger.LogInformation("UI instrument 1 — {Summary}", _dispatchProbe.Describe());
    }

    private void OnShowActivationButton(PhysicalPoint point) =>
        _dispatcher.BeginInvoke(() => _messenger.Send(new ShowActivationButtonMessage(point)));

    private void OnHideActivationButton() =>
        _dispatcher.BeginInvoke(() => _messenger.Send(new HideActivationButtonMessage()));

    private void OnShowActionMenu(PhysicalPoint point) =>
        _dispatcher.BeginInvoke(() =>
        {
            var im = _interactionManager;
            var showBack = im.CurrentState == InteractionState.ManualSelectionActionMenuVisible;
            var dwellMs = showBack
                ? _settings.Dwell.ManualSelectionActionMenuDwellMs
                : _settings.Dwell.ActionMenuDwellMs;

            var ManualSelectionDisplayRadius = showBack
                ? (int)(_settings.ManualSelection.CircleRadiusPixels * _settings.ManualSelection.MagnificationLevel)
                : 0;

            // Get screen dimensions in physical pixels
            var physicalScreenW = DpiHelper.LogicalToPhysicalX(System.Windows.SystemParameters.PrimaryScreenWidth);
            var physicalScreenH = DpiHelper.LogicalToPhysicalY(System.Windows.SystemParameters.PrimaryScreenHeight);

            // Calculate display point using helper (moves coordinate logic to ViewModels layer)
            var circleSize = _settings.Cursor.CircleSize;
            var clampedPoint = ActionMenuLayoutHelper.CalculateDisplayPoint(
                point,
                _settings.ViewPosition,
                circleSize,
                physicalScreenW,
                physicalScreenH);

            _messenger.Send(new ShowActionMenuMessage(
                clampedPoint,
                _settings.ActionMenu.VisibleActions,
                showBack,
                dwellMs,
                point, // GazePoint - always pass the original gaze point for indicator positioning
                ManualSelectionDisplayRadius,
                circleSize)
            {
                // ManualSelection path only (showBack == true): pass ManualSelection circle context for preview capture.
                // ManualSelectionCenter = the ORIGINAL pre-ManualSelection fixation (center of ManualSelection circle), NOT the precise in-ManualSelection point.
                // The precise in-ManualSelection point is already in clampedPoint / message.Center.
                // FixationPoint is always non-null in the ManualSelection path (it is set before ManualSelection activation).
                // ManualSelectionRadius is logical/DIP (CircleRadiusPixels); ActionMenuWindow converts it to physical with ScaleX.
                ManualSelectionCenter = showBack ? _interactionManager.FixationPoint! : null,
                ManualSelectionRadius = showBack ? _settings.ManualSelection.CircleRadiusPixels : null,
                ResolvedControl = im.SelectionMode == SelectionMode.AutoSelection ? im.AutoSelectionTarget : null
            });
        });

    private void OnHideActionMenu() =>
        _dispatcher.BeginInvoke(() => _messenger.Send(new HideActionMenuMessage()));

    // Fresh entry: capture a new screenshot. Back from the action menu: reuse the existing one.
    private void OnStartManualSelection(PhysicalPoint point) => SendStartManualSelection(point, recapture: true);
    private void OnRestoreManualSelectionView(PhysicalPoint point) => SendStartManualSelection(point, recapture: false);

    private void SendStartManualSelection(PhysicalPoint point, bool recapture) =>
        _dispatcher.BeginInvoke(() =>
        {
            // Get screen dimensions in physical pixels
            var physicalScreenW = DpiHelper.LogicalToPhysicalX(System.Windows.SystemParameters.PrimaryScreenWidth);
            var physicalScreenH = DpiHelper.LogicalToPhysicalY(System.Windows.SystemParameters.PrimaryScreenHeight);

            // Calculate display point using helper (moves coordinate logic to ViewModels layer)
            var displayPoint = ManualSelectionLayoutHelper.CalculateDisplayPoint(
                point,
                _settings.ViewPosition,
                physicalScreenW,
                physicalScreenH);

            _messenger.Send(new StartManualSelectionMessage(
                displayPoint,
                _settings.ManualSelection.CircleRadiusPixels,
                _settings.ManualSelection.MagnificationLevel,
                point,  // GazePoint - always pass the original gaze point for capture
                recapture
            ));
        });

    private void OnEndManualSelection() =>
        _dispatcher.BeginInvoke(() => _messenger.Send(new EndManualSelectionMessage()));

    private void OnHideManualSelection() =>
        _dispatcher.BeginInvoke(() => _messenger.Send(new ManualSelectionHiddenMessage()));

    private void OnStateChanged(InteractionState state)
    {
        _logger.LogInformation("State → {State}", state);
        _dispatcher.BeginInvoke(() => _messenger.Send(new StateChangedMessage(state)));
    }

    private void OnRepeatDwellProgress(double progress) =>
        _dispatcher.BeginInvoke(() => _messenger.Send(new RepeatDwellProgressChangedMessage(progress)));

    private void OnActionRequested(InteractionAction action, PhysicalPoint point)
    {
        var isPrecise = _interactionManager.CurrentState == InteractionState.ManualSelectionActionMenuVisible
                     || (_interactionManager.CurrentState == InteractionState.ActionMenuVisible
                         && _interactionManager.SelectionMode == SelectionMode.AutoSelection)
                     // Repeat-mode dwell-fires carry the exact fixated point — a completed dwell
                     // is at least as deliberate as a zoomed selection, and the dispatcher's
                     // StableCentroid substitution would otherwise move the click off the point
                     // the suppression zones were checked against (review round 3 finding 4).
                     || _interactionManager.CurrentState == InteractionState.RepeatMode;
        _dispatcher.BeginInvoke(() => _messenger.Send(new ActionRequestedMessage(action, point, isPrecise)));
    }

    private async Task<ControlInfo?> ResolveTargetAsync(PhysicalPoint point)
    {
        try
        {
            return await Task.Run(() => _controlFinder.FindTarget(point))
                .WaitAsync(TimeSpan.FromMilliseconds(500));
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("ControlFinder timed out after 500ms — falling back to pixel");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ControlFinder failed — falling back to pixel");
            return null;
        }
    }

    public void Dispose()
    {
        // Final instrument-1 summary: the periodic emit above can leave up to DispatchReportInterval
        // samples unreported, and on a short session that is all of them.
        if (_dispatchProbe is { Latency.Count: > 0 })
            _logger.LogInformation("UI instrument 1 (final) — {Summary}", _dispatchProbe.Describe());

        _gazeEngine.CursorMoved -= OnCursorMoved;
        _interactionManager.ShowActivationButton -= OnShowActivationButton;
        _interactionManager.HideActivationButton -= OnHideActivationButton;
        _interactionManager.ShowActionMenu -= OnShowActionMenu;
        _interactionManager.HideActionMenu -= OnHideActionMenu;
        _interactionManager.StartManualSelection -= OnStartManualSelection;
        _interactionManager.RestoreManualSelectionView -= OnRestoreManualSelectionView;
        _interactionManager.EndManualSelection -= OnEndManualSelection;
        _interactionManager.HideManualSelection -= OnHideManualSelection;
        _interactionManager.StateChanged -= OnStateChanged;
        _interactionManager.ActionRequested -= OnActionRequested;
        _interactionManager.RepeatDwellProgressChanged -= OnRepeatDwellProgress;
        _messenger.UnregisterAll(this);
        GC.SuppressFinalize(this);
    }
}
