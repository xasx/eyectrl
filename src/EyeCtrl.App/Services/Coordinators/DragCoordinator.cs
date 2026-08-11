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

internal sealed class DragCoordinator : IDisposable
{
    private const double RightEdgeInsetLogical = 30;

    private EventCoordinationContext? _context;
    private DragTrailOverlay? _dragTrailOverlay;
    private PhysicalPoint _dragGrabOrigin;
    private Action? _disposeEscapeHook;
    private bool _disposed;

    public void Initialize(EventCoordinationContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _context.InteractionManager.DragStarted += OnDragStarted;
        _context.DragTrailVM.PropertyChanged += OnDragTrailVMPropertyChanged;
        _context.Messenger.Register<DragCoordinator, DragDropMessage>(this, (_, m) =>
            _context.Dispatcher.BeginInvoke(() => OnDragDrop(m)));
        _context.Messenger.Register<DragCoordinator, DragCancelMessage>(this, (_, _) =>
            _context.Dispatcher.BeginInvoke(OnDragCancel));

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
            if (_context?.InteractionManager.CurrentState == InteractionState.Dragging)
            {
                _context.Messenger.Send(new DragCancelMessage());
                handled = true;
            }
        }
    }

    private void OnDragStarted(PhysicalPoint grabOrigin)
    {
        if (_context == null) return;

        _dragGrabOrigin = grabOrigin;
        _context.InputSimulator.MouseDown(grabOrigin);

        _dragTrailOverlay = new DragTrailOverlay();
        _dragTrailOverlay.Configure(_context.Settings.Drag.TrailColor, _context.Settings.Drag.TrailOpacity);
        _dragTrailOverlay.Show();

        var anchor = ScreenGeometry.RightEdgeAnchor(RightEdgeInsetLogical, UIConstants.DragButtonRadius);
        var cancelDef = new EdgeButtonDefinition(
            "Drag_cancel", "\u2715", _context.Settings.Dwell.DragRightEdgeButtonDwellMs,
            m => m.Send(new DragCancelMessage()),
            VerticalOffset: DpiHelper.LogicalToPhysicalY(30));
        _context.OverlayManager.EdgeButtonBar.ShowButtons(new[] { cancelDef }, anchor);

        _context.Messenger.Send(new StartDragTrailMessage(grabOrigin));
    }

    private void OnDragDrop(DragDropMessage msg)
    {
        _context?.OverlayManager.EdgeButtonBar.Hide();
        _context?.InputSimulator.MouseUp(msg.DropPoint);
        _context?.InteractionManager.CompleteDrag();
        CloseDragOverlay();
    }

    private void OnDragCancel()
    {
        _context?.OverlayManager.EdgeButtonBar.Hide();
        _context?.InputSimulator.MouseUp(_dragGrabOrigin);
        _context?.InteractionManager.CompleteDrag();
        CloseDragOverlay();
    }

    private void CloseDragOverlay()
    {
        _dragTrailOverlay?.Close();
        _dragTrailOverlay = null;
    }

    private void OnDragTrailVMPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_context == null || _dragTrailOverlay == null) return;
        var vm = _context.DragTrailVM;

        switch (e.PropertyName)
        {
            case nameof(DragTrailViewModel.CursorPosition):
            case nameof(DragTrailViewModel.SettleProgress):
            case nameof(DragTrailViewModel.ReleaseButtonCenter):
                _dragTrailOverlay.Update(
                    vm.GrabOrigin, vm.CursorPosition, vm.SettleProgress, vm.ReleaseButtonCenter);
                break;
            case nameof(DragTrailViewModel.ShowBack):
                var bar = _context.OverlayManager.EdgeButtonBar;
                if (_context.DragTrailVM.ShowBack)
                {
                    var backDef = new EdgeButtonDefinition(
                        "Drag_back", "\u21A9", _context.Settings.Dwell.DragRightEdgeButtonDwellMs,
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

    public void Dispose()
    {
        if (_disposed || _context is null) { _disposed = true; return; }
        _context.InteractionManager.DragStarted -= OnDragStarted;
        _context.DragTrailVM.PropertyChanged -= OnDragTrailVMPropertyChanged;
        _disposeEscapeHook?.Invoke();
        CloseDragOverlay();
        _context.Messenger.UnregisterAll(this);
        _disposed = true;
    }
}
