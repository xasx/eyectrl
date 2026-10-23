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
/// Settings → Restart, which is exactly the path that cements it. Five paths therefore release:
/// drop, cancel, the fatal-exit last-gasp release, dispose, and a failure of the post-press trail
/// start. (F3 runs the drag's setup — overlay, anchor, cancel button — before the press, so a
/// failure there sends no press at all. The one step after the press, the message that starts the
/// trail view model, is guarded by its own catch: it drives the VM and its overlay updates, which
/// is exactly the kind of code the old post-press window contained, so a throw in it releases
/// before aborting rather than escaping with the button held.)
/// </para>
/// <para>
/// The drag-entry methods are <see langword="internal"/> so tests drive them directly, following
/// <see cref="QuickActionDispatcher"/>'s precedent (no reflection).
/// </para>
/// </summary>
internal sealed class DragCoordinator : CoordinatorBase
{
    /// <summary>
    /// The anchor the drag's Cancel/Back pair is placed against: the shared right-edge exit
    /// anchor every non-keyboard mode uses, so the out slot is the same absolute place here as
    /// in the action menu, manual selection, repeat and scroll.
    /// </summary>
    internal static PhysicalPoint ExitAnchorPhysical => ScreenGeometry.SharedRightEdgeExitAnchor();

    private IDragTrailOverlay? _dragTrailOverlay;
    private PhysicalPoint _dragGrabOrigin;

    /// <summary>
    /// Builds the trail overlay. The default is production's real window; tests replace it with a
    /// recording fake so a drag can be driven without an HWND on the desktop (see
    /// <see cref="IDragTrailOverlay"/>). A factory rather than an instance so the overlay is still
    /// built per drag, exactly as before.
    /// </summary>
    internal Func<IDragTrailOverlay> CreateTrailOverlay { get; set; } = static () => new DragTrailOverlay();

    /// <summary>
    /// The exception that aborted the most recent drag's setup, or null if its setup completed (or
    /// no drag has been attempted). Cleared at the start of every drag, so it always describes the
    /// LAST attempt.
    /// <para>
    /// It exists because the setup catch used to report only to the process-global Serilog logger,
    /// which a test process leaves unconfigured: when the intermittent CI failure aborted a drag
    /// to FreeLook, the exception that caused it was unrecoverable from the test output. A test
    /// that expects a drag to start asserts this is null FIRST, so the next flake names its cause
    /// instead of failing with only the state it left behind.
    /// </para>
    /// </summary>
    internal Exception? LastSetupFailure { get; private set; }

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
    /// The fixed UPPER "out" slot: one radius-plus-half-gap above the shared right-edge anchor
    /// (negative Y, 60 DIP at the shipped sizes). Cancel is the drag's exit and always lives here.
    /// </summary>
    internal static double CancelButtonVerticalOffsetPhysical =>
        -DpiHelper.LogicalToPhysicalY(UIConstants.EdgeButtonSlotOffset);

    /// <summary>The mirrored lower slot (positive Y), taken by the secondary Back button.</summary>
    internal static double BackButtonVerticalOffsetPhysical =>
        DpiHelper.LogicalToPhysicalY(UIConstants.EdgeButtonSlotOffset);

    /// <summary>
    /// Builds the drag-cancel button (the fixed upper out slot on the right edge, r50 like every
    /// other edge-bar exit). Static and internal so the symbol/label sweep test can reach the
    /// definition without a live coordinator — the same pattern as
    /// <see cref="ScrollModeController.CreateScrollStopButtonDefinition"/>.
    /// </summary>
    internal static EdgeButtonDefinition CreateCancelButtonDefinition(int dwellMs)
        => new(
            "Drag_cancel", "\u2715", dwellMs,
            m => m.Send(new DragCancelMessage()),
            VerticalOffset: CancelButtonVerticalOffsetPhysical,
            RadiusLogical: UIConstants.ButtonRadius,
            AccessibleName: StringTable.Current.T("overlays.edge.cancelDrag"));

