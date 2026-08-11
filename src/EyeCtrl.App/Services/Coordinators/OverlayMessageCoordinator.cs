namespace EyeCtrl.App.Services.Coordinators;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels.Messages;
using Serilog;

/// <summary>
/// Owns the message-driven half of <see cref="EventCoordinator"/>'s overlay surface: the
/// ManualSelection/action-menu/backdrop-dismiss/calibration message registrations. Extracted from
/// the former <c>OverlayBindingCoordinator</c> as part of the sub-coordinator decomposition
/// (cluster 1), split along the stimulus axis in T10 — the property-change-driven half lives in
/// <see cref="OverlayVmBindingCoordinator"/>. Reaches back into that sibling for the one cross-side
/// seam (<c>_vmBinding.HideBackdrop()</c>) since the backdrop window is owned there.
/// </summary>
internal sealed class OverlayMessageCoordinator : IDisposable
{
    private EventCoordinationContext? _context;
    private readonly ToolbarLifecycleCoordinator _toolbar;
    private readonly OverlayVmBindingCoordinator _vmBinding;
    private bool _disposed;

    public OverlayMessageCoordinator(ToolbarLifecycleCoordinator toolbar, OverlayVmBindingCoordinator vmBinding)
    {
        _toolbar = toolbar;
        _vmBinding = vmBinding;
    }

