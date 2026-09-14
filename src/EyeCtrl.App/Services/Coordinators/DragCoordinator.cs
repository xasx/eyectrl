namespace EyeCtrl.App.Services.Coordinators;

using System.ComponentModel;
using EyeCtrl.App.Overlays;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;

/// <summary>
/// Owns the drag surface of <see cref="EventCoordinator"/>: the <c>DragStarted</c> hook that presses
/// the mouse and raises the trail overlay, the drop/cancel message registrations, the drag-trail VM
/// binding (including the right-edge cancel/back buttons), and the global ESC-hatch subscription
/// that cancels an in-flight drag (issue #141). Extracted from <c>EventCoordinator</c> as part of
/// the sub-coordinator decomposition (cluster 4).
/// <para>
/// <b>The invariant this class owes the user: every <c>MouseDown</c> it issues is matched by a
/// <c>MouseUp</c>, on every exit path.</b> A <c>SendInput</c> button-down is global OS state that
/// outlives this process, so a drag left un-released does not end when the app does — the successor
/// starts in <c>FreeLook</c>, where the gaze engine moves the cursor, and the user's gaze becomes a
/// continuous drag they cannot stop. Worse, the instinctive response to a stuck drag is tray Exit or
/// Settings → Restart, which is exactly the path that cements it. Four paths therefore release:
/// drop, cancel, a setup failure between the press and the cancel button existing, and dispose.
/// </para>
/// <para>
/// The drag-entry methods are <see langword="internal"/> so tests drive them directly, following
/// <see cref="QuickActionDispatcher"/>'s precedent (no reflection).
/// </para>
/// </summary>
internal sealed class DragCoordinator : CoordinatorBase
{
    private const double RightEdgeInsetLogical = 30;

    private DragTrailOverlay? _dragTrailOverlay;
    private PhysicalPoint _dragGrabOrigin;

    /// <summary>
    /// True between issuing <c>MouseDown</c> and its matching <c>MouseUp</c>. This coordinator's own
    /// record of what it did to the OS, deliberately NOT derived from
    /// <c>InteractionManager.CurrentState</c>: during teardown the state machine may already have
    /// been reset or be mid-transition, and a release that consults it would then skip the release
    /// precisely when it matters. What must be undone is the SendInput call, so the flag tracks the
    /// SendInput call.
    /// </summary>
    private bool _buttonHeld;
    protected override void OnInitialize()
    {
        var context = Context!;
        context.InteractionManager.DragStarted += OnDragStarted;
        context.DragTrailVM.PropertyChanged += OnDragTrailVMPropertyChanged;
        context.Messenger.Register<DragCoordinator, DragDropMessage>(this, (_, m) =>
            context.Dispatcher.BeginInvoke(() => OnDragDrop(m)));
        context.Messenger.Register<DragCoordinator, DragCancelMessage>(this, (_, _) =>
            context.Dispatcher.BeginInvoke(OnDragCancel));

        // DragDismissManualSelectionMessage is intentionally NOT registered here. Only
        // DragTrailViewModel reacts to it (transitions DragSubState back to FreeDrag). The old
        // EventCoordinator handler invoked a defensive ManualSelection overlay hide inside
        // ScrollModeController, which was a no-op because Dragging and ManualSelectionActive are
        // mutually exclusive InteractionStates — so consuming the message here would discard it
        // without effect. (The Drag_back button at OnDragTrailVMPropertyChanged still sends it.)
        context.EscapeHatch.EscapePressed += OnEscapePressed;
    }

