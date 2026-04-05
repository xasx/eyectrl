namespace EyeCtrl.App.Services;

using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels.Messages;
using EyeCtrl.ViewModels.Helpers;

public class GazeEngineAdapter : IDisposable
{
    private readonly GazeEngine _gazeEngine;
    private readonly InteractionManager _interactionManager;
    private readonly EyeCtrlSettings _settings;
    private readonly IMessenger _messenger;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<GazeEngineAdapter> _logger;

    public GazeEngineAdapter(
        GazeEngine gazeEngine,
        EyeCtrlSettings settings,
        IMessenger messenger,
        Dispatcher dispatcher,
        ILogger<GazeEngineAdapter> logger)
    {
        _logger = logger;
        _gazeEngine = gazeEngine;
        _interactionManager = gazeEngine.InteractionManager;
        _settings = settings;
        _messenger = messenger;
        _dispatcher = dispatcher;

        // Core -> ViewModel (via messages, marshaled to UI thread)
        _gazeEngine.CursorMoved += OnCursorMoved;
        _interactionManager.ShowActivationButton += OnShowActivationButton;
        _interactionManager.HideActivationButton += OnHideActivationButton;
        _interactionManager.ShowActionMenu += OnShowActionMenu;
        _interactionManager.HideActionMenu += OnHideActionMenu;
        _interactionManager.StartZoom += OnStartZoom;
        _interactionManager.EndZoom += OnEndZoom;
        _interactionManager.HideZoom += OnHideZoom;
        _interactionManager.StateChanged += OnStateChanged;
        _interactionManager.ActionRequested += OnActionRequested;

        // ViewModel -> Core (via message subscriptions)
        messenger.Register<ActivationCompletedMessage>(this, (_, _) =>
            _interactionManager.OnActivationButtonDwelled());
        messenger.Register<ActionSelectedMessage>(this, (_, msg) =>
            _interactionManager.OnActionSelected(msg.Action));
        messenger.Register<BackSelectedMessage>(this, (_, _) =>
            _interactionManager.OnZoomBack());
        messenger.Register<PauseToggledMessage>(this, (_, _) =>
            _interactionManager.TogglePause());
        messenger.Register<ZoomToggledMessage>(this, (_, _) =>
        {
            _interactionManager.ZoomEnabled = !_interactionManager.ZoomEnabled;
            _settings.Zoom.Enabled = _interactionManager.ZoomEnabled;
        });
    }

    private void OnCursorMoved(PhysicalPoint point, long timestamp) =>
        _dispatcher.BeginInvoke(() => _messenger.Send(new GazeCursorMovedMessage(point, timestamp)));

    private void OnShowActivationButton(PhysicalPoint point) =>
        _dispatcher.BeginInvoke(() => _messenger.Send(new ShowActivationButtonMessage(point)));

    private void OnHideActivationButton() =>
        _dispatcher.BeginInvoke(() => _messenger.Send(new HideActivationButtonMessage()));

