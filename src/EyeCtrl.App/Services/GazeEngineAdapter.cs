namespace EyeCtrl.App.Services;

using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using EyeCtrl.Core;
using EyeCtrl.Core.Interfaces;
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
    private readonly IControlFinder _controlFinder;

    public GazeEngineAdapter(
        GazeEngine gazeEngine,
        EyeCtrlSettings settings,
        IMessenger messenger,
        Dispatcher dispatcher,
        ILogger<GazeEngineAdapter> logger,
        IControlFinder controlFinder)
    {
        _logger = logger;
        _gazeEngine = gazeEngine;
        _interactionManager = gazeEngine.InteractionManager;
        _settings = settings;
        _messenger = messenger;
        _dispatcher = dispatcher;
        _controlFinder = controlFinder;

        // Core -> ViewModel (via messages, marshaled to UI thread)
        _gazeEngine.CursorMoved += OnCursorMoved;
        _interactionManager.ShowActivationButton += OnShowActivationButton;
        _interactionManager.HideActivationButton += OnHideActivationButton;
        _interactionManager.ShowActionMenu += OnShowActionMenu;
        _interactionManager.HideActionMenu += OnHideActionMenu;
        _interactionManager.StartManualSelection += OnStartManualSelection;
        _interactionManager.EndManualSelection += OnEndManualSelection;
        _interactionManager.HideManualSelection += OnHideManualSelection;
        _interactionManager.StateChanged += OnStateChanged;
        _interactionManager.ActionRequested += OnActionRequested;

        // ViewModel -> Core (via message subscriptions)
        messenger.Register<ActivationCompletedMessage>(this, async (_, _) =>
        {
            try
            {
                if (_interactionManager.SelectionMode == SelectionMode.AutoSelection)
                {
                    var fixationPoint = _interactionManager.FixationPoint;
                    if (fixationPoint != null)
                    {
                        _interactionManager.AutoSelectionTarget = await ResolveTargetAsync(fixationPoint.Value);
                    }
                }
                if (_interactionManager.SelectionMode == SelectionMode.AutoSelection
                    && _interactionManager.AutoSelectionTarget == null
                    && !_settings.AutoSelection.FallbackToPixelOnNoControl)
                {
                    _interactionManager.OnFixationEnded();
                    return;
                }
                _interactionManager.OnActivationButtonDwelled();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to handle activation completed");
                // Recovery must not throw out of this async void handler, or it crashes the app.
                try { _interactionManager.OnFixationEnded(); }
                catch (Exception resetEx) { _logger.LogError(resetEx, "Failed to reset interaction state after activation error"); }
            }
        });
        messenger.Register<ActionSelectedMessage>(this, (_, msg) =>
            _interactionManager.OnActionSelected(msg.Action));
        messenger.Register<BackSelectedMessage>(this, (_, _) =>
            _interactionManager.OnManualSelectionBack());
        messenger.Register<PauseToggledMessage>(this, (_, _) =>
            _interactionManager.TogglePause());
        messenger.Register<SelectionModeChangedMessage>(this, (_, msg) =>
        {
            _settings.SelectionMode = msg.Mode;
            _interactionManager.SelectionMode = msg.Mode;
        });

        messenger.Send(new SelectionModeChangedMessage(_settings.SelectionMode));
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
            var showBack = im.CurrentState == InteractionState.ManualSelectionActionMenuVisible;
            var dwellMs = showBack
                ? _settings.Dwell.ManualSelectionActionMenuDwellMs
                : _settings.Dwell.ActionMenuDwellMs;

            var ManualSelectionDisplayRadius = showBack
                ? (int)(_settings.ManualSelection.CircleRadiusPixels * _settings.ManualSelection.MagnificationLevel)
                : 0;

            // Get screen dimensions in physical pixels
            var physicalScreenW = DpiHelper.LogicalToPhysicalX(System.Windows.SystemParameters.PrimaryScreenWidth);
            var physicalScreenH = DpiHelper.LogicalToPhysicalY(System.Windows.SystemParameters.PrimaryScreenHeight);

            // Calculate display point using helper (moves coordinate logic to ViewModels layer)
            var circleSize = _settings.Cursor.CircleSize;
            var clampedPoint = ActionMenuLayoutHelper.CalculateDisplayPoint(
                point,
                _settings.ViewPosition,
                circleSize,
                physicalScreenW,
                physicalScreenH);

            _messenger.Send(new ShowActionMenuMessage(
                clampedPoint,
                _settings.ActionMenu.VisibleActions,
                showBack,
                dwellMs,
                point, // GazePoint - always pass the original gaze point for indicator positioning
                ManualSelectionDisplayRadius,
                circleSize)
            {
                // ManualSelection path only (showBack == true): pass ManualSelection circle context for preview capture.
                // ManualSelectionCenter = the ORIGINAL pre-ManualSelection fixation (center of ManualSelection circle), NOT the precise in-ManualSelection point.
                // The precise in-ManualSelection point is already in clampedPoint / message.Center.
                // FixationPoint is always non-null in the ManualSelection path (it is set before ManualSelection activation).
                // ManualSelectionRadius is logical/DIP (CircleRadiusPixels); ActionMenuWindow converts it to physical with ScaleX.
                ManualSelectionCenter = showBack ? _interactionManager.FixationPoint! : null,
                ManualSelectionRadius = showBack ? _settings.ManualSelection.CircleRadiusPixels : null,
                ResolvedControl = im.SelectionMode == SelectionMode.AutoSelection ? im.AutoSelectionTarget : null
            });
        });

    private void OnHideActionMenu() =>
        _dispatcher.BeginInvoke(() => _messenger.Send(new HideActionMenuMessage()));

    private void OnStartManualSelection(PhysicalPoint point) =>
        _dispatcher.BeginInvoke(() =>
        {
            // Get screen dimensions in physical pixels
            var physicalScreenW = DpiHelper.LogicalToPhysicalX(System.Windows.SystemParameters.PrimaryScreenWidth);
            var physicalScreenH = DpiHelper.LogicalToPhysicalY(System.Windows.SystemParameters.PrimaryScreenHeight);

            // Calculate display point using helper (moves coordinate logic to ViewModels layer)
            var displayPoint = ManualSelectionLayoutHelper.CalculateDisplayPoint(
                point,
                _settings.ViewPosition,
                physicalScreenW,
                physicalScreenH);

            _messenger.Send(new StartManualSelectionMessage(
                displayPoint,
                _settings.ManualSelection.CircleRadiusPixels,
                _settings.ManualSelection.MagnificationLevel,
                point  // GazePoint - always pass the original gaze point for capture
            ));
        });

    private void OnEndManualSelection() =>
        _dispatcher.BeginInvoke(() => _messenger.Send(new EndManualSelectionMessage()));

    private void OnHideManualSelection() =>
        _dispatcher.BeginInvoke(() => _messenger.Send(new ManualSelectionHiddenMessage()));

    private void OnStateChanged(InteractionState state)
    {
        _logger.LogInformation("State → {State}", state);
        _dispatcher.BeginInvoke(() => _messenger.Send(new StateChangedMessage(state)));
    }

    private void OnActionRequested(InteractionAction action, PhysicalPoint point)
    {
        var isPrecise = _interactionManager.CurrentState == InteractionState.ManualSelectionActionMenuVisible
                     || (_interactionManager.CurrentState == InteractionState.ActionMenuVisible
                         && _interactionManager.SelectionMode == SelectionMode.AutoSelection);
        _dispatcher.BeginInvoke(() => _messenger.Send(new ActionRequestedMessage(action, point, isPrecise)));
    }

    private async Task<ControlInfo?> ResolveTargetAsync(PhysicalPoint point)
    {
        try
        {
            return await Task.Run(() => _controlFinder.FindTarget(point))
                .WaitAsync(TimeSpan.FromMilliseconds(500));
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("ControlFinder timed out after 500ms — falling back to pixel");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ControlFinder failed — falling back to pixel");
            return null;
        }
    }

    public void Dispose()
    {
        _gazeEngine.CursorMoved -= OnCursorMoved;
        _interactionManager.ShowActivationButton -= OnShowActivationButton;
        _interactionManager.HideActivationButton -= OnHideActivationButton;
        _interactionManager.ShowActionMenu -= OnShowActionMenu;
        _interactionManager.HideActionMenu -= OnHideActionMenu;
        _interactionManager.StartManualSelection -= OnStartManualSelection;
        _interactionManager.EndManualSelection -= OnEndManualSelection;
        _interactionManager.HideManualSelection -= OnHideManualSelection;
        _interactionManager.StateChanged -= OnStateChanged;
        _interactionManager.ActionRequested -= OnActionRequested;
        GC.SuppressFinalize(this);
    }
}