    /// <summary>
    /// Builds the post-drop back button (the lower slot, below the anchor). Static and internal for
    /// the same reason as <see cref="CreateCancelButtonDefinition"/>.
    /// </summary>
    internal static EdgeButtonDefinition CreateBackButtonDefinition(int dwellMs)
        => new(
            "Drag_back", "\u21A9", dwellMs,
            m => m.Send(new DragDismissManualSelectionMessage()),
            VerticalOffset: BackButtonVerticalOffsetPhysical,
            RadiusLogical: UIConstants.ButtonRadius,
            AccessibleName: StringTable.Current.T("overlays.edge.back"));

    internal void OnDragStarted(PhysicalPoint grabOrigin)
    {
        if (Context is null) return;

        LastSetupFailure = null;
        _dragGrabOrigin = grabOrigin;

        // F2 (clicking investigation §4, pre-existing): InteractionManager.OnActionSelected invokes
        // HideActionMenu SYNCHRONOUSLY and then raises DragStarted, but the hide it invoked has only
        // been POSTED — so the menu window AND the backdrop are both still on screen at the grab
        // point, and the press below would land on them. Order:
        //   1. the menu VM goes false, the same property change the posted hide would deliver, so its
        //      coordinator listener hides the menu window and posts the backdrop's deferred hide;
        //      the later, queued hide sets an already-false property and raises nothing (idempotent);
        //   2. the backdrop's pending hide is completed synchronously (F1) before anything else.
        //      A listenerless context (focused coordinator tests) leaves nothing pending, so the
        //      completion is a no-op there — as it is when no menu/backdrop was up at all.
        // The overlay manager is read through a NULLABLE local: the drag-release tests run a harness
        // with no OverlayManager on purpose (TestFakes.MakeDragContext — their teardown must throw
        // AFTER the release), and the press must still happen there. A `?.` on
        // Context.OverlayManager itself would make the compiler treat it as maybe-null for the rest
        // of this method (CS8602 at the setup's EdgeButtonBar below).
        if (Context.ActionMenuVM is { } menuVm)
            menuVm.IsVisible = false;
        IOverlayManager? overlays = Context.OverlayManager;
        var backdrop = overlays?.GlassBackdrop;
        backdrop?.CompleteHideNow();

        // F3 (owner D1, "a drag that can't get ready does nothing"): every step that can fail —
        // building, configuring and showing the trail window, the right-edge anchor's DPI
        // arithmetic, building the cancel button, and putting it on the bar — runs BEFORE the
        // press. A failure here therefore never clicks at the grab point: the drag simply does not
        // start, and no OS button state was changed. This replaced the old order, which pressed
        // first and, on failure, released again at the grab origin — a click the user never asked
        // for. The press is now the first thing that touches OS-global state, and only once the
        // user's way out (the cancel button) is already on screen. Everything above the press is
        // window-free after the F2 seam; the ONE step below it is guarded separately.
        try
        {
            _dragTrailOverlay = CreateTrailOverlay();
            _dragTrailOverlay.Configure(Context.Settings.Drag.TrailColor, Context.Settings.Drag.TrailOpacity);
            _dragTrailOverlay.Show();

            // The anchor reserves the SAME r50 the cancel/back buttons now use (they are no longer
            // the old r20 drag-button size), so the pair fits fully on screen like every other
            // edge-bar exit.
            var anchor = ExitAnchorPhysical;
            var cancelDef = CreateCancelButtonDefinition(Context.Settings.Dwell.DragRightEdgeButtonDwellMs);
            Context.OverlayManager.EdgeButtonBar.ShowButtons(
                new[] { cancelDef }, anchor, EdgeBarOwner.Drag);
        }
        catch (Exception ex)
        {
            LastSetupFailure = ex;
            Log.Error(ex, "Drag setup failed before the button was pressed; aborting the drag without a press.");
            AbortDragAfterSetupFailure();
            return;
        }

        Log.Information("Drag: MouseDown at ({X:F0}, {Y:F0}) [BackdropVisible={BackdropVisible}]",
            grabOrigin.X, grabOrigin.Y, backdrop?.IsVisible ?? false);

        Context.InputSimulator.MouseDown(grabOrigin);
        Volatile.Write(ref _osButtonHeld, 1);

        // The trail start is the ONE step that still runs AFTER the press, and it can throw: the
        // Send fans out synchronously into DragTrailViewModel.Receive, whose property setters
        // re-enter OnDragTrailVMPropertyChanged and call Update on the overlay — DPI arithmetic and
        // a WPF window, the same failure kinds the old post-press window contained. Without this
        // guard a throw here escaped OnDragStarted with the button still down (the stuck-button
        // failure this class exists to prevent) and LastSetupFailure stayed null, so F1's
        // diagnostic missed it too. The abort releases FIRST; after the press a press+release is
        // unavoidable, but it must not be a press with no release.
        try
        {
            Context.Messenger.Send(new StartDragTrailMessage(grabOrigin));
        }
        catch (Exception ex)
        {
            LastSetupFailure = ex;
            Log.Error(ex, "Drag trail start failed after the button was pressed; releasing it and aborting the drag.");
            AbortDragAfterSetupFailure();
            ResetDragTrailVmAfterFailedStart();
        }
    }