    public void Initialize(EventCoordinationContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context)); // MANDATORY — see "How to read"

        SetupMessageHandlers();
    }

    private void SetupMessageHandlers()
    {
        if (_context == null) return;

        var im = _context.InteractionManager;
        var ManualSelectionOverlay = _context.OverlayManager.ManualSelectionOverlay;
        var ManualSelectionIndicator = _context.OverlayManager.ManualSelectionIndicator;

        // ManualSelectionDwellCompleted: Handle coordinate conversion
        _context.Messenger.Register<OverlayMessageCoordinator, ManualSelectionDwellCompletedMessage>(this, (_, msg) =>
            _context.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    ManualSelectionIndicator.Hide();
                    var realPoint = ManualSelectionOverlay.GetRealPhysicalPoint(msg.PrecisePoint);
                    im.OnManualSelectionDwell(realPoint);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error handling ManualSelection dwell completion");
                }
            }));

        // ResetManualSelectionIndicator: Trigger animation
        _context.Messenger.Register<OverlayMessageCoordinator, ResetManualSelectionIndicatorMessage>(this, (_, msg) =>
            _context.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (!ManualSelectionIndicator.IsVisible) return;
                    var targetDiameter = _context.Settings.ManualSelection.GazeIndicatorOuterDiameter;
                    ManualSelectionIndicator.AnimateReset(targetDiameter, _context.ManualSelectionIndicatorVM.ResetAnimationMs);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error animating ManualSelection indicator reset");
                }
            }));

        // ManualSelectionCloseMessage -> exit ManualSelection mode
        _context.Messenger.Register<OverlayMessageCoordinator, ManualSelectionCloseMessage>(this, (_, _) =>
            _context.Dispatcher.BeginInvoke(() => im.OnManualSelectionBack()));

        // ManualSelectionRecaptureMessage -> re-capture at same fixation point
        _context.Messenger.Register<OverlayMessageCoordinator, ManualSelectionRecaptureMessage>(this, (_, _) =>
            _context.Dispatcher.BeginInvoke(() =>
            {
                if (im.CurrentState == InteractionState.ManualSelectionActive && im.FixationPoint != null)
                {
                    _vmBinding.HideBackdrop();
                    _context.Messenger.Send(new StartManualSelectionMessage(
                        im.FixationPoint.Value,
                        _context.Settings.ManualSelection.CircleRadiusPixels,
                        _context.Settings.ManualSelection.MagnificationLevel,
                        im.FixationPoint.Value));
                }
            }));

        // ShowActionMenu -> highlight overlay if auto-selection target resolved
        _context.Messenger.Register<OverlayMessageCoordinator, ShowActionMenuMessage>(this, (_, msg) =>
            _context.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (msg.ResolvedControl == null || _context == null) return;
                    var highlightVM = _context.TargetHighlightVM;
                    highlightVM.IsVisible = true;
                    _context.OverlayManager.TargetHighlight.Configure(
                        _context.Settings.AutoSelection.HighlightColor,
                        _context.Settings.AutoSelection.HighlightThickness,
                        _context.Settings.AutoSelection.HighlightOpacity);
                    _context.OverlayManager.TargetHighlight.PositionAt(msg.ResolvedControl.BoundingRect);
                    _context.OverlayManager.TargetHighlight.ShowHighlight();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error showing target highlight overlay");
                }
            }));

        // HideActionMenu -> hide highlight overlay
        _context.Messenger.Register<OverlayMessageCoordinator, HideActionMenuMessage>(this, (_, _) =>
            _context.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (_context != null && _context.TargetHighlightVM.IsVisible)
                    {
                        _context.TargetHighlightVM.IsVisible = false;
                        _context.OverlayManager.TargetHighlight.HideHighlight();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error hiding target highlight overlay");
                }
            }));

        // BackdropDismissMessage -> hide backdrop, highlight, and navigate back
        _context.Messenger.Register<OverlayMessageCoordinator, BackdropDismissMessage>(this, (_, _) =>
            _context.Dispatcher.BeginInvoke(() =>
            {
                var context = _context;
                if (context == null)
                    return;

                _vmBinding.HideBackdrop();

                if (context.TargetHighlightVM.IsVisible)
                {
                    context.TargetHighlightVM.IsVisible = false;
                    context.OverlayManager.TargetHighlight.HideHighlight();
                }

                var im = context.InteractionManager;
                if (im.CurrentState == InteractionState.ActionMenuVisible)
                    im.OnActionMenuDismissed();
                else if (im.CurrentState == InteractionState.ManualSelectionActive)
                    im.OnManualSelectionBack();
            }));

        // Calibration: show/hide overlay on state change, suppress interaction during capture
        _context.Messenger.Register<OverlayMessageCoordinator, CalibrationStateChangedMessage>(this, (_, msg) =>
            _context.Dispatcher.BeginInvoke(() =>
            {
                var calWindow = _context.OverlayManager.Calibration;
                var cursorCircle = _context.OverlayManager.CursorCircle;
                switch (msg.State)
                {
                    case CalibrationState.Collecting:
                    case CalibrationState.Training:
                        _context.GazeEngine.SuppressFixation = true;
                        // Deactivate toolbar dwell while gaze is being collected at calibration
                        // targets — they can land on screen edges/corners where a docked toolbar
                        // lives, and would otherwise dwell-activate a hidden/stale button position.
                        _toolbar.SetActive(false);
                        // Hide the cursor circle so it doesn't compete with the calibration dot.
                        cursorCircle.Hide();
                        calWindow.UpdateVisualState(msg.State, msg.ProgressPercent);
                        calWindow.Show();
                        break;
                    case CalibrationState.Active:
                    case CalibrationState.Failed:
                        _context.GazeEngine.SuppressFixation = false;
                        // Re-activate toolbar dwell via the auto-hide controller, which is
                        // state-aware: it re-asserts a hide if the toolbar auto-hid while Feed was
                        // suppressed (calibration suppresses Feed on IsSessionActive, which flips
                        // false at CompleteSession — before the terminal CalibrationState arrives).
                        // NOTE: CalibrationState.Active means "calibration COMPLETE and transform
                        // applied" (success), NOT "in progress" — wiring it to SetActive(false)
                        // would permanently kill toolbar dwell on configs where ShowToolbar() never
                        // fires (floating toolbar / mouse provider — auto-hide disabled, no
                        // recovery short of restart). ReassertVisibility preserves that: when
                        // auto-hide is disabled (_enabled == false) the else-branch activates.
                        _toolbar.ReassertVisibility();
                        // Restore the cursor circle so the user can aim at Retry/Accept.
                        if (_context.CursorVM.IsVisible) cursorCircle.Show();
                        calWindow.UpdateVisualState(msg.State, msg.ProgressPercent);
                        calWindow.Show();
                        break;
                    default:
                        // CalibrationState.Idle / CalibrationState.Cancelled — calibration is over
                        // (not in progress), so toolbar dwell resumes — but only if the toolbar
                        // hasn't since auto-hidden (same state-aware path as the Active/Failed case).
                        _context.GazeEngine.SuppressFixation = false;
                        _toolbar.ReassertVisibility();
                        if (_context.CursorVM.IsVisible) cursorCircle.Show();
                        calWindow.DismissCalibration();
                        break;
                }
            }));
    }

    public void Dispose()
    {
        if (_disposed || _context is null) { _disposed = true; return; }

        _context.Messenger.UnregisterAll(this);

        _disposed = true;
    }
}