    private void OnShowActionMenu(PhysicalPoint point) =>
        _dispatcher.BeginInvoke(() =>
        {
            var im = _interactionManager;
            var showBack = im.CurrentState == InteractionState.ZoomSelectionActionMenuVisible;
            var dwellMs = showBack
                ? _settings.Dwell.ZoomActionMenuDwellMs
                : _settings.Dwell.ActionMenuDwellMs;

            var zoomDisplayRadius = showBack
                ? (int)(_settings.Zoom.CircleRadiusPixels * _settings.Zoom.MagnificationLevel)
                : 0;

            // Get screen dimensions in physical pixels
            var physicalScreenW = DpiHelper.LogicalToPhysicalX(System.Windows.SystemParameters.PrimaryScreenWidth);
            var physicalScreenH = DpiHelper.LogicalToPhysicalY(System.Windows.SystemParameters.PrimaryScreenHeight);

            // Calculate display point using helper (moves coordinate logic to ViewModels layer)
            var circleSize = _settings.Cursor.CircleSize;
            var (clampedPoint, _) = ActionMenuLayoutHelper.CalculateDisplayPoint(
                point,
                _settings.ActionMenu.Position,
                circleSize,
                physicalScreenW,
                physicalScreenH);

            _messenger.Send(new ShowActionMenuMessage(
                clampedPoint,
                _settings.ActionMenu.VisibleActions,
                showBack,
                dwellMs,
                point, // GazePoint - always pass the original gaze point for indicator positioning
                zoomDisplayRadius,
                circleSize)
            {
                // Zoom path only (showBack == true): pass zoom circle context for preview capture.
                // ZoomCenter = the ORIGINAL pre-zoom fixation (center of zoom circle), NOT the precise in-zoom point.
                // The precise in-zoom point is already in clampedPoint / message.Center.
                // FixationPoint is always non-null in the zoom path (it is set before zoom activation).
                // ZoomRadius is logical/DIP (CircleRadiusPixels); ActionMenuWindow converts it to physical with ScaleX.
                ZoomCenter = showBack ? _interactionManager.FixationPoint! : null,
                ZoomRadius = showBack ? _settings.Zoom.CircleRadiusPixels : null
            });
        });

    private void OnHideActionMenu() =>
        _dispatcher.BeginInvoke(() => _messenger.Send(new HideActionMenuMessage()));

    private void OnStartZoom(PhysicalPoint point) =>
        _dispatcher.BeginInvoke(() =>
        {
            // Get screen dimensions in physical pixels
            var physicalScreenW = DpiHelper.LogicalToPhysicalX(System.Windows.SystemParameters.PrimaryScreenWidth);
            var physicalScreenH = DpiHelper.LogicalToPhysicalY(System.Windows.SystemParameters.PrimaryScreenHeight);

            // Calculate display point using helper (moves coordinate logic to ViewModels layer)
            var displayPoint = ZoomLayoutHelper.CalculateDisplayPoint(
                point,
                _settings.Zoom.Position,
                physicalScreenW,
                physicalScreenH);

            _messenger.Send(new StartZoomMessage(
                displayPoint,
                _settings.Zoom.CircleRadiusPixels,
                _settings.Zoom.MagnificationLevel,
                point  // GazePoint - always pass the original gaze point for capture
            ));
        });

    private void OnEndZoom() =>
        _dispatcher.BeginInvoke(() => _messenger.Send(new EndZoomMessage()));

    private void OnHideZoom() =>
        _dispatcher.BeginInvoke(() => _messenger.Send(new ZoomHiddenMessage()));

    private void OnStateChanged(InteractionState state)
    {
        _logger.LogInformation("State → {State}", state);
        _dispatcher.BeginInvoke(() => _messenger.Send(new StateChangedMessage(state)));
    }

    private void OnActionRequested(InteractionAction action, PhysicalPoint point)
    {
        var isFromZoom = _interactionManager.CurrentState == InteractionState.ZoomSelectionActionMenuVisible;
        _dispatcher.BeginInvoke(() => _messenger.Send(new ActionRequestedMessage(action, point, isFromZoom)));
    }

    public void Dispose()
    {
        _gazeEngine.CursorMoved -= OnCursorMoved;
        _interactionManager.ShowActivationButton -= OnShowActivationButton;
        _interactionManager.HideActivationButton -= OnHideActivationButton;
        _interactionManager.ShowActionMenu -= OnShowActionMenu;
        _interactionManager.HideActionMenu -= OnHideActionMenu;
        _interactionManager.StartZoom -= OnStartZoom;
        _interactionManager.EndZoom -= OnEndZoom;
        _interactionManager.HideZoom -= OnHideZoom;
        _interactionManager.StateChanged -= OnStateChanged;
        _interactionManager.ActionRequested -= OnActionRequested;
        GC.SuppressFinalize(this);
    }
}
