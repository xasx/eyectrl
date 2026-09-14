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

    private static OverlayMessageCoordinator NewOverlayMessageCoordinator()
    {
        var modal = new ModalInteractionState();
        return new OverlayMessageCoordinator(
            new ToolbarLifecycleCoordinator(modal), new OverlayVmBindingCoordinator(modal));
    }

    private static EventCoordinationContext MakeManualSelectionContext(
        Dispatcher dispatcher, InteractionManager im, TestFakes.FakeEscapeHatch hatch)
        => TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            InteractionManager = im,
            Dispatcher = dispatcher,
            EscapeHatch = hatch,
            OverlayManager = TestFakes.MakeOverlayManager(
                manualSelection: new FakeManualSelectionOverlay(),
                manualSelectionIndicator: new FakeManualSelectionIndicator()),
        };

    /// <summary>No-op magnifier: the ESC tests never let a handler reach it.</summary>
    private sealed class FakeManualSelectionOverlay : IManualSelectionOverlay
    {
        public void Show(PhysicalPoint center, int radius, double magnification,
            PhysicalPoint? gazePoint = null, bool recapture = true) { }

        public void UpdateCenter(PhysicalPoint center, int radius, double magnification,
            PhysicalPoint? gazePoint = null, bool recapture = true) { }

        public void Hide() { }

        public PhysicalPoint GetRealPhysicalPoint(PhysicalPoint gazeOnScreen) => gazeOnScreen;
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
    }

    /// <summary>Drains the dispatcher queue: run everything queued at/above Background priority.</summary>
    private static void Pump(Dispatcher dispatcher) =>
        dispatcher.Invoke(() => { }, DispatcherPriority.Background);
}