    /// <summary>
    /// Pool thread (global hatch, issue #141): ESC pressed anywhere in the system cancels the
    /// drag. The ComponentDispatcher filter this replaced could only see keystrokes aimed at
    /// EyeCtrl's own windows — and drag exists to act on other apps, which hold the foreground.
    /// The Send is marshaled to the UI thread BEFORE fan-out (review PR #142): Messenger
    /// delivers synchronously to every recipient, and <see cref="DragTrailViewModel"/>'s
    /// <c>DragCancelMessage</c> handler does not marshal — sending from the pool thread would
    /// run its dwell teardown off the UI thread. The state guard lives INSIDE the dispatched
    /// action: <see cref="OnDragCancel"/> sends an unconditional MouseUp, and a stray one could
    /// release a caregiver's physically held button if the drag ended between the guard and
    /// the send.
    /// </summary>
    private void OnEscapePressed()
    {
        var context = Context;
        if (context is null) return;
        context.Dispatcher.BeginInvoke(() =>
        {
            if (context.InteractionManager.CurrentState == InteractionState.Dragging)
                context.Messenger.Send(new DragCancelMessage());
        });
    }

    internal void OnDragStarted(PhysicalPoint grabOrigin)
    {
        if (Context is null) return;

        _dragGrabOrigin = grabOrigin;
        Context.InputSimulator.MouseDown(grabOrigin);
        _buttonHeld = true;

        // Everything from here to the cancel button being on screen is the window in which the
        // button is down and the user has no way to let go of it. A throw in it used to escape
        // into the dispatcher, where the app's crash-to-die policy killed the process with the
        // button still pressed \u2014 and the press survives the process. This is not a hypothetical
        // range: ScreenGeometry.RightEdgeAnchor does DPI arithmetic that has thrown on small
        // screens before, DragTrailOverlay is a WPF window whose construction can fail, and
        // ShowButtons touches another overlay. Whatever fails, the button must come back up.
        try
        {
            _dragTrailOverlay = new DragTrailOverlay();
            _dragTrailOverlay.Configure(Context.Settings.Drag.TrailColor, Context.Settings.Drag.TrailOpacity);
            _dragTrailOverlay.Show();

            var anchor = ScreenGeometry.RightEdgeAnchor(RightEdgeInsetLogical, UIConstants.DragButtonRadius);
            var cancelDef = new EdgeButtonDefinition(
                "Drag_cancel", "\u2715", Context.Settings.Dwell.DragRightEdgeButtonDwellMs,
                m => m.Send(new DragCancelMessage()),
                VerticalOffset: DpiHelper.LogicalToPhysicalY(30));
            Context.OverlayManager.EdgeButtonBar.ShowButtons(new[] { cancelDef }, anchor);

            Context.Messenger.Send(new StartDragTrailMessage(grabOrigin));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Drag setup failed after the button was pressed; releasing it and aborting the drag.");
            AbortDragAfterSetupFailure();
        }
    }

    /// <summary>
    /// Unwinds a drag that never became usable. Deliberately swallowing: this runs because
    /// something already failed, and letting the recovery throw would return the caller to the
    /// crash path with the button state it was trying to fix. Release comes first \u2014 every later
    /// step is cosmetic by comparison.
    /// </summary>
    private void AbortDragAfterSetupFailure()
    {
        ReleaseHeldButton(_dragGrabOrigin);

        try { Context?.OverlayManager.EdgeButtonBar.Hide(); }
        catch (Exception ex) { Log.Error(ex, "Failed to hide the edge button bar while aborting a drag."); }

        try { CloseDragOverlay(); }
        catch (Exception ex) { Log.Error(ex, "Failed to close the drag trail overlay while aborting a drag."); }

        // Back to FreeLook, or the user is stuck in a Dragging state with nothing to drag and
        // TogglePause deliberately blocked.
        try { Context?.InteractionManager.CompleteDrag(); }
        catch (Exception ex) { Log.Error(ex, "Failed to reset interaction state while aborting a drag."); }
    }

    /// <summary>
    /// Releases the button if this coordinator is holding it, and only then clears the flag.
    /// Idempotent, so a drop followed by a dispose releases exactly once and the down/up counts
    /// stay balanced. The flag is cleared AFTER the call so a throwing <c>MouseUp</c> leaves the
    /// release outstanding for <see cref="OnDispose"/> to retry rather than marking it done.
    /// </summary>
    private void ReleaseHeldButton(PhysicalPoint at)
    {
        if (!_buttonHeld || Context is null) return;
        Context.InputSimulator.MouseUp(at);
        _buttonHeld = false;
    }

