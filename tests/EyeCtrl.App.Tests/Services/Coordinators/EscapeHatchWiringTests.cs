namespace EyeCtrl.App.Tests.Services.Coordinators;

using CommunityToolkit.Mvvm.Messaging;
using System.Windows.Threading;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// Wiring tests for the global ESC hatch subscriptions (review PR #142: "the wiring is
/// untested — FakeEscapeHatch.Press() was added and never called"). These drive the REAL
/// coordinators through the fake hatch and a real <see cref="Dispatcher"/>, so the
/// hatch→handler→guard→(no)message path is exercised end-to-end.
///
/// The positive paths (ESC mid-drag / mid-repeat) are NOT here on purpose: entering those
/// states through the coordinator constructs real overlay windows (DragTrailOverlay,
/// EdgeButtonBarWindow — concrete WPF types), which is manual-smoke territory. The guard
/// paths below still prove the subscription exists, fires, marshals, and decides.
/// </summary>
public class EscapeHatchWiringTests
{
    [StaFact]
    public void Drag_EscapeOutsideDragging_SendsNoCancel_AfterPumping()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var messenger = new WeakReferenceMessenger();
        var im = new InteractionManager();
        var dragVm = new DragTrailViewModel(new EyeCtrlSettings(), messenger);
        var hatch = new TestFakes.FakeEscapeHatch();
        var ctx = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            InteractionManager = im,
            Messenger = messenger,
            Dispatcher = dispatcher,
            DragTrailVM = dragVm,
            EscapeHatch = hatch,
        };
        using var coordinator = new DragCoordinator();
        coordinator.Initialize(ctx);

        var cancelled = false;
        messenger.Register<object, DragCancelMessage>(this, (_, _) => cancelled = true);

        hatch.Press();
        Pump(dispatcher);

        Check.That(cancelled).IsFalse();
    }

    [StaFact]
    public void Repeat_EscapeOutsideRepeatMode_SendsNoExit_AfterPumping()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var messenger = new WeakReferenceMessenger();
        var im = new InteractionManager();
        var hatch = new TestFakes.FakeEscapeHatch();
        var ctx = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            InteractionManager = im,
            Messenger = messenger,
            Dispatcher = dispatcher,
            EscapeHatch = hatch,
        };
        using var coordinator = new RepeatModeController();
        coordinator.Initialize(ctx);

        var exitRequested = false;
        messenger.Register<object, ExitRepeatModeMessage>(this, (_, _) => exitRequested = true);

        hatch.Press();
        Pump(dispatcher);

        Check.That(exitRequested).IsFalse();
    }

    // ── Manual selection (ticket: ESC exits manual selection) ──
    //
    // The subscription lives in OverlayMessageCoordinator, so these build that coordinator
    // (its ctor needs the toolbar + VM-binding siblings; OnInitialize also dereferences the
    // two manual-selection overlays, hence the partial overlay manager). The positive tests
    // drive the REAL InteractionManager into the manual-selection states and assert the
    // exit the state machine records — the only signal that proves the hatch handler ran.

    [StaFact]
    public void ManualSelection_EscapeWhileActive_ExitsToFreeLook()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var im = new InteractionManager();
        var hatch = new TestFakes.FakeEscapeHatch();
        using var coordinator = NewOverlayMessageCoordinator();
        coordinator.Initialize(MakeManualSelectionContext(dispatcher, im, hatch));

        im.SelectionMode = SelectionMode.ManualSelection;
        im.OnFixationStarted(new PhysicalPoint(100, 100));
        im.OnActivationButtonDwelled();
        Check.That(im.CurrentState).IsEqualTo(InteractionState.ManualSelectionActive);

        var endRaised = false;
        im.EndManualSelection += () => endRaised = true;

        hatch.Press();
        Pump(dispatcher);

        Check.That(endRaised).IsTrue();
        Check.That(im.CurrentState).IsEqualTo(InteractionState.FreeLook);
    }

    [StaFact]
    public void ManualSelection_EscapeFromActionMenu_ExitsAllTheWayToFreeLook()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var im = new InteractionManager();
        var hatch = new TestFakes.FakeEscapeHatch();
        using var coordinator = NewOverlayMessageCoordinator();
        coordinator.Initialize(MakeManualSelectionContext(dispatcher, im, hatch));

        im.SelectionMode = SelectionMode.ManualSelection;
        im.OnFixationStarted(new PhysicalPoint(100, 100));
        im.OnActivationButtonDwelled();
        im.OnManualSelectionDwell(new PhysicalPoint(200, 200));
        Check.That(im.CurrentState).IsEqualTo(InteractionState.ManualSelectionActionMenuVisible);

        var endRaised = false;
        im.EndManualSelection += () => endRaised = true;

        hatch.Press();
        Pump(dispatcher);

        // Deliberately FreeLook, not ManualSelectionActive: ESC is a full exit from the menu,
        // not a step back to the magnifier (which would leave the user in the modal view).
        Check.That(endRaised).IsTrue();
        Check.That(im.CurrentState).IsEqualTo(InteractionState.FreeLook);
    }

    [StaFact]
    public void ManualSelection_EscapeOutsideTheMode_ExitsNothing()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var im = new InteractionManager();
        var hatch = new TestFakes.FakeEscapeHatch();
        using var coordinator = NewOverlayMessageCoordinator();
        coordinator.Initialize(MakeManualSelectionContext(dispatcher, im, hatch));

        var endRaised = false;
        im.EndManualSelection += () => endRaised = true;

        hatch.Press();
        Pump(dispatcher);

        Check.That(endRaised).IsFalse();
        Check.That(im.CurrentState).IsEqualTo(InteractionState.FreeLook);
    }

    // ── Scroll mode (ticket: ESC exits scroll mode — the sharper gap, per the ticket, since
    // gaze-driven scrolling is something a caregiver otherwise cannot stop at all) ──

    [StaFact]
    public void ScrollMode_EscapeWhileActive_ExitsToFreeLook()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var im = new InteractionManager();
        var hatch = new TestFakes.FakeEscapeHatch();
        using var coordinator = NewOverlayMessageCoordinator();
        coordinator.Initialize(MakeManualSelectionContext(dispatcher, im, hatch));

        im.OnFixationStarted(new PhysicalPoint(100, 100));
        im.OnActivationButtonDwelled();
        im.OnActionSelected(InteractionAction.ScrollMode);
        Check.That(im.CurrentState).IsEqualTo(InteractionState.ScrollMode);

        hatch.Press();
        Pump(dispatcher);

        Check.That(im.CurrentState).IsEqualTo(InteractionState.FreeLook);
        Check.That(im.ScrollAnchor).IsNull();
    }

    [StaFact]
    public void ScrollMode_EscapeOutsideTheMode_ExitsNothing()
    {
        // Also stands in for a general "ESC does not overreach" guard: Fixation is a state none
        // of OnEscapePressed's branches should touch, so a regression that widened any one of
        // them (e.g. an unconditional else) would flip this from Fixation.
        var dispatcher = Dispatcher.CurrentDispatcher;
        var im = new InteractionManager();
        var hatch = new TestFakes.FakeEscapeHatch();
        using var coordinator = NewOverlayMessageCoordinator();
        coordinator.Initialize(MakeManualSelectionContext(dispatcher, im, hatch));

        im.OnFixationStarted(new PhysicalPoint(100, 100));
        Check.That(im.CurrentState).IsEqualTo(InteractionState.Fixation);

        hatch.Press();
        Pump(dispatcher);

        Check.That(im.CurrentState).IsEqualTo(InteractionState.Fixation);
    }

    // ── Plain action menu (ticket: ESC should also dismiss the non-manual action menu — the
    // existing else-if widened to cover ActionMenuVisible, since OnActionMenuDismissed already
    // guards both states internally) ──

    [StaFact]
    public void ActionMenu_EscapeWhileVisible_DismissesToFreeLook()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var im = new InteractionManager();
        var hatch = new TestFakes.FakeEscapeHatch();
        using var coordinator = NewOverlayMessageCoordinator();
        coordinator.Initialize(MakeManualSelectionContext(dispatcher, im, hatch));

        im.OnFixationStarted(new PhysicalPoint(100, 100));
        im.OnActivationButtonDwelled();
        Check.That(im.CurrentState).IsEqualTo(InteractionState.ActionMenuVisible);

        hatch.Press();
        Pump(dispatcher);

        Check.That(im.CurrentState).IsEqualTo(InteractionState.FreeLook);
    }

    // ── Dragging / RepeatMode (ticket: decide whether ESC should reach them here too) ──
    //
    // Decision: NO. DragCoordinator and RepeatModeController already subscribe to
    // EscapeHatch.EscapePressed directly (see the Drag_*/Repeat_* tests above) and guard on their
    // own state, so a branch here would double-handle the same key press. These two guard tests
    // pin that decision from OverlayMessageCoordinator's side: pressing ESC while Dragging or
    // RepeatMode, with ONLY OverlayMessageCoordinator subscribed (no DragCoordinator/
    // RepeatModeController in the picture), must leave the state untouched — a regression that
    // widened OnEscapePressed's condition to also catch these states would strand the user, since
    // OnManualSelectionBack/OnActionMenuDismissed/ExitScrollMode all no-op outside their own
    // states and would not actually end either mode.

    [StaFact]
    public void Dragging_EscapeViaOverlayMessageCoordinator_LeavesDraggingUntouched()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var im = new InteractionManager();
        var hatch = new TestFakes.FakeEscapeHatch();
        using var coordinator = NewOverlayMessageCoordinator();
        coordinator.Initialize(MakeManualSelectionContext(dispatcher, im, hatch));

        im.OnFixationStarted(new PhysicalPoint(100, 100));
        im.OnActivationButtonDwelled();
        im.OnActionSelected(InteractionAction.Drag);
        Check.That(im.CurrentState).IsEqualTo(InteractionState.Dragging);

        hatch.Press();
        Pump(dispatcher);

        Check.That(im.CurrentState).IsEqualTo(InteractionState.Dragging);
    }

    [StaFact]
    public void RepeatMode_EscapeViaOverlayMessageCoordinator_LeavesRepeatModeUntouched()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var im = new InteractionManager();
        var hatch = new TestFakes.FakeEscapeHatch();
        using var coordinator = NewOverlayMessageCoordinator();
        coordinator.Initialize(MakeManualSelectionContext(dispatcher, im, hatch));

        im.EnterRepeatMode(InteractionAction.LeftClick);
        Check.That(im.CurrentState).IsEqualTo(InteractionState.RepeatMode);

        hatch.Press();
        Pump(dispatcher);

        Check.That(im.CurrentState).IsEqualTo(InteractionState.RepeatMode);
    }

    private static OverlayMessageCoordinator NewOverlayMessageCoordinator()
    {
        var modal = new ModalInteractionState();
        return new OverlayMessageCoordinator(
            new ToolbarLifecycleCoordinator(modal), new OverlayVmBindingCoordinator(modal));
    }

    private static EventCoordinationContext MakeManualSelectionContext(
        Dispatcher dispatcher, InteractionManager im, TestFakes.FakeEscapeHatch hatch)
    {
        var messenger = new WeakReferenceMessenger();
        return TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            InteractionManager = im,
            Messenger = messenger,
            Dispatcher = dispatcher,
            EscapeHatch = hatch,
            // The ESC branches attribute the magnifier's close reason on this VM; the real class is
            // cheap and needs no window.
            ManualSelectionVM = new ManualSelectionOverlayViewModel(new EyeCtrlSettings(), messenger),
            OverlayManager = TestFakes.MakeOverlayManager(
                manualSelection: new FakeManualSelectionOverlay(),
                manualSelectionIndicator: new FakeManualSelectionIndicator()),
        };
    }

    /// <summary>No-op magnifier: the ESC tests never let a handler reach it.</summary>
    private sealed class FakeManualSelectionOverlay : IManualSelectionOverlay
    {
        public void Show(PhysicalPoint center, int radius, double magnification,
            PhysicalPoint? gazePoint = null, bool recapture = true) { }

        public void UpdateCenter(PhysicalPoint center, int radius, double magnification,
            PhysicalPoint? gazePoint = null, bool recapture = true) { }

        public void Hide() { }

        public PhysicalPoint GetRealPhysicalPoint(PhysicalPoint gazeOnScreen) => gazeOnScreen;

        // Forward-compat stub for ticket 2's IManualSelectionOverlay.EffectiveCaptureCenter: legal
        // on this branch today (extra member), required once that interface change lands. Throw
        // rather than default — (0,0) reads as a plausible coordinate, and this repo has already
        // been bitten by a real value that silently looked like zero.
        public PhysicalPoint EffectiveCaptureCenter =>
            throw new NotImplementedException("ESC wiring tests do not exercise the capture centre");
    }

    /// <summary>No-op gaze dot.</summary>
    private sealed class FakeManualSelectionIndicator : IManualSelectionIndicatorOverlay
    {
        public bool IsVisible => false;
        public void Show() { }
        public void Hide() { }
        public void UpdatePosition(double screenX, double screenY) { }
        public void UpdateOuterDiameter(double diameter) { }
        public void AnimateReset(double targetDiameter, int durationMs) { }
        public void RaiseToTop() { }
        public void UpdateAnchor(double screenX, double screenY, bool visible) { }
    }

    /// <summary>Drains the dispatcher queue: run everything queued at/above Background priority.</summary>
    private static void Pump(Dispatcher dispatcher) =>
        dispatcher.Invoke(() => { }, DispatcherPriority.Background);
}
