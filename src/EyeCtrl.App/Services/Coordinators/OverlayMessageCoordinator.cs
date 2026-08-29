namespace EyeCtrl.App.Services.Coordinators;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;
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
internal sealed class OverlayMessageCoordinator : CoordinatorBase
{
    /// <summary>
    /// Inset of the right-edge bar from the screen edge, in DIP. Declared here rather than
    /// widening <c>OverlayVmBindingCoordinator</c>'s private const of the same name: the two
    /// coordinators anchor the same bar and must agree, and the value is the one every other
    /// context (drag, manual selection) already uses.
    /// </summary>
    private const double RightEdgeInsetLogical = 30;

    private readonly ToolbarLifecycleCoordinator _toolbar;
    private readonly OverlayVmBindingCoordinator _vmBinding;
    public OverlayMessageCoordinator(ToolbarLifecycleCoordinator toolbar, OverlayVmBindingCoordinator vmBinding)
    {
        // Both are dereferenced unconditionally by the calibration handler (_toolbar.SetActive /
        // ReassertVisibility) and the backdrop seam (_vmBinding.HideBackdrop).
        _toolbar = toolbar ?? throw new ArgumentNullException(nameof(toolbar));
        _vmBinding = vmBinding ?? throw new ArgumentNullException(nameof(vmBinding));
    }

    protected override void OnInitialize()
    {
        var context = Context!;

        SetupMessageHandlers();
    }

    private void SetupMessageHandlers()
    {
        if (Context is null) return;

        var im = Context.InteractionManager;
        var ManualSelectionOverlay = Context.OverlayManager.ManualSelectionOverlay;
        var ManualSelectionIndicator = Context.OverlayManager.ManualSelectionIndicator;

        // ManualSelectionDwellCompleted: Handle coordinate conversion
        Context.Messenger.Register<OverlayMessageCoordinator, ManualSelectionDwellCompletedMessage>(this, (_, msg) =>
            Context.Dispatcher.BeginInvoke(() =>
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
        Context.Messenger.Register<OverlayMessageCoordinator, ResetManualSelectionIndicatorMessage>(this, (_, msg) =>
            Context.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (!ManualSelectionIndicator.IsVisible) return;
                    var targetDiameter = Context.Settings.ManualSelection.GazeIndicatorOuterDiameter;
                    ManualSelectionIndicator.AnimateReset(targetDiameter, Context.ManualSelectionIndicatorVM.ResetAnimationMs);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error animating ManualSelection indicator reset");
                }
            }));

        // ManualSelectionCloseMessage -> exit ManualSelection mode
        Context.Messenger.Register<OverlayMessageCoordinator, ManualSelectionCloseMessage>(this, (_, _) =>
            Context.Dispatcher.BeginInvoke(() => im.OnManualSelectionBack()));

        // ManualSelectionRecaptureMessage -> re-capture at same fixation point
        Context.Messenger.Register<OverlayMessageCoordinator, ManualSelectionRecaptureMessage>(this, (_, _) =>
            Context.Dispatcher.BeginInvoke(() =>
            {
                if (im.CurrentState == InteractionState.ManualSelectionActive && im.FixationPoint != null)
                {
                    _vmBinding.HideBackdrop();
                    Context.Messenger.Send(new StartManualSelectionMessage(
                        im.FixationPoint.Value,
                        Context.Settings.ManualSelection.CircleRadiusPixels,
                        Context.Settings.ManualSelection.MagnificationLevel,
                        im.FixationPoint.Value));
                }
            }));