    /// <summary>
    /// Last-gasp release for fatal-exception paths: sends the matching MouseUp for a held drag
    /// button and NOTHING else — no overlay work, no interaction-state transitions, either of
    /// which may be mid-tear when the process is dying. A SendInput button-down is OS-global
    /// state that outlives the process: dying with the button held leaves the successor in
    /// FreeLook, where the user's gaze becomes a drag they cannot stop. Callable from any
    /// thread (a single Win32 send); idempotent via <see cref="_buttonHeld"/>.
    /// </summary>
    internal void ReleaseHeldButtonForFatalExit() => ReleaseHeldButton(_dragGrabOrigin);

    // Release FIRST on both paths, before any overlay work.
    //
    // These used to hide the edge-button bar first, which put two overlay dereferences ahead of
    // the only step that touches OS-global state. A throw in EdgeButtonBar.Hide() — the bar is
    // reached through IOverlayManager, and its window can be closed or absent — skipped the
    // release entirely and escaped into the dispatcher, so the crash-to-die policy killed the
    // process with the button still pressed. The visual teardown is cosmetic and belongs to a
    // process that may be about to die; the release is not.
    internal void OnDragDrop(DragDropMessage msg)
    {
        ReleaseHeldButton(msg.DropPoint);
        Context?.OverlayManager.EdgeButtonBar.Hide();
        Context?.InteractionManager.CompleteDrag();
        CloseDragOverlay();
    }

    internal void OnDragCancel()
    {
        ReleaseHeldButton(_dragGrabOrigin);
        Context?.OverlayManager.EdgeButtonBar.Hide();
        Context?.InteractionManager.CompleteDrag();
        CloseDragOverlay();
    }

    private void CloseDragOverlay()
    {
        _dragTrailOverlay?.Close();
        _dragTrailOverlay = null;
    }

    private void OnDragTrailVMPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Context is null || _dragTrailOverlay == null) return;
        var vm = Context.DragTrailVM;

        switch (e.PropertyName)
        {
            case nameof(DragTrailViewModel.CursorPosition):
            case nameof(DragTrailViewModel.SettleProgress):
            case nameof(DragTrailViewModel.ReleaseButtonCenter):
                _dragTrailOverlay.Update(
                    vm.GrabOrigin, vm.CursorPosition, vm.SettleProgress, vm.ReleaseButtonCenter);
                break;
            case nameof(DragTrailViewModel.ShowBack):
                var bar = Context.OverlayManager.EdgeButtonBar;
                if (Context.DragTrailVM.ShowBack)
                {
                    var backDef = new EdgeButtonDefinition(
                        "Drag_back", "\u21A9", Context.Settings.Dwell.DragRightEdgeButtonDwellMs,
                        m => m.Send(new DragDismissManualSelectionMessage()),
                        VerticalOffset: -DpiHelper.LogicalToPhysicalY(30));
                    bar.AddButton(backDef);
                }
                else
                {
                    bar.RemoveButton("Drag_back");
                }
                _dragTrailOverlay.Update(
                    vm.GrabOrigin, vm.CursorPosition, vm.SettleProgress, vm.ReleaseButtonCenter);
                break;
        }
    }

    protected override void OnDispose()
    {
        if (Context is null) return;

        // FIRST, and guarded. Dispose runs on tray Exit, Settings → Restart, and Windows logoff —
        // all deliberate, all previously leaving the button pressed because nothing on this path
        // consulted the drag at all. The successor process then starts in FreeLook with the button
        // still down and the user's gaze becomes an unstoppable drag. Everything else in this
        // method is bookkeeping inside a process that is going away; this is the only step whose
        // omission outlives it, so it must not be skipped by an earlier line throwing.
        try
        {
            ReleaseHeldButton(_dragGrabOrigin);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to release the drag button during dispose; it may still be held.");
        }

        Context.InteractionManager.DragStarted -= OnDragStarted;
        Context.DragTrailVM.PropertyChanged -= OnDragTrailVMPropertyChanged;
        Context.EscapeHatch.EscapePressed -= OnEscapePressed;
        CloseDragOverlay();
    }
}