    /// <summary>
    /// Unwinds a drag that never became usable. Deliberately swallowing: this runs because
    /// something already failed, and letting the recovery throw would return the caller to the
    /// crash path with the button state it was trying to fix. Release comes first — every later
    /// step is cosmetic by comparison, and it is a no-op only when the failure happened before the
    /// press (the F3 setup). When the failure came from the post-press trail start, this release is
    /// the whole point. That is why it stays first regardless of which caller got here.
    /// </summary>
    private void AbortDragAfterSetupFailure()
    {
        ReleaseHeldButton(_dragGrabOrigin);

        try { Context?.OverlayManager.EdgeButtonBar.Hide(EdgeBarOwner.Drag); }
        catch (Exception ex) { Log.Error(ex, "Failed to hide the edge button bar while aborting a drag."); }

        try { CloseDragOverlay(); }
        catch (Exception ex) { Log.Error(ex, "Failed to close the drag trail overlay while aborting a drag."); }

        // Back to FreeLook, or the user is stuck in a Dragging state with nothing to drag and
        // TogglePause deliberately blocked.
        try { Context?.InteractionManager.CompleteDrag(); }
        catch (Exception ex) { Log.Error(ex, "Failed to reset interaction state while aborting a drag."); }
    }

    /// <summary>
    /// Puts the drag-trail view model back to idle after a start that threw part-way through.
    /// <c>StartDragTrailMessage</c> fans out synchronously into <c>DragTrailViewModel.Receive</c>,
    /// which sets its properties one at a time; this coordinator's own PropertyChanged handler calls
    /// the overlay's <c>Update</c> on several of them. A throw part-way through therefore leaves the
    /// VM half-initialised — possibly <c>IsDragActive</c> true with no dwell targets, because
    /// <c>SetupDwellTargets</c> is the last statement. Cancelling it restores the idle state, so the
    /// NEXT drag starts clean.
    /// <para>
    /// Delivered by calling the VM's own <c>Receive</c>, not through the messenger: the coordinator
    /// is itself registered for <see cref="DragCancelMessage"/> as a DEFERRED teardown, and the drag
    /// has already been fully unwound above — re-posting that teardown would only repeat the hide
    /// and close on a later dispatcher turn, after the state has been reset.
    /// </para>
    /// </summary>
    private void ResetDragTrailVmAfterFailedStart()
    {
        try { Context?.DragTrailVM.Receive(new DragCancelMessage()); }
        catch (Exception ex) { Log.Error(ex, "Failed to reset the drag-trail view model after an aborted start."); }
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
        Context?.OverlayManager.EdgeButtonBar.Hide(EdgeBarOwner.Drag);
        Context?.InteractionManager.CompleteDrag();
        CloseDragOverlay();
    }

    internal void OnDragCancel()
    {
        ReleaseHeldButton(_dragGrabOrigin);
        Context?.OverlayManager.EdgeButtonBar.Hide(EdgeBarOwner.Drag);
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
