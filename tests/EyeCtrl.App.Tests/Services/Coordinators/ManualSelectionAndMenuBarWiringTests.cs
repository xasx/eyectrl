namespace EyeCtrl.App.Tests.Services.Coordinators;

using System.Collections.Generic;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.App.Tests.Services;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// A4 fixup-01 F2 + F4: the manual-selection release and the two remaining CALL-SITE anchors.
///
/// <para>
/// F2 (M20): the earlier ownership tests seeded and released the manual-selection pair by hand.
/// Here the real handlers do it: <see cref="ManualSelectionOverlayViewModel.IsVisible"/> true shows
/// the pair through <see cref="OverlayVmBindingCoordinator"/>, false must release it and leave an
/// EMPTY bar — and a later menu claim/release must not restore a ghost pair.
/// </para>
/// <para>
/// F4 (M11/M12): the DPI-equality theory read the static anchor properties, so a handler that
/// passed its own 50-DIP inset stayed green. These drive <c>ShowActionMenuMessage</c> and the
/// manual-selection visibility edge, then compare the <c>SeedCenters</c> the handler actually used
/// with the shared slot, at DPI 1.0 and 2.0.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class ManualSelectionAndMenuBarWiringTests
{
    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void ManualSelectionShow_DrawsCloseOnTheSharedSlot_AndHideEmptiesTheBar(double scale)
    {
        WithDpi(scale, () =>
        {
            using var world = new World();
            world.Messenger.Send(new StartManualSelectionMessage(
                new PhysicalPoint(500, 500), 150, 2.0, new PhysicalPoint(500, 500)));
            world.Pump();

            Check.That(world.ManualSelectionVM.IsVisible).IsTrue();
            Check.That(world.Bar.EdgeBarVm).IsNotNull();
            Check.That(world.Bar.EdgeBarVm!.CurrentOwner).IsEqualTo(EdgeBarOwner.ManualSelection);

            var close = ManualSelectionEdgeButtons.Definitions(
                    world.Settings.Dwell.ManualSelectionSideButtonDwellMs)
                .Single(d => d.Id == ManualSelectionEdgeButtons.CloseId);
            var drawn = world.Bar.EdgeBarVm.SeedCenters[ManualSelectionEdgeButtons.CloseId];
            Check.WithCustomMessage(
                    $"manual selection drew Close at ({drawn.X:F1},{drawn.Y:F1}), not on the "
                    + "shared exit slot (M12).")
                 .That(drawn).IsEqualTo(
                     ScreenGeometry.SharedRightEdgeExitAnchor()
                     + new PhysicalPoint(0, close.VerticalOffset));

            world.Messenger.Send(new EndManualSelectionMessage());
            world.Pump();

            Check.That(world.ManualSelectionVM.IsVisible).IsFalse();
            Check.WithCustomMessage(
                    $"the magnifier closed but its exits stayed up: "
                    + $"[{string.Join(",", world.Bar.ShownButtonIds)}] (M20).")
                 .That(world.Bar.EdgeBarVm.Buttons).IsEmpty();
            Check.That(world.Bar.EdgeBarVm.CurrentOwner).IsNull();

            // A later menu claim/release must not resurrect the released pair.
            world.ShowActionMenu();
            Check.That(world.Bar.EdgeBarVm.CurrentOwner).IsEqualTo(EdgeBarOwner.ActionMenu);
            world.Messenger.Send(new HideActionMenuMessage());
            world.Pump();

            Check.WithCustomMessage(
                    $"a later menu release restored ghost exits: "
                    + $"[{string.Join(",", world.Bar.ShownButtonIds)}]")
                 .That(world.Bar.EdgeBarVm.Buttons).IsEmpty();
        });
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void ActionMenuShow_DrawsCloseOnTheSharedSlot(double scale)
    {
        WithDpi(scale, () =>
        {
            using var world = new World();
            world.ShowActionMenu();

            Check.That(world.Bar.EdgeBarVm).IsNotNull();
            Check.That(world.Bar.EdgeBarVm!.CurrentOwner).IsEqualTo(EdgeBarOwner.ActionMenu);

            var close = ActionMenuEdgeButtons.Definitions(world.Settings.Dwell.ActionMenuDwellMs)
                .Single(d => d.Id == ActionMenuEdgeButtons.CloseId);
            var drawn = world.Bar.EdgeBarVm.SeedCenters[ActionMenuEdgeButtons.CloseId];
            Check.WithCustomMessage(
                    $"the action menu drew Close at ({drawn.X:F1},{drawn.Y:F1}), not on the "
                    + "shared exit slot (M11).")
                 .That(drawn).IsEqualTo(
                     ScreenGeometry.SharedRightEdgeExitAnchor()
                     + new PhysicalPoint(0, close.VerticalOffset));
        });
    }

    private static void WithDpi(double scale, Action body)
    {
        DpiHelper.Initialize(scale, scale);
        try { body(); }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    /// <summary>
    /// The real message + vm-binding coordinators over a real edge bar and real VMs. Only the
    /// overlays those two coordinators dereference on the show/hide paths are supplied; everything
    /// else throws, so a path this file does not intend fails loudly.
    /// </summary>
    private sealed class World : IDisposable
    {
        private readonly OverlayMessageCoordinator _messages;
        private readonly OverlayVmBindingCoordinator _vmBinding;
        private readonly Dispatcher _dispatcher;

        internal World()
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            Messenger = new WeakReferenceMessenger();
            Settings = new EyeCtrlSettings();
            Bar = new TestFakes.FakeEdgeButtonBar();
            ModBar = new TestFakes.FakeEdgeButtonBar();
            ManualSelectionVM = new ManualSelectionOverlayViewModel(Settings, Messenger);

            var modal = new ModalInteractionState();
            _messages = new OverlayMessageCoordinator(
                new ToolbarLifecycleCoordinator(modal), new OverlayVmBindingCoordinator(modal));
            _vmBinding = new OverlayVmBindingCoordinator(modal);

            var context = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
            {
                InteractionManager = new InteractionManager(),
                Messenger = Messenger,
                Dispatcher = _dispatcher,
                Settings = Settings,
                CursorVM = new CursorCircleViewModel(Settings, Messenger),
                ActivationVM = new ActivationButtonViewModel(Messenger),
                ManualSelectionVM = ManualSelectionVM,
                ManualSelectionIndicatorVM = new ManualSelectionGazeIndicatorViewModel(Messenger),
                ActionMenuVM = new ActionMenuViewModel(Settings, Messenger),
                ToolbarVM = new ToolbarViewModel(Settings, Messenger),
                BackdropVM = new GlassBackdropViewModel(Settings, Messenger),
                TaskSwitcherVM = new TaskSwitcherViewModel(windowManager: null!, Settings, Messenger),
                OverlayManager = new FakeOverlays(Bar, ModBar),
            };

            _messages.Initialize(context);
            _vmBinding.Initialize(context);
        }

        internal WeakReferenceMessenger Messenger { get; }

        internal EyeCtrlSettings Settings { get; }

        internal TestFakes.FakeEdgeButtonBar Bar { get; }

        internal TestFakes.FakeEdgeButtonBar ModBar { get; }

        internal ManualSelectionOverlayViewModel ManualSelectionVM { get; }

        internal void ShowActionMenu()
        {
            Messenger.Send(new ShowActionMenuMessage(
                new PhysicalPoint(500, 500),
                new List<InteractionAction> { InteractionAction.LeftClick },
                CircleSize: 250,
                DwellTimeMs: 600,
                GazePoint: new PhysicalPoint(500, 500),
                ShowBack: false));
            Pump();
        }

        internal void Pump() => _dispatcher.Invoke(() => { }, DispatcherPriority.Background);

        public void Dispose()
        {
            _messages.Dispose();
            _vmBinding.Dispose();
        }
    }

    /// <summary>Only the members the two coordinators touch on these paths.</summary>
    private sealed class FakeOverlays : IOverlayManager
    {
        private readonly IEdgeButtonBar _bar;
        private readonly IEdgeButtonBar _modBar;

        internal FakeOverlays(IEdgeButtonBar bar, IEdgeButtonBar modBar) => (_bar, _modBar) = (bar, modBar);

        public IEdgeButtonBar EdgeButtonBar => _bar;
        public IEdgeButtonBar ActionMenuModBar => _modBar;
        public IActionMenuOverlay ActionMenu { get; } = new NoOpActionMenu();
        public IManualSelectionOverlay ManualSelectionOverlay { get; } = new NoOpManualSelection();
        public IManualSelectionIndicatorOverlay ManualSelectionIndicator { get; } = new NoOpIndicator();
        public IGlassBackdropOverlay GlassBackdrop { get; } = new NoOpBackdrop();
        public ICursorCircleOverlay CursorCircle { get; } = new NoOpCursorCircle();
        public IActivationButtonOverlay ActivationButton { get; } = new NoOpActivationButton();

        public IToolbarOverlay Toolbar => throw new NotImplementedException();
        public IPauseIndicatorOverlay PauseIndicator => throw new NotImplementedException();
        public KeyboardWindow Keyboard => throw new NotImplementedException();
        public ICalibrationOverlay Calibration => throw new NotImplementedException();
        public ITargetHighlightOverlay TargetHighlight => throw new NotImplementedException();
        public ITrackStatusOverlay TrackStatus => throw new NotImplementedException();
        public ITaskSwitcherOverlay TaskSwitcher => throw new NotImplementedException();
        public IModesMenuOverlay ModesMenu => throw new NotImplementedException();
        public IRepeatCursorOverlay RepeatCursor => throw new NotImplementedException();
        public IQuickActionsGridOverlay QuickActionsGrid => throw new NotImplementedException();

        public void Initialize(OverlayViewModels viewModels, EyeCtrlSettings settings,
            GazeProviderType providerType) => throw new NotImplementedException();
    }

    private sealed class NoOpActionMenu : IActionMenuOverlay
    {
        public void Show(PhysicalPoint center, List<InteractionAction> actions, bool showBackButton,
            int dwellTimeMs, double ringRadius, double previewCircleSize, string borderColor,
            PositionIndicatorStyle positionIndicatorStyle, PhysicalPoint? zoomCenter = null,
            int? zoomRadius = null, PhysicalPoint? gazePoint = null, bool showLabels = false,
            double previewMagnification = 1.0) { }

        public void Hide() { }
    }

    private sealed class NoOpManualSelection : IManualSelectionOverlay
    {
        public void Show(PhysicalPoint center, int radius, double magnification,
            PhysicalPoint? gazePoint = null, bool recapture = true) { }
        public void UpdateCenter(PhysicalPoint center, int radius, double magnification,
            PhysicalPoint? gazePoint = null, bool recapture = true) { }
        public void Hide() { }
        public PhysicalPoint GetRealPhysicalPoint(PhysicalPoint gazeOnScreen) => gazeOnScreen;
        public PhysicalPoint EffectiveCaptureCenter => default;
    }

    private sealed class NoOpBackdrop : IGlassBackdropOverlay
    {
        public bool IsVisible => false;
        public void ShowBackdrop(BackdropSettings settings, PhysicalPoint buttonCenter) { }
        public void Show() { }
        public void Hide() { }
        public void CompleteHideNow() { }
        public void ApplyBackdrop(BackdropSettings settings) { }
        public void DrawHideButton(PhysicalPoint buttonCenter, double progress = 0) { }
        public void UpdateHideButtonProgress(double progress) { }
        public void ShowEscapeHint() { }
        public void HideEscapeHint() { }
    }

    private sealed class NoOpIndicator : IManualSelectionIndicatorOverlay
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

    private sealed class NoOpCursorCircle : ICursorCircleOverlay
    {
        public void Show() { }
        public void Hide() { }
        public void UpdatePosition(PhysicalPoint point) { }
        public void MoveTo(PhysicalPoint point, bool glide) { }
        public void UpdateAppearance(CursorSettings settings) { }
        public void SetPinned(bool pinned) { }
    }

    private sealed class NoOpActivationButton : IActivationButtonOverlay
    {
        public void Show() { }
        public void ShowAt(PhysicalPoint position, double fixationRadiusPixels) { }
        public void Hide() { }
        public void HideImmediate() { }
        public void UpdateDwellProgress(double progress) { }
    }
}