        // ShowActionMenu -> highlight overlay if auto-selection target resolved
        Context.Messenger.Register<OverlayMessageCoordinator, ShowActionMenuMessage>(this, (_, msg) =>
            Context.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (Context is null) return;

                    // The menu was the only overlay context that put nothing on the right-edge
                    // bar, so an accidental open could only be resolved by committing an action.
                    //
                    // ShowButtons, not AddButton: AddButton sets neither IsVisible nor the bar's
                    // edge anchor, so a bar that was not already showing stays hidden and anchors
                    // every button at (0,0). Clearing is safe here — on the ManualSelection path
                    // the magnifier's own buttons are already gone by this point, because
                    // InteractionManager.OnManualSelectionDwell fires HideManualSelection BEFORE
                    // ShowActionMenu and both handlers queue at DispatcherPriority.Normal, which
                    // is FIFO. That ordering is a real WPF guarantee but invisible from here:
                    // changing either handler's dispatcher priority would silently invert it.
                    var edgeAnchor = ScreenGeometry.RightEdgeAnchor(
                        RightEdgeInsetLogical, UIConstants.ButtonRadius);
                    Context.OverlayManager.EdgeButtonBar.ShowButtons(
                        ActionMenuEdgeButtons.Definitions(
                            msg.ShowBack
                                ? Context.Settings.Dwell.ManualSelectionActionMenuDwellMs
                                : Context.Settings.Dwell.ActionMenuDwellMs),
                        edgeAnchor);

                    if (msg.ResolvedControl == null) return;
                    var highlightVM = Context.TargetHighlightVM;
                    highlightVM.IsVisible = true;
                    Context.OverlayManager.TargetHighlight.Configure(
                        Context.Settings.AutoSelection.HighlightColor,
                        Context.Settings.AutoSelection.HighlightThickness,
                        Context.Settings.AutoSelection.HighlightOpacity);
                    Context.OverlayManager.TargetHighlight.PositionAt(msg.ResolvedControl.BoundingRect);
                    Context.OverlayManager.TargetHighlight.ShowHighlight();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error showing target highlight overlay");
                }
            }));

        // HideActionMenu -> hide highlight overlay
        Context.Messenger.Register<OverlayMessageCoordinator, HideActionMenuMessage>(this, (_, _) =>
            Context.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    // Takes the menu's Close/Help off the bar, but ONLY while the menu still owns
                    // it. The bar is shared, and this handler is deferred while the contexts that
                    // take the bar over from the menu are not:
                    //
                    //   InteractionManager.OnActionSelected fires HideActionMenu (:148, :156) and
                    //   then DragStarted (:163) / SetState(ScrollMode) (:152). HideActionMenu
                    //   reaches here through GazeEngineAdapter.OnHideActionMenu, which wraps it in
                    //   Dispatcher.BeginInvoke — so it lands AFTER DragCoordinator.OnDragStarted
                    //   and ScrollModeController.EnterScrollMode, both of which run synchronously
                    //   off their events and have already called ShowButtons by then.
                    //
                    // Unguarded, this would therefore clear the drag's cancel button and scroll
                    // mode's stop button the instant either began — removing the only way out of
                    // a drag for a user whose sole input channel is an eye tracker.
                    var bar = Context?.OverlayManager.EdgeButtonBar;
                    if (bar?.EdgeBarVm?.DefinitionFor(ActionMenuEdgeButtons.CloseId) is not null)
                        bar.Hide();

                    if (Context is not null && Context.TargetHighlightVM.IsVisible)
                    {
                        Context.TargetHighlightVM.IsVisible = false;
                        Context.OverlayManager.TargetHighlight.HideHighlight();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error hiding target highlight overlay");
                }
            }));

        // BackdropDismissMessage -> hide backdrop, highlight, and navigate back
        Context.Messenger.Register<OverlayMessageCoordinator, BackdropDismissMessage>(this, (_, _) =>
            Context.Dispatcher.BeginInvoke(() =>
            {
                var context = Context;
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
        Context.Messenger.Register<OverlayMessageCoordinator, CalibrationStateChangedMessage>(this, (_, msg) =>
            Context.Dispatcher.BeginInvoke(() =>
            {
                var calWindow = Context.OverlayManager.Calibration;
                var cursorCircle = Context.OverlayManager.CursorCircle;
                switch (msg.State)
                {
                    case CalibrationState.Collecting:
                    case CalibrationState.Training:
                        Context.GazeEngine.SuppressFixation = true;
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
                        Context.GazeEngine.SuppressFixation = false;
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
                        if (Context.CursorVM.IsVisible) cursorCircle.Show();
                        calWindow.UpdateVisualState(msg.State, msg.ProgressPercent);
                        calWindow.Show();
                        break;
                    default:
                        // CalibrationState.Idle / CalibrationState.Cancelled — calibration is over
                        // (not in progress), so toolbar dwell resumes — but only if the toolbar
                        // hasn't since auto-hidden (same state-aware path as the Active/Failed case).
                        Context.GazeEngine.SuppressFixation = false;
                        _toolbar.ReassertVisibility();
                        if (Context.CursorVM.IsVisible) cursorCircle.Show();
                        calWindow.DismissCalibration();
                        break;
                }
            }));
    }

}
