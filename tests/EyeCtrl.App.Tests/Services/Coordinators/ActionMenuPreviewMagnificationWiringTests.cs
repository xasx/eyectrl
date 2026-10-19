namespace EyeCtrl.App.Tests.Services.Coordinators;

using System.Reflection;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.App.Tests.Services;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// G1: the action-menu preview shows the picked spot at 1×, on both paths. The zoom path (back
/// from the magnifier) used to pass the settings' <c>MagnificationLevel</c> through, which made the
/// menu preview inherit the magnifier's 2×; the owner asked for a plain, unzoomed picture there.
/// A non-default <c>MagnificationLevel</c> is set so a regression that reaches for it (mutant N1)
/// changes the recorded value and fails here.
/// </summary>
public class ActionMenuPreviewMagnificationWiringTests
{
    [Fact]
    public void ShowActionMenu_PassesOneToOneOnBothPaths()
    {
        var settings = new EyeCtrlSettings();
        settings.ManualSelection.MagnificationLevel = 2.5;
        var messenger = new WeakReferenceMessenger();
        var menuVm = new ActionMenuViewModel(settings, messenger);
        var recording = new RecordingActionMenuOverlay();

        var ctx = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            Settings = settings,
            Messenger = messenger,
            ActionMenuVM = menuVm,
            OverlayManager = TestFakes.MakeOverlayManager(
                actionMenu: recording,
                cursorCircle: new TestFakes.FakeCursorCircleOverlay(),
                activationButton: new TestFakes.FakeActivationButtonOverlay()),
        };
        var coordinator = new OverlayVmBindingCoordinator(new ModalInteractionState());
        SetContext(coordinator, ctx);

        var center = new PhysicalPoint(500, 500);

        // The zoom path: the manual-selection menu. The settings' non-default factor must NOT reach
        // the preview — the picked spot is shown at 1× (no zoom).
        menuVm.Receive(new ShowActionMenuMessage(
            center, [InteractionAction.LeftClick], ShowBack: true, DwellTimeMs: 600, GazePoint: center)
        {
            ManualSelectionCenter = center,
            ManualSelectionRadius = 170,
        });
        InvokeActionMenuHandler(coordinator, nameof(ActionMenuViewModel.IsVisible));

        Check.WithCustomMessage(
                "the zoom path must show the preview at 1× (no zoom), not at the magnifier's "
                + $"configured factor: saw {string.Join(", ", recording.PreviewMagnifications)}.")
            .That(recording.PreviewMagnifications).ContainsExactly(1.0);

        // The plain path: the ordinary action menu is 1:1 too.
        menuVm.IsVisible = false;
        menuVm.Receive(new ShowActionMenuMessage(
            center, [InteractionAction.LeftClick], ShowBack: false, DwellTimeMs: 600, GazePoint: center));
        InvokeActionMenuHandler(coordinator, nameof(ActionMenuViewModel.IsVisible));

        Check.WithCustomMessage(
                "the plain menu path must keep the preview at 1.0×: "
                + $"saw {string.Join(", ", recording.PreviewMagnifications)}.")
            .That(recording.PreviewMagnifications).ContainsExactly(1.0, 1.0);
    }

    private static void SetContext(OverlayVmBindingCoordinator coordinator, EventCoordinationContext context) =>
        typeof(CoordinatorBase)
            .GetProperty("Context", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(coordinator, context);

    private static void InvokeActionMenuHandler(OverlayVmBindingCoordinator coordinator, string propertyName) =>
        typeof(OverlayVmBindingCoordinator)
            .GetMethod("OnActionMenuVMPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(coordinator, new object?[] { null, new System.ComponentModel.PropertyChangedEventArgs(propertyName) });

    /// <summary>Records the preview factor each show was given; the window itself must never be shown.</summary>
    private sealed class RecordingActionMenuOverlay : IActionMenuOverlay
    {
        public List<double> PreviewMagnifications { get; } = new();

        public void Show(PhysicalPoint center, List<InteractionAction> actions, bool showBackButton,
            int dwellTimeMs, double ringRadius, double previewCircleSize, string borderColor,
            PositionIndicatorStyle positionIndicatorStyle, PhysicalPoint? zoomCenter = null,
            int? zoomRadius = null, PhysicalPoint? gazePoint = null, bool showLabels = false,
            double previewMagnification = 1.0)
        {
            PreviewMagnifications.Add(previewMagnification);
        }

        public void Hide() { }
    }
}
