namespace EyeCtrl.App.Tests.Services.Coordinators;

using System.Collections.Generic;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.App.Tests.Services;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// Ticket 5 (recapture): the "↻" edge button used to re-derive Center from FixationPoint and
/// resend StartManualSelectionMessage, which teleported the magnifier away from wherever
/// ViewPosition placed it on the first press (Center actually changing) and did NOTHING on every
/// press after (Center already equal to FixationPoint, so [ObservableProperty] swallowed the
/// write). It also called HideBackdrop() first, which never restored — silently reducing the
/// view's three exits to two.
///
/// The fix stops resending StartManualSelectionMessage for recapture and instead bumps
/// ManualSelectionOverlayViewModel.RecaptureVersion (the RingVersion idiom), which
/// OverlayVmBindingCoordinator treats as its own "refresh the screenshot in place" signal,
/// independent of whether Center/Radius/Magnification changed. HideBackdrop() is no longer
/// called at all.
///
/// This drives the REAL OverlayMessageCoordinator + OverlayVmBindingCoordinator pair (both
/// Initialize()d, unlike the ESC-hatch tests which only initialize the message coordinator) so the
/// full message -> VM property -> window-seam pipeline is exercised, through recording fakes for
/// the magnifier and the backdrop.
/// </summary>
public class ManualSelectionRecaptureTests
{
    [StaFact]
    public void Recapture_RefreshesCaptureInPlace_TwiceInARow_WithoutTouchingBackdrop()
    {
        // Deliberately does NOT call DpiHelper.Initialize: this test only compares PhysicalPoint
        // centers passed straight through the fakes, never a DIP<->physical conversion, so it
        // needs no particular scale — and DpiHelperWriterIsolationTests requires every writer to
        // live in a DisableParallelization collection, which a plain reader doesn't need.
        var dispatcher = Dispatcher.CurrentDispatcher;
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        var im = new InteractionManager();

        var manualSelectionVm = new ManualSelectionOverlayViewModel(settings, messenger);
        var overlay = new RecordingManualSelectionOverlay();
        var backdrop = new RecordingGlassBackdrop();
        var edgeBar = new TestFakes.FakeEdgeButtonBar();

        var modal = new ModalInteractionState();
        var messageCoordinator = new OverlayMessageCoordinator(
            new ToolbarLifecycleCoordinator(modal), new OverlayVmBindingCoordinator(modal));
        var vmBindingCoordinator = new OverlayVmBindingCoordinator(modal);

        var ctx = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            InteractionManager = im,
            Messenger = messenger,
            Dispatcher = dispatcher,
            Settings = settings,
            ManualSelectionVM = manualSelectionVm,
            ManualSelectionIndicatorVM = new ManualSelectionGazeIndicatorViewModel(messenger),
            CursorVM = new CursorCircleViewModel(settings, messenger),
            ActivationVM = new ActivationButtonViewModel(settings, messenger),
            ActionMenuVM = new ActionMenuViewModel(settings, messenger),
            ToolbarVM = new ToolbarViewModel(settings, messenger),
            BackdropVM = new GlassBackdropViewModel(settings, messenger),
            TaskSwitcherVM = new TaskSwitcherViewModel(windowManager: null!, settings, messenger),
            OverlayManager = new FakeOverlays(overlay, backdrop, edgeBar),
        };

        messageCoordinator.Initialize(ctx);
        vmBindingCoordinator.Initialize(ctx);

