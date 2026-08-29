namespace EyeCtrl.App.Services.Coordinators;

using System.ComponentModel;
using System.Windows.Interop;
using EyeCtrl.App.Overlays;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using CommunityToolkit.Mvvm.Messaging;

/// <summary>
/// Owns the drag surface of <see cref="EventCoordinator"/>: the <c>DragStarted</c> hook that presses
/// the mouse and raises the trail overlay, the drop/cancel message registrations, the drag-trail VM
/// binding (including the right-edge cancel/back buttons), and the WM_KEYDOWN/VK_ESCAPE thread filter
/// that cancels an in-flight drag. Extracted from <c>EventCoordinator</c> as part of the
/// sub-coordinator decomposition (cluster 4).
/// </summary>
internal sealed class DragCoordinator : CoordinatorBase
{
    private const double RightEdgeInsetLogical = 30;

    private DragTrailOverlay? _dragTrailOverlay;
    private PhysicalPoint _dragGrabOrigin;
    private Action? _disposeEscapeHook;
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
        ComponentDispatcher.ThreadFilterMessage += OnThreadFilterMessage;
        _disposeEscapeHook = () => ComponentDispatcher.ThreadFilterMessage -= OnThreadFilterMessage;
    }

    private void OnThreadFilterMessage(ref MSG msg, ref bool handled)
    {
        if (msg.message == 0x0100 && (int)msg.wParam == 0x1B) // WM_KEYDOWN, VK_ESCAPE
        {
            if (Context?.InteractionManager.CurrentState == InteractionState.Dragging)
            {
                Context.Messenger.Send(new DragCancelMessage());
                handled = true;
            }
        }
    }

    private void OnDragStarted(PhysicalPoint grabOrigin)
    {
        if (Context is null) return;

        _dragGrabOrigin = grabOrigin;
        Context.InputSimulator.MouseDown(grabOrigin);

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

    private void OnDragDrop(DragDropMessage msg)
    {
        Context?.OverlayManager.EdgeButtonBar.Hide();
        Context?.InputSimulator.MouseUp(msg.DropPoint);
        Context?.InteractionManager.CompleteDrag();
        CloseDragOverlay();
    }

    private void OnDragCancel()
    {
        Context?.OverlayManager.EdgeButtonBar.Hide();
        Context?.InputSimulator.MouseUp(_dragGrabOrigin);
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
        Context.InteractionManager.DragStarted -= OnDragStarted;
        Context.DragTrailVM.PropertyChanged -= OnDragTrailVMPropertyChanged;
        _disposeEscapeHook?.Invoke();
        CloseDragOverlay();
    }
}
