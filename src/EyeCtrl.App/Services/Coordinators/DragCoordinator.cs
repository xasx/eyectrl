namespace EyeCtrl.App.Services.Coordinators;

using System.ComponentModel;
using EyeCtrl.App.Overlays;
using EyeCtrl.Core.Localization;
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
    /// <para>
    /// An int (0/1) consumed with <see cref="Interlocked"/>, not a bool: the fatal-exit release
    /// runs on the FAULTING thread (AppDomain.UnhandledException can fire on any thread), so a
    /// plain check-then-act could double-send MouseUp against a simultaneous legitimate release —
    /// or, with no memory barrier, never observe the press at all. Exchange makes the release
    /// exactly-once across every path, cross-thread.
    /// </para>
    /// </summary>
    private int _osButtonHeld;
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

    /// <summary>
    /// Builds the drag-cancel button (above the anchor on the right edge). Static and internal so
    /// the symbol/label sweep test can reach the definition without a live coordinator — the same
    /// pattern as <see cref="ScrollModeController.CreateScrollStopButtonDefinition"/>.
    /// </summary>
    internal static EdgeButtonDefinition CreateCancelButtonDefinition(int dwellMs)
        => new(
            "Drag_cancel", "\u2715", dwellMs,
            m => m.Send(new DragCancelMessage()),
            VerticalOffset: DpiHelper.LogicalToPhysicalY(30),
            AccessibleName: StringTable.Current.T("overlays.edge.cancelDrag"));

    /// <summary>
    /// Builds the post-drop back button (below the anchor). Static and internal for the same
    /// reason as <see cref="CreateCancelButtonDefinition"/>.
    /// </summary>
    internal static EdgeButtonDefinition CreateBackButtonDefinition(int dwellMs)
        => new(
            "Drag_back", "\u21A9", dwellMs,
            m => m.Send(new DragDismissManualSelectionMessage()),
            VerticalOffset: -DpiHelper.LogicalToPhysicalY(30),
            AccessibleName: StringTable.Current.T("overlays.edge.back"));

    internal void OnDragStarted(PhysicalPoint grabOrigin)
    {
        if (Context is null) return;

        _dragGrabOrigin = grabOrigin;

        // F2 (clicking investigation §4, pre-existing): InteractionManager.OnActionSelected invokes
        // HideActionMenu SYNCHRONOUSLY and then raises DragStarted, but the hide it invoked has only
        // been POSTED — so the menu window AND the backdrop are both still on screen at the grab
        // point, and the press below would land on them. Order:
        //   1. the menu VM goes false, the same property change the posted hide would deliver, so its
        //      coordinator listener hides the menu window and posts the backdrop's deferred hide;
        //      the later, queued hide sets an already-false property and raises nothing (idempotent);
        //   2. the backdrop's pending hide is completed synchronously (F1) before the button goes
        //      down. A listenerless context (focused coordinator tests) leaves nothing pending, so
        //      the completion is a no-op there — as it is when no menu/backdrop was up at all.
        if (Context.ActionMenuVM is { } menuVm)
            menuVm.IsVisible = false;
        var backdrop = Context.OverlayManager?.GlassBackdrop;
        backdrop?.CompleteHideNow();

        Log.Information("Drag: MouseDown at ({X:F0}, {Y:F0}) [BackdropVisible={BackdropVisible}]",
            grabOrigin.X, grabOrigin.Y, backdrop?.IsVisible ?? false);

        Context.InputSimulator.MouseDown(grabOrigin);
        Volatile.Write(ref _osButtonHeld, 1);

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
            var cancelDef = CreateCancelButtonDefinition(Context.Settings.Dwell.DragRightEdgeButtonDwellMs);
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
        if (Context is null) return;
        // Claim with Exchange (not read-then-write): exactly one caller across ALL paths —
        // dispatcher, dispose, and the cross-thread fatal-exit release — sends the MouseUp.
        if (Interlocked.Exchange(ref _osButtonHeld, 0) == 0) return;
        try
        {
            Context.InputSimulator.MouseUp(at);
        }
        catch
        {
            // A FAILED send leaves the release outstanding, not done: restore the claim so the
            // next release path (OnDispose is the retry) still fires — the guarantee the
            // dispose/cancel/drop ordering tests pin.
            // Residual window (round-2 nit 3, documented): between the Exchange above and this
            // restore, the flag reads 0 — a concurrent fatal-exit release landing there returns
            // having done nothing, and the process dies with the button held. Reachable only if
            // MouseUp THROWS: the shipped Win32InputSimulator cannot (SendRaw logs shortfalls
            // instead of throwing); the only thrower is the tests' ThrowOnFirstReleaseSimulator.
            Volatile.Write(ref _osButtonHeld, 1);
            throw;
        }
    }

    /// <summary>
    /// Last-gasp release for fatal-exception paths: sends the matching MouseUp for a held drag
    /// button and NOTHING else — no overlay work, no interaction-state transitions, either of
    /// which may be mid-tear when the process is dying. A SendInput button-down is OS-global
    /// state that outlives the process: dying with the button held leaves the successor in
    /// FreeLook, where the user's gaze becomes a drag they cannot stop. Safe from any thread
    /// (AppDomain.UnhandledException fires on the faulting thread): the Interlocked flag makes
    /// the release exactly-once against every other release path.
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
                    var backDef = CreateBackButtonDefinition(Context.Settings.Dwell.DragRightEdgeButtonDwellMs);
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
