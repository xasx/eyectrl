namespace EyeCtrl.App.Tests.Services.Coordinators;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using Xunit;

/// <summary>
/// End-to-end headless pin on part (d)'s wiring: the indicator view model's anchor properties must
/// reach the real window through <see cref="OverlayVmBindingCoordinator"/>. The service test pins
/// the anchor message, the view-model test pins the properties it sets, and the window test pins
/// where the mark lands — this is the seam between them, and without it the mark could be dropped
/// on the floor with the whole suite green.
///
/// <para>
/// The coordinator only ever touches <c>ManualSelectionIndicator</c> (and assigns
/// <c>GlassBackdrop</c> in <c>SetupBackdrop</c>), so the overlay manager is a single-member fake:
/// the indicator is the real window, the backdrop is null (every consumer null-guards it), and
/// everything else throws. The window needs the application resource dictionary, hence the gate and
/// the STA thread; the DpiHelper write puts this class in the exclusive DpiHelper collection.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class ManualSelectionAnchorWiringTests
{
    [StaFact]
    public void IndicatorVmAnchorProperties_DriveTheRealWindowAnchorMark()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(1.0, 1.0);
        try
        {
            var indicator = new ManualSelectionGazeIndicatorWindow();
            indicator.Configure(outerDiameter: 60, centerDotDiameter: 10,
                strokeThickness: 2, opacity: 0.7, holdRadiusLogical: 72);

            var settings = new EyeCtrlSettings();
            var messenger = new WeakReferenceMessenger();
            var indicatorVm = new ManualSelectionGazeIndicatorViewModel(messenger);
            using var coordinator = new OverlayVmBindingCoordinator(new ModalInteractionState());
            coordinator.Initialize(TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
            {
                // The coordinator subscribes to every VM in SetupPropertyChangedHandlers and
                // unsubscribes from all of them in OnDispose, so each must be real (the shared
                // builder leaves them null! for tests that never touch them).
                CursorVM = new CursorCircleViewModel(settings, messenger),
                ActivationVM = new ActivationButtonViewModel(settings, messenger),
                ManualSelectionVM = new ManualSelectionOverlayViewModel(settings, messenger),
                ManualSelectionIndicatorVM = indicatorVm,
                ActionMenuVM = new ActionMenuViewModel(settings, messenger),
                ToolbarVM = new ToolbarViewModel(settings, messenger),
                BackdropVM = new GlassBackdropViewModel(settings, messenger),
                TaskSwitcherVM = new TaskSwitcherViewModel(windowManager: null!, settings, messenger),
                OverlayManager = new SingleIndicatorOverlayManager(indicator),
                Dispatcher = Dispatcher.CurrentDispatcher,
            });

            // The gaze positions the window; the anchor then seats at a fixed screen point through
            // the same properties ManualSelectionAnchorChangedMessage sets in production.
            indicatorVm.UpdateGazePosition(new PhysicalPoint(1000, 500));
            indicatorVm.AnchorX = 1072;
            indicatorVm.AnchorY = 500;
            indicatorVm.IsAnchorVisible = true;

            var mark = (FrameworkElement)indicator.FindName("AnchorMark");
            Check.That(mark.Visibility).IsEqualTo(Visibility.Visible);
            Check.That(Canvas.GetLeft(mark)).IsCloseTo(102 + 72 - 12, 0.001);

            indicatorVm.IsAnchorVisible = false;
            Check.That(mark.Visibility).IsEqualTo(Visibility.Collapsed);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
        }
    }

    /// <summary>
    /// Only the two members the coordinator touches. <c>GlassBackdrop</c> returns null on purpose:
    /// <c>SetupBackdrop</c> only assigns it and every consumer null-guards. Everything else throws
    /// so an accidental reach fails loudly instead of with an NRE somewhere unrelated.
    /// </summary>
    private sealed class SingleIndicatorOverlayManager : IOverlayManager
    {
        private readonly IManualSelectionIndicatorOverlay _indicator;

        internal SingleIndicatorOverlayManager(IManualSelectionIndicatorOverlay indicator) => _indicator = indicator;

        public IManualSelectionIndicatorOverlay ManualSelectionIndicator => _indicator;

        public IGlassBackdropOverlay GlassBackdrop => null!;

        public ICursorCircleOverlay CursorCircle => throw new NotImplementedException();
        public IActivationButtonOverlay ActivationButton => throw new NotImplementedException();
        public IManualSelectionOverlay ManualSelectionOverlay => throw new NotImplementedException();
        public IActionMenuOverlay ActionMenu => throw new NotImplementedException();
        public IToolbarOverlay Toolbar => throw new NotImplementedException();
        public IPauseIndicatorOverlay PauseIndicator => throw new NotImplementedException();
        public KeyboardWindow Keyboard => throw new NotImplementedException();
        public ICalibrationOverlay Calibration => throw new NotImplementedException();
        public ITargetHighlightOverlay TargetHighlight => throw new NotImplementedException();
        public IEdgeButtonBar EdgeButtonBar => throw new NotImplementedException();
        public ITrackStatusOverlay TrackStatus => throw new NotImplementedException();
        public ITaskSwitcherOverlay TaskSwitcher => throw new NotImplementedException();
        public IModesMenuOverlay ModesMenu => throw new NotImplementedException();
        public IRepeatCursorOverlay RepeatCursor => throw new NotImplementedException();
        public IQuickActionsGridOverlay QuickActionsGrid => throw new NotImplementedException();
        public void Initialize(OverlayViewModels viewModels, EyeCtrlSettings settings, GazeProviderType providerType)
            => throw new NotImplementedException();
    }
}
