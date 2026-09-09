namespace EyeCtrl.App.Tests.Services.Coordinators;

using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.Services;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// The task-switcher close handler's dwell re-arm gate, driven behaviourally.
///
/// <para>
/// <c>TaskSwitcherDwellPositionTests</c> pins the gate's SYMBOL and <c>ToolbarSlideTests</c> pins
/// <c>IsOnScreen</c>'s truth table, and between them one thing stays invisible: polarity.
/// <c>if (!Toolbar.IsOnScreen)</c> satisfies both while doing the exact opposite — re-arming dwell
/// for a departing band and refusing to re-arm an arriving one. These two tests are the only thing
/// that catches that, which is why they are worth a harness.
/// </para>
///
/// <para>
/// Dwell state is observed the way <c>ToolbarVisibilityAdapterTests</c> observes it — through the
/// real dwell machinery, since <c>ToolbarViewModel.SetActive</c> is non-virtual — via
/// <c>ExposedDwellToolbarViewModel</c>, which that file already declares at namespace scope.
/// </para>
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[Collection("WpfApplication")]
public class TaskSwitcherReArmTests
{
    [StaFact]
    [Trait("Category", "UI")]
    public void SwitcherClosing_DuringSlideIn_ReArmsToolbarDwell()
    {
        using var h = new Harness();

        // The band is arriving: visible, staying, dwell armed by design.
        h.Toolbar.SlideOutThenHide(() => { });
        h.Toolbar.CompleteSlideForTest();
        h.Toolbar.ShowThenSlideIn();
        Check.That(h.Toolbar.IsOnScreen).IsTrue();

        // The switcher disarmed dwell when it opened; closing it must repair that. Nothing else
        // will — the slide-in's completion no longer touches dwell state.
        h.ToolbarVm.SetActive(false);
        h.CloseSwitcher();

        Check.That(h.DwellAccumulates()).IsTrue();
    }

    [StaFact]
    [Trait("Category", "UI")]
    public void SwitcherClosing_DuringSlideOut_DoesNotReArmToolbarDwell()
    {
        using var h = new Harness();

        // The band is leaving and the window is about to hide.
        h.Toolbar.SlideOutThenHide(() => { });
        Check.That(h.Toolbar.IsOnScreen).IsFalse();

        h.ToolbarVm.SetActive(false);
        h.CloseSwitcher();

        Check.That(h.DwellAccumulates()).IsFalse();
    }

    /// <summary>
    /// A docked, shown toolbar plus a coordinator initialised over a real context. The switcher VM
    /// is set visible BEFORE Initialize so no show-branch ever runs — this harness only exercises
    /// the close branch, which is the one carrying the gate.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly OverlayVmBindingCoordinator _coordinator;
        private readonly TaskSwitcherViewModel _switcherVm;
        private readonly TaskSwitcherWindow _switcherWindow;

        internal ToolbarWindow Toolbar { get; }
        internal ExposedDwellToolbarViewModel ToolbarVm { get; }

        internal Harness()
        {
            WpfApplicationGate.EnsureApplicationWithTokens();

            var settings = new EyeCtrlSettings();
            var messenger = new WeakReferenceMessenger();

            ToolbarVm = new ExposedDwellToolbarViewModel(settings, messenger);
            Toolbar = new ToolbarWindow();
            Toolbar.Initialize(ToolbarTestHelpers.ButtonSize, settings.Dwell.ToolbarButtonDwellMs);
            Toolbar.DataContext = ToolbarVm;
            Toolbar.BindToViewModel();
            Toolbar.Show();
            Toolbar.SetMode(isDocked: true, edge: "Top");

            _switcherWindow = new TaskSwitcherWindow();
            _switcherVm = new TaskSwitcherViewModel(windowManager: null!, settings, messenger);

            // Visible before Initialize: the coordinator subscribes inside Initialize, so this
            // assignment raises nothing and the show branch never runs.
            _switcherVm.IsVisible = true;

            _coordinator = new OverlayVmBindingCoordinator(new ModalInteractionState());
            _coordinator.Initialize(TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
            {
                CursorVM = new CursorCircleViewModel(settings, messenger),
                ActivationVM = new ActivationButtonViewModel(settings, messenger),
                ManualSelectionVM = new ManualSelectionOverlayViewModel(settings, messenger),
                ManualSelectionIndicatorVM = new ManualSelectionGazeIndicatorViewModel(messenger),
                ActionMenuVM = new ActionMenuViewModel(settings, messenger),
                BackdropVM = new GlassBackdropViewModel(settings, messenger),
                ToolbarVM = ToolbarVm,
                TaskSwitcherVM = _switcherVm,
                OverlayManager = new FakeOverlays(Toolbar, _switcherWindow),
                Dispatcher = Dispatcher.CurrentDispatcher,
                Settings = settings,
            });

            ToolbarVm.DwellDetector.AddTarget(new DwellTarget(
                "Close", new PhysicalPoint(100, 100), radius: 50, dwellTimeMs: 1000));
        }

