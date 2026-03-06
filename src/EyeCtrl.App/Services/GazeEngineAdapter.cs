namespace EyeCtrl.App.Services;

using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class GazeEngineAdapter : IDisposable
{
    private readonly GazeEngine _gazeEngine;
    private readonly InteractionManager _interactionManager;
    private readonly EyeCtrlSettings _settings;
    private readonly IMessenger _messenger;
    private readonly Dispatcher _dispatcher;

    public GazeEngineAdapter(
        GazeEngine gazeEngine,
        EyeCtrlSettings settings,
        IMessenger messenger,
        Dispatcher dispatcher)
    {
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

    private void OnCursorMoved(ScreenPoint point, long timestamp) =>
        _dispatcher.BeginInvoke(() => _messenger.Send(new GazeCursorMovedMessage(point, timestamp)));

    private void OnShowActivationButton(ScreenPoint point) =>
        _dispatcher.BeginInvoke(() => _messenger.Send(new ShowActivationButtonMessage(point)));

    private void OnHideActivationButton() =>
        _dispatcher.BeginInvoke(() => _messenger.Send(new HideActivationButtonMessage()));

    private void OnShowActionMenu(ScreenPoint point) =>
        _dispatcher.BeginInvoke(() =>
        {
            var im = _interactionManager;
            var showBack = im.CurrentState == InteractionState.ZoomActionMenuVisible;
            var dwellMs = showBack
                ? _settings.Dwell.ZoomActionMenuDwellMs
                : _settings.Dwell.ActionMenuDwellMs;

            // Clamp center so the full button ring stays on-screen.
            // RingRadius=160 + ButtonRadius=50 = 210px margin on each side.
            const double margin = 210;
            var screenW = System.Windows.SystemParameters.PrimaryScreenWidth;
            var screenH = System.Windows.SystemParameters.PrimaryScreenHeight;
            var clampedPoint = new ScreenPoint(
                Math.Clamp(point.X, margin, screenW - margin),
                Math.Clamp(point.Y, margin, screenH - margin));

            _messenger.Send(new ShowActionMenuMessage(clampedPoint, _settings.ActionMenu.VisibleActions, showBack, dwellMs));
        });

    private void OnHideActionMenu() =>
        _dispatcher.BeginInvoke(() => _messenger.Send(new HideActionMenuMessage()));

    private void OnStartZoom(ScreenPoint point) =>
        _dispatcher.BeginInvoke(() =>
            _messenger.Send(new StartZoomMessage(point, _settings.Zoom.CircleRadiusPixels, _settings.Zoom.MagnificationLevel)));

    private void OnEndZoom() =>
        _dispatcher.BeginInvoke(() => _messenger.Send(new EndZoomMessage()));

    private void OnStateChanged(InteractionState state) =>
        _dispatcher.BeginInvoke(() => _messenger.Send(new StateChangedMessage(state)));

    private void OnActionRequested(InteractionAction action, ScreenPoint point) =>
        _dispatcher.BeginInvoke(() => _messenger.Send(new ActionRequestedMessage(action, point)));

    public void Dispose()
    {
        _gazeEngine.CursorMoved -= OnCursorMoved;
        _interactionManager.ShowActivationButton -= OnShowActivationButton;
        _interactionManager.HideActivationButton -= OnHideActivationButton;
        _interactionManager.ShowActionMenu -= OnShowActionMenu;
        _interactionManager.HideActionMenu -= OnHideActionMenu;
        _interactionManager.StartZoom -= OnStartZoom;
        _interactionManager.EndZoom -= OnEndZoom;
        _interactionManager.StateChanged -= OnStateChanged;
        _interactionManager.ActionRequested -= OnActionRequested;
        GC.SuppressFinalize(this);
    }
}