        var sawHiddenTransition = false;
        manualSelectionVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ManualSelectionOverlayViewModel.IsVisible) && !manualSelectionVm.IsVisible)
                sawHiddenTransition = true;
        };

        im.SelectionMode = SelectionMode.ManualSelection;
        var fixation = new PhysicalPoint(300, 400);
        im.OnFixationStarted(fixation);
        im.OnActivationButtonDwelled();
        Check.That(im.CurrentState).IsEqualTo(InteractionState.ManualSelectionActive);

        // First entry: a real StartManualSelectionMessage, exactly as GazeEngineAdapter sends it.
        // Center (960, 540) stands in for wherever ViewPosition placed the magnifier (e.g. screen
        // centre under AtCenter) — deliberately NOT equal to the fixation point, so a reintroduced
        // teleport toward FixationPoint is observable.
        var placedCenter = new PhysicalPoint(960, 540);
        messenger.Send(new StartManualSelectionMessage(placedCenter, 150, 2.0, fixation));
        Pump(dispatcher);

        Check.That(manualSelectionVm.IsVisible).IsTrue();
        Check.That(manualSelectionVm.Center).IsEqualTo(placedCenter);
        Check.That(backdrop.ShowCalls).IsEqualTo(1);

        // The initial Show() is not what this test pins — only the two recaptures that follow.
        overlay.Calls.Clear();
        var showCallsAfterSetup = overlay.ShowCalls;

        // Trap C: dwell the recapture button TWICE, from the already-visible precondition
        // established above, and assert on both — the historic bug only reproduced on the SECOND
        // press (the first one "worked" by accident, via the teleport).
        messenger.Send(new ManualSelectionRecaptureMessage());
        Pump(dispatcher);
        messenger.Send(new ManualSelectionRecaptureMessage());
        Pump(dispatcher);

        // Trap A: a re-capture actually happened, both times — not merely "Center is unchanged".
        Check.That(overlay.UpdateCenterCalls).IsEqualTo(2);
        // Recapture goes through UpdateCenter, not Show — no new Show() calls since setup.
        Check.That(overlay.ShowCalls).IsEqualTo(showCallsAfterSetup);

        // Geometry preserved: neither recapture moved the view toward the fixation point.
        foreach (var call in overlay.Calls)
            Check.That(call.Center).IsEqualTo(placedCenter);

        // Trap B: the coordinator's hide branch never ran on either recapture — no transient
        // IsVisible=false, and the overlay/backdrop were never torn down and rebuilt.
        Check.That(sawHiddenTransition).IsFalse();
        Check.That(manualSelectionVm.IsVisible).IsTrue();

        // Pins the deleted HideBackdrop() call: the backdrop is still up after both recaptures.
        Check.That(backdrop.HideCalls).IsEqualTo(0);
        Check.That(backdrop.ShowCalls).IsEqualTo(1);
    }

    /// <summary>Drains the dispatcher queue: run everything queued at/above Background priority.</summary>
    private static void Pump(Dispatcher dispatcher) =>
        dispatcher.Invoke(() => { }, DispatcherPriority.Background);

    /// <summary>Records every Show/UpdateCenter call the coordinator makes on the magnifier.</summary>
    private sealed class RecordingManualSelectionOverlay : IManualSelectionOverlay
    {
        public List<(PhysicalPoint Center, int Radius, double Magnification)> Calls { get; } = new();
        public int ShowCalls { get; private set; }
        public int UpdateCenterCalls { get; private set; }

        public void Show(PhysicalPoint center, int radius, double magnification,
            PhysicalPoint? gazePoint = null, bool recapture = true)
        {
            ShowCalls++;
            Calls.Add((center, radius, magnification));
        }

        public void UpdateCenter(PhysicalPoint center, int radius, double magnification,
            PhysicalPoint? gazePoint = null, bool recapture = true)
        {
            UpdateCenterCalls++;
            Calls.Add((center, radius, magnification));
        }

        public void Hide() { }

        public PhysicalPoint GetRealPhysicalPoint(PhysicalPoint gazeOnScreen) => gazeOnScreen;
    }

    /// <summary>Records Show/Hide so a test can assert the backdrop was never torn down.</summary>
    private sealed class RecordingGlassBackdrop : IGlassBackdropOverlay
    {
        public int ShowCalls { get; private set; }
        public int HideCalls { get; private set; }

        public void Show() => ShowCalls++;
        public void Hide() => HideCalls++;
        public void ApplyBackdrop(BackdropSettings settings) { }
        public void DrawHideButton(PhysicalPoint buttonCenter, double progress = 0) { }
        public void UpdateHideButtonProgress(double progress) { }
    }

    /// <summary>
    /// Only the members the manual-selection show/recapture path touches. Everything else throws,
    /// so a coordinator reaching beyond what this test intends fails loudly (same discipline as
    /// TaskSwitcherReArmTests.FakeOverlays and TestFakes.PartialOverlayManager).
    /// </summary>
    private sealed class FakeOverlays : IOverlayManager
    {
        private readonly IManualSelectionOverlay _manualSelection;
        private readonly IGlassBackdropOverlay _backdrop;
        private readonly IEdgeButtonBar _edgeBar;

        internal FakeOverlays(IManualSelectionOverlay manualSelection, IGlassBackdropOverlay backdrop,
            IEdgeButtonBar edgeBar)
        {
            _manualSelection = manualSelection;
            _backdrop = backdrop;
            _edgeBar = edgeBar;
        }

        public IManualSelectionOverlay ManualSelectionOverlay => _manualSelection;
        public IGlassBackdropOverlay GlassBackdrop => _backdrop;
        public IEdgeButtonBar EdgeButtonBar => _edgeBar;

        public IManualSelectionIndicatorOverlay ManualSelectionIndicator { get; } = new NoOpIndicator();
        public ICursorCircleOverlay CursorCircle { get; } = new NoOpCursorCircle();
        public IActivationButtonOverlay ActivationButton { get; } = new NoOpActivationButton();

        public IModesMenuOverlay ModesMenu => throw new NotImplementedException();
        public IRepeatCursorOverlay RepeatCursor => throw new NotImplementedException();
        public IToolbarOverlay Toolbar => throw new NotImplementedException();
        public IPauseIndicatorOverlay PauseIndicator => throw new NotImplementedException();
        public IActionMenuOverlay ActionMenu => throw new NotImplementedException();
        public KeyboardWindow Keyboard => throw new NotImplementedException();
        public ICalibrationOverlay Calibration => throw new NotImplementedException();
        public ITargetHighlightOverlay TargetHighlight => throw new NotImplementedException();
        public ITrackStatusOverlay TrackStatus => throw new NotImplementedException();
        public ITaskSwitcherOverlay TaskSwitcher => throw new NotImplementedException();
        public IQuickActionsGridOverlay QuickActionsGrid => throw new NotImplementedException();

        public void Initialize(OverlayViewModels viewModels, EyeCtrlSettings settings,
            EyeCtrl.Core.Enums.GazeProviderType providerType) => throw new NotImplementedException();

        private sealed class NoOpIndicator : IManualSelectionIndicatorOverlay
        {
            public bool IsVisible => false;
            public void Show() { }
            public void Hide() { }
            public void UpdatePosition(double screenX, double screenY) { }
            public void UpdateOuterDiameter(double diameter) { }
            public void AnimateReset(double targetDiameter, int durationMs) { }
            public void RaiseToTop() { }
        }

        private sealed class NoOpCursorCircle : ICursorCircleOverlay
        {
            public void Show() { }
            public void Hide() { }
            public void UpdatePosition(PhysicalPoint point) { }
            public void UpdateAppearance(CursorSettings settings) { }
        }

        private sealed class NoOpActivationButton : IActivationButtonOverlay
        {
            public void Show() { }
            public void ShowAt(PhysicalPoint position, double offset) { }
            public void Hide() { }
            public void HideImmediate() { }
            public void UpdateDwellProgress(double progress) { }
        }
    }
}