        /// <summary>Flips the switcher to hidden and drains the handler's BeginInvoke.</summary>
        internal void CloseSwitcher()
        {
            _switcherVm.IsVisible = false;
            Pump();
        }

        /// <summary>
        /// Whether toolbar dwell is live: two gaze updates on a positioned target — the first
        /// records entry and emits nothing, the second accumulates. Routed through Receive, the
        /// view-model's own dwell gate, so progress reaching the target proves SetActive(true) ran.
        /// </summary>
        internal bool DwellAccumulates()
        {
            ToolbarVm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(100, 100), 0));
            ToolbarVm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(100, 100), 500_000));
            return ToolbarVm.LastProgress > 0;
        }

        private static void Pump()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new DispatcherOperationCallback(_ => { frame.Continue = false; return null; }),
                null);
            Dispatcher.PushFrame(frame);
        }

        public void Dispose()
        {
            _coordinator.Dispose();
            _switcherVm.Dispose();
            _switcherWindow.Close();
            Toolbar.Close();
        }
    }

    /// <summary>
    /// Only the three members the close branch touches. GlassBackdrop returns null deliberately:
    /// SetupBackdrop merely assigns it to a field and every consumer null-guards, so no backdrop
    /// window need exist. Every other member throws, so an accidental reach fails loudly rather
    /// than returning null.
    /// </summary>
    private sealed class FakeOverlays : IOverlayManager
    {
        private readonly ToolbarWindow _toolbar;
        private readonly TaskSwitcherWindow _switcher;

        internal FakeOverlays(ToolbarWindow toolbar, TaskSwitcherWindow switcher)
        {
            _toolbar = toolbar;
            _switcher = switcher;
        }

        public IToolbarOverlay Toolbar => _toolbar;
        public ITaskSwitcherOverlay TaskSwitcher => _switcher;
        public IGlassBackdropOverlay GlassBackdrop => null!;

        public IPauseIndicatorOverlay PauseIndicator => throw new NotImplementedException();
        public ICursorCircleOverlay CursorCircle => throw new NotImplementedException();
        public IActivationButtonOverlay ActivationButton => throw new NotImplementedException();
        public IManualSelectionOverlay ManualSelectionOverlay => throw new NotImplementedException();
        public IActionMenuOverlay ActionMenu => throw new NotImplementedException();
        public KeyboardWindow Keyboard => throw new NotImplementedException();
        public IManualSelectionIndicatorOverlay ManualSelectionIndicator => throw new NotImplementedException();
        public ICalibrationOverlay Calibration => throw new NotImplementedException();
        public ITargetHighlightOverlay TargetHighlight => throw new NotImplementedException();
        public IEdgeButtonBar EdgeButtonBar => throw new NotImplementedException();
        public ITrackStatusOverlay TrackStatus => throw new NotImplementedException();
        public IQuickActionsGridOverlay QuickActionsGrid => throw new NotImplementedException();

        public void Initialize(OverlayViewModels viewModels, EyeCtrlSettings settings,
            EyeCtrl.Core.Enums.GazeProviderType providerType) => throw new NotImplementedException();
    }
}
