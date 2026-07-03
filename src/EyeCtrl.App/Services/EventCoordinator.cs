namespace EyeCtrl.App.Services;

using System;
using System.ComponentModel;
using System.IO;
using System.Windows.Interop;
using EyeCtrl.App;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Views;
using EyeCtrl.Platform.Input;
using EyeCtrl.Core;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;

/// <summary>
/// Coordinates event handlers between ViewModels, overlay windows, and the core engine.
/// Extracted from App.xaml.cs to improve Single Responsibility Principle compliance.
/// </summary>
public class EventCoordinator : IEventCoordinator
{
    private const int ActivationButtonOffsetPixels = 55;
    private const int BackspaceVirtualKeyCode = 0x08;
    private const int EnterVirtualKeyCode = 0x0D;
    private const int TabVirtualKeyCode = 0x09;
    private const ushort SpaceVirtualKeyCode = 0x20;
    private const int DefaultScrollAmount = 3;

    private EventCoordinationContext? _context;
    private bool _keyboardVisible;
    private bool _settingsOpen;
    private SettingsWindow? _settingsWindow;
    private bool _disposed;
    private Action<PhysicalPoint, long>? _gazeMovedHandler;
    private readonly PredictionStackFactory _predictionFactory = new();
    private GlassBackdropWindow? _glassBackdrop;
    private string? _appDataDir;
    private ModelRegistryService? _modelRegistry;
    private ModelDownloadService? _downloadService;
    private ModelManagementService? _modelManagementService;
    private ScrollOverlayViewModel? _scrollOverlayVm;
    private ScrollOverlayWindow? _scrollOverlayWindow;
    private ScrollWorker? _scrollWorker;
    private bool _scrollModeActive;
    private DragTrailOverlay? _dragTrailOverlay;
    private PhysicalPoint _dragGrabOrigin;
    private Action? _disposeEscapeHook;

    /// <summary>
    /// Gets the word predictor instance used for keyboard predictions, if enabled.
    /// </summary>
    public WordPredictor? WordPredictor => _predictionFactory.Current?.WordPredictor;
    public PredictionOrchestrator? Orchestrator => _predictionFactory.Current;

    public void SetModelServices(ModelRegistryService modelRegistry, ModelDownloadService modelDownload, ModelManagementService modelManagement)
    {
        _modelRegistry = modelRegistry;
        _downloadService = modelDownload;
        _modelManagementService = modelManagement;
    }

    public void Initialize(EventCoordinationContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EyeCtrl");

        SetupPropertyChangedHandlers();
        SetupMessageHandlers();
        SetupGazeRouting();
        SetupBackdrop();
        SetupDragHandlers();
    }

    private void SetupPropertyChangedHandlers()
    {
        if (_context == null) return;

        var cursorCircle = _context.OverlayManager.CursorCircle;
        var activationButton = _context.OverlayManager.ActivationButton;
        var ManualSelectionOverlay = _context.OverlayManager.ManualSelectionOverlay;
        var ManualSelectionSideButtons = _context.OverlayManager.ManualSelectionSideButtons;
        var ManualSelectionIndicator = _context.OverlayManager.ManualSelectionIndicator;
        var actionMenu = _context.OverlayManager.ActionMenu;
        var toolbar = _context.OverlayManager.Toolbar;

        // CursorCircle: VM.Position -> window position, VM.IsVisible -> show/hide
        _context.CursorVM.PropertyChanged += OnCursorVMPropertyChanged;

        // ActivationButton: VM -> show/hide/dwell
        _context.ActivationVM.PropertyChanged += OnActivationVMPropertyChanged;

        // ManualSelectionOverlay: VM -> show/hide/reposition
        _context.ManualSelectionVM.PropertyChanged += OnManualSelectionVMPropertyChanged;

        // ManualSelectionGazeIndicator: VM property changes -> window updates
        _context.ManualSelectionIndicatorVM.PropertyChanged += OnManualSelectionIndicatorVMPropertyChanged;

        // ManualSelectionSideButtons: VM -> show/hide/progress
        _context.ManualSelectionVM.PropertyChanged += OnManualSelectionSideButtonsPropertyChanged;

        // ActionMenu: VM -> show/hide
        _context.ActionMenuVM.PropertyChanged += OnActionMenuVMPropertyChanged;

        // Toolbar: VM state changes -> toolbar state
        _context.ToolbarVM.PropertyChanged += OnToolbarVMPropertyChanged;

        // GlassBackdrop: VM progress -> backdrop window update
        _context.BackdropVM.PropertyChanged += OnBackdropVMPropertyChanged;
    }

    private void SetupMessageHandlers()
    {
        if (_context == null) return;

        var im = _context.InteractionManager;
        var ManualSelectionOverlay = _context.OverlayManager.ManualSelectionOverlay;
        var ManualSelectionIndicator = _context.OverlayManager.ManualSelectionIndicator;
        var keyboard = _context.OverlayManager.Keyboard;

        im.StateChanged += OnInteractionStateChanged;

        // ManualSelectionDwellCompleted: Handle coordinate conversion
        _context.Messenger.Register<EventCoordinator, ManualSelectionDwellCompletedMessage>(this, (_, msg) =>
            _context.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    ManualSelectionIndicator.Hide();
                    var realPoint = ManualSelectionOverlay.GetRealPhysicalPoint(msg.PrecisePoint);
                    im.OnManualSelectionDwell(realPoint);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error handling ManualSelection dwell completion");
                }
            }));

        // ResetManualSelectionIndicator: Trigger animation
        _context.Messenger.Register<EventCoordinator, ResetManualSelectionIndicatorMessage>(this, (_, msg) =>
            _context.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (!ManualSelectionIndicator.IsVisible) return;
                    var targetDiameter = _context.Settings.ManualSelection.GazeIndicatorOuterDiameter;
                    ManualSelectionIndicator.AnimateReset(targetDiameter, _context.ManualSelectionIndicatorVM.ResetAnimationMs);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error animating ManualSelection indicator reset");
                }
            }));

        // ManualSelectionCloseMessage -> exit ManualSelection mode
        _context.Messenger.Register<EventCoordinator, ManualSelectionCloseMessage>(this, (_, _) =>
            _context.Dispatcher.BeginInvoke(() => im.OnManualSelectionBack()));

        // ManualSelectionRecaptureMessage -> re-capture at same fixation point
        _context.Messenger.Register<EventCoordinator, ManualSelectionRecaptureMessage>(this, (_, _) =>
            _context.Dispatcher.BeginInvoke(() =>
            {
                if (im.CurrentState == InteractionState.ManualSelectionActive && im.FixationPoint != null)
                {
                    HideBackdrop();
                    _context.Messenger.Send(new StartManualSelectionMessage(
                        im.FixationPoint.Value,
                        _context.Settings.ManualSelection.CircleRadiusPixels,
                        _context.Settings.ManualSelection.MagnificationLevel,
                        im.FixationPoint.Value));
                }
            }));

        // Keyboard toggle
        _context.Messenger.Register<EventCoordinator, KeyboardToggledMessage>(this, (_, _) =>
            _context.Dispatcher.BeginInvoke(() =>
            {
                _keyboardVisible = !_keyboardVisible;
                Log.Information("Keyboard {State}", _keyboardVisible ? "shown" : "hidden");
                if (_keyboardVisible)
                {
                    keyboard.Show();
                    _context.GazeEngine.SuppressFixation = true;
                    im.OnFixationEnded();
                }
                else
                {
                    keyboard.Hide();
                    _context.GazeEngine.SuppressFixation = false;
                }
            }));

        // Settings window
        _context.Messenger.Register<EventCoordinator, SettingsRequestedMessage>(this, (_, _) =>
            _context.Dispatcher.BeginInvoke(() => ShowSettingsWindow()));

        // Key activation -> input simulator
        _context.Messenger.Register<EventCoordinator, KeyActivatedMessage>(this, (_, msg) =>
            _context.Dispatcher.BeginInvoke(() => HandleKeyActivation(msg.Key, msg.Modifiers)));

        // Prediction -> input simulator
        _context.Messenger.Register<EventCoordinator, PredictionSelectedMessage>(this, (_, msg) =>
            _context.Dispatcher.BeginInvoke(() => HandlePredictionSelected(msg.DeleteCount, msg.Word)));

        // Special key -> input simulator (function keys, navigation keys)
        _context.Messenger.Register<EventCoordinator, SpecialKeyMessage>(this, (_, msg) =>
            _context.Dispatcher.BeginInvoke(() => HandleSpecialKey(msg.KeyName, msg.Modifiers)));

        // Action requested -> input simulator
        _context.Messenger.Register<EventCoordinator, ActionRequestedMessage>(this, (_, msg) =>
            _context.Dispatcher.BeginInvoke(() => HandleActionRequested(msg)));

        // CloseSettings -> close settings window
        _context.Messenger.Register<EventCoordinator, CloseSettingsMessage>(this, (_, _) =>
            _context.Dispatcher.BeginInvoke(() =>
            {
                if (_settingsWindow != null)
                {
                    _settingsWindow.Close();
                }
            }));

        // ShowActionMenu -> highlight overlay if auto-selection target resolved
        _context.Messenger.Register<EventCoordinator, ShowActionMenuMessage>(this, (_, msg) =>
            _context.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (msg.ResolvedControl == null || _context == null) return;
                    var highlightVM = _context.TargetHighlightVM;
                    highlightVM.IsVisible = true;
                    _context.OverlayManager.TargetHighlight.Configure(
                        _context.Settings.AutoSelection.HighlightColor,
                        _context.Settings.AutoSelection.HighlightThickness,
                        _context.Settings.AutoSelection.HighlightOpacity);
                    _context.OverlayManager.TargetHighlight.PositionAt(msg.ResolvedControl.BoundingRect);
                    _context.OverlayManager.TargetHighlight.ShowHighlight();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error showing target highlight overlay");
                }
            }));

        // HideActionMenu -> hide highlight overlay
        _context.Messenger.Register<EventCoordinator, HideActionMenuMessage>(this, (_, _) =>
            _context.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (_context != null && _context.TargetHighlightVM.IsVisible)
                    {
                        _context.TargetHighlightVM.IsVisible = false;
                        _context.OverlayManager.TargetHighlight.HideHighlight();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error hiding target highlight overlay");
                }
            }));

        // BackdropDismissMessage -> hide backdrop, highlight, and navigate back
        _context.Messenger.Register<EventCoordinator, BackdropDismissMessage>(this, (_, _) =>
            _context.Dispatcher.BeginInvoke(() =>
            {
                var context = _context;
                if (context == null)
                    return;

                HideBackdrop();

                if (context.TargetHighlightVM.IsVisible)
                {
                    context.TargetHighlightVM.IsVisible = false;
                    context.OverlayManager.TargetHighlight.HideHighlight();
                }

                var im = context.InteractionManager;
                if (im.CurrentState == InteractionState.ActionMenuVisible)
                    im.OnActionMenuDismissed();
                else if (im.CurrentState == InteractionState.ManualSelectionActive)
                    im.OnManualSelectionBack();
            }));

        // ShowKeyboardRequestMessage -> show keyboard for text input
        _context.Messenger.Register<EventCoordinator, ShowKeyboardRequestMessage>(this, (_, msg) =>
        {
            if (_context?.OverlayManager.Keyboard == null) return;
            _keyboardVisible = true;
            _context.OverlayManager.Keyboard.Show();
            Log.Debug("Keyboard shown for text input (target handle: {Handle})", msg.TargetHandle);
        });

        // HideKeyboardRequestMessage -> hide keyboard
        _context.Messenger.Register<EventCoordinator, HideKeyboardRequestMessage>(this, (_, _) =>
        {
            if (_context?.OverlayManager.Keyboard == null) return;
            _keyboardVisible = false;
            _context.OverlayManager.Keyboard.Hide();
            Log.Debug("Keyboard hidden");
        });

        // Calibration: show/hide overlay on state change, suppress interaction during capture
        _context.Messenger.Register<EventCoordinator, CalibrationStateChangedMessage>(this, (_, msg) =>
            _context.Dispatcher.BeginInvoke(() =>
            {
                var calWindow = _context.OverlayManager.Calibration;
                var cursorCircle = _context.OverlayManager.CursorCircle;
                switch (msg.State)
                {
                    case CalibrationState.Collecting:
                    case CalibrationState.Training:
                        _context.GazeEngine.SuppressFixation = true;
                        // Hide the cursor circle so it doesn't compete with the calibration dot.
                        cursorCircle.Hide();
                        calWindow.UpdateVisualState(msg.State, msg.ProgressPercent);
                        calWindow.Show();
                        break;
                    case CalibrationState.Active:
                    case CalibrationState.Failed:
                        _context.GazeEngine.SuppressFixation = false;
                        // Restore the cursor circle so the user can aim at Retry/Accept.
                        if (_context.CursorVM.IsVisible) cursorCircle.Show();
                        calWindow.UpdateVisualState(msg.State, msg.ProgressPercent);
                        calWindow.Show();
                        break;
                    default:
                        _context.GazeEngine.SuppressFixation = false;
                        if (_context.CursorVM.IsVisible) cursorCircle.Show();
                        calWindow.DismissCalibration();
                        break;
                }
            }));
    }

    private void SetupGazeRouting()
    {
        if (_context == null) return;

        var toolbar = _context.OverlayManager.Toolbar;
        var keyboard = _context.OverlayManager.Keyboard;

        _gazeMovedHandler = (point, timestamp) =>
            _context.Dispatcher.BeginInvoke(() =>
            {
                toolbar.UpdateGaze(point, timestamp);

                if (_keyboardVisible && keyboard.IsVisible)
                    keyboard.UpdateGaze(point, timestamp);

                if (_settingsWindow?.IsVisible ?? false)
                    _settingsWindow.UpdateGaze(point, timestamp);
            });

        _context.GazeEngine.CursorMoved += _gazeMovedHandler;
    }

    private void SetupBackdrop()
    {
        if (_context == null)
            return;
        _glassBackdrop = _context.OverlayManager.GlassBackdrop;
    }

    private void SetupDragHandlers()
    {
        if (_context == null) return;

        _context.InteractionManager.DragStarted += OnDragStarted;
        _context.DragTrailVM.PropertyChanged += OnDragTrailVMPropertyChanged;

        _context.Messenger.Register<EventCoordinator, DragDropMessage>(this, (_, m) =>
            _context.Dispatcher.BeginInvoke(() => OnDragDrop(m)));
        _context.Messenger.Register<EventCoordinator, DragCancelMessage>(this, (_, _) =>
            _context.Dispatcher.BeginInvoke(OnDragCancel));
        _context.Messenger.Register<EventCoordinator, DragDismissZoomMessage>(this, (_, _) =>
            _context.Dispatcher.BeginInvoke(OnDragDismissZoom));

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
        _dragTrailOverlay.Show();

        var (cancelCenter, backCenter) = ComputeDragEdgeButtonCenters();
        _context.Messenger.Send(new StartDragTrailMessage(grabOrigin, cancelCenter, backCenter));
    }

    // The right-edge cancel/back buttons sit at a fixed X near the right screen edge, vertically
    // centered. Screen geometry lives in the App layer, so it's computed here and passed into the
    // ViewModel (which stays framework-agnostic) rather than the VM reading SystemParameters.
    private static (PhysicalPoint cancel, PhysicalPoint back) ComputeDragEdgeButtonCenters()
    {
        var screenWidth = DpiHelper.LogicalToPhysicalX(System.Windows.SystemParameters.PrimaryScreenWidth);
        var buttonX = screenWidth - DpiHelper.LogicalToPhysicalX(30);
        var centerY = DpiHelper.LogicalToPhysicalY(System.Windows.SystemParameters.PrimaryScreenHeight / 2.0);
        var cancel = new PhysicalPoint(buttonX, centerY + DpiHelper.LogicalToPhysicalY(30));
        var back = new PhysicalPoint(buttonX, centerY - DpiHelper.LogicalToPhysicalY(30));
        return (cancel, back);
    }

    private void OnDragDrop(DragDropMessage msg)
    {
        _context?.InputSimulator.MouseUp(msg.DropPoint);
        _context?.InteractionManager.CompleteDrag();
        CloseDragOverlay();
    }

    private void OnDragCancel()
    {
        _context?.InputSimulator.MouseUp(_dragGrabOrigin);
        _context?.InteractionManager.CompleteDrag();
        CloseDragOverlay();
    }

    private void OnDragDismissZoom()
    {
        if (_context?.OverlayManager.ManualSelectionOverlay.IsVisible == true)
            _context.OverlayManager.ManualSelectionOverlay.Hide();
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
            case nameof(DragTrailViewModel.ShowBack):
            case nameof(DragTrailViewModel.ReleaseButtonCenter):
                _dragTrailOverlay.Update(
                    vm.GrabOrigin, vm.CursorPosition, vm.SettleProgress,
                    vm.ShowBack, vm.ReleaseButtonCenter, vm.CancelButtonCenter, vm.BackButtonCenter);
                break;
        }
    }

    private void ShowBackdropIfEnabled()
    {
        if (_glassBackdrop == null || _context == null)
            return;
        var settings = _context.Settings.Backdrop;
        if (!settings.Enabled)
            return;

        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen == null)
            return;
        var dipRight = screen.Bounds.Width / DpiHelper.ScaleX - UIConstants.BackdropHideButtonOffset;
        var dipTop = UIConstants.BackdropHideButtonOffset;
        var hideButtonCenter = new PhysicalPoint(
            DpiHelper.LogicalToPhysicalX(dipRight),
            DpiHelper.LogicalToPhysicalY(dipTop));

        _glassBackdrop.Show();
        _glassBackdrop.ApplyBackdrop(settings);
        _glassBackdrop.DrawHideButton(hideButtonCenter);
        _context.Messenger.Send(new ShowBackdropMessage(hideButtonCenter));
    }

    private void HideBackdrop()
    {
        _glassBackdrop?.Hide();
        _context?.Messenger.Send(new HideBackdropMessage());
    }

    #region PropertyChanged Handlers

    private void OnCursorVMPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_context == null) return;

        var cursorCircle = _context.OverlayManager.CursorCircle;
        switch (e.PropertyName)
        {
            case nameof(CursorCircleViewModel.Position):
                cursorCircle.UpdatePosition(_context.CursorVM.Position);
                break;
            case nameof(CursorCircleViewModel.IsVisible):
                if (_context.CursorVM.IsVisible && !_settingsOpen) cursorCircle.Show();
                else cursorCircle.Hide();
                break;
        }
    }

    private void OnActivationVMPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_context == null) return;

        var activationButton = _context.OverlayManager.ActivationButton;
        var actionMenu = _context.OverlayManager.ActionMenu;

        switch (e.PropertyName)
        {
            case nameof(ActivationButtonViewModel.IsVisible):
                if (_context.ActivationVM.IsVisible)
                {
                    actionMenu.Hide();
                    var circleRadius = _context.Settings.Cursor.CircleSize / 2;
                    var buttonOffset = circleRadius + ActivationButtonOffsetPixels;
                    activationButton.Show();
                    activationButton.ShowAt(_context.ActivationVM.Position, buttonOffset);
                }
                else
                {
                    activationButton.HideImmediate();
                }
                break;
            case nameof(ActivationButtonViewModel.DwellProgress):
                activationButton.UpdateDwellProgress(_context.ActivationVM.DwellProgress);
                break;
        }
    }

    private void OnManualSelectionVMPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_context == null) return;

        var ManualSelectionOverlay = _context.OverlayManager.ManualSelectionOverlay;
        var cursorCircle = _context.OverlayManager.CursorCircle;
        var activationButton = _context.OverlayManager.ActivationButton;
        var ManualSelectionIndicator = _context.OverlayManager.ManualSelectionIndicator;

        if (e.PropertyName == nameof(ManualSelectionOverlayViewModel.Center) && _context.ManualSelectionVM.IsVisible)
        {
            ManualSelectionOverlay.UpdateCenter(_context.ManualSelectionVM.Center, _context.ManualSelectionVM.Radius, _context.ManualSelectionVM.Magnification);
        }
        else if (e.PropertyName == nameof(ManualSelectionOverlayViewModel.IsVisible))
        {
            if (_context.ManualSelectionVM.IsVisible)
            {
                cursorCircle.Hide();
                activationButton.HideImmediate();
                ShowBackdropIfEnabled();
                ManualSelectionOverlay.Show(_context.ManualSelectionVM.Center, _context.ManualSelectionVM.Radius, _context.ManualSelectionVM.Magnification, _context.ManualSelectionVM.GazePoint);
                ManualSelectionIndicator.Show();
            }
            else
            {
                ManualSelectionOverlay.Hide();
                ManualSelectionIndicator.Hide();
                HideBackdrop();
            }
        }
    }

    private void OnManualSelectionIndicatorVMPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_context == null) return;

        var ManualSelectionIndicator = _context.OverlayManager.ManualSelectionIndicator;
        switch (e.PropertyName)
        {
            case nameof(ManualSelectionGazeIndicatorViewModel.IndicatorX):
            case nameof(ManualSelectionGazeIndicatorViewModel.IndicatorY):
                ManualSelectionIndicator.UpdatePosition(_context.ManualSelectionIndicatorVM.IndicatorX, _context.ManualSelectionIndicatorVM.IndicatorY);
                break;
            case nameof(ManualSelectionGazeIndicatorViewModel.OuterCircleDiameter):
                ManualSelectionIndicator.UpdateOuterDiameter(_context.ManualSelectionIndicatorVM.OuterCircleDiameter);
                break;
            case nameof(ManualSelectionGazeIndicatorViewModel.IsVisible):
                if (_context.ManualSelectionIndicatorVM.IsVisible) ManualSelectionIndicator.Show();
                else ManualSelectionIndicator.Hide();
                break;
        }
    }

    private void OnManualSelectionSideButtonsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_context == null) return;

        var ManualSelectionSideButtons = _context.OverlayManager.ManualSelectionSideButtons;
        switch (e.PropertyName)
        {
            case nameof(ManualSelectionOverlayViewModel.IsVisible):
                if (_context.ManualSelectionVM.IsVisible)
                    ManualSelectionSideButtons.ShowAt(_context.ManualSelectionVM.CloseButtonCenter, _context.ManualSelectionVM.RecaptureButtonCenter);
                else
                    ManualSelectionSideButtons.Hide();
                break;
            case nameof(ManualSelectionOverlayViewModel.CloseButtonProgress):
                ManualSelectionSideButtons.UpdateProgress("ManualSelection_close", _context.ManualSelectionVM.CloseButtonProgress);
                break;
            case nameof(ManualSelectionOverlayViewModel.RecaptureButtonProgress):
                ManualSelectionSideButtons.UpdateProgress("ManualSelection_recapture", _context.ManualSelectionVM.RecaptureButtonProgress);
                break;
        }
    }

    private void OnActionMenuVMPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_context == null) return;

        var actionMenu = _context.OverlayManager.ActionMenu;
        var cursorCircle = _context.OverlayManager.CursorCircle;
        var activationButton = _context.OverlayManager.ActivationButton;

        if (e.PropertyName == nameof(ActionMenuViewModel.IsVisible))
        {
            if (_context.ActionMenuVM.IsVisible)
            {
                cursorCircle.Hide();
                activationButton.HideImmediate();

                ShowBackdropIfEnabled();
                actionMenu.Show(
                    _context.ActionMenuVM.Center,
                    _context.Settings.ActionMenu.VisibleActions,
                    _context.ActionMenuVM.ShowBack,
                    _context.ActionMenuVM.ShowBack ? _context.Settings.Dwell.ManualSelectionActionMenuDwellMs : _context.Settings.Dwell.ActionMenuDwellMs,
                    _context.ActionMenuVM.EffectiveRingRadius,
                    _context.Settings.Cursor.CircleSize,
                    _context.Settings.Cursor.CircleColor,
                    _context.Settings.Cursor.PositionIndicatorStyle,
                    _context.ActionMenuVM.ManualSelectionCenter,
                    _context.ActionMenuVM.ManualSelectionRadius,
                    _context.ActionMenuVM.GazePoint);
            }
            else
            {
                actionMenu.Hide();
                HideBackdrop();
            }
        }
    }

    private void OnToolbarVMPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_context == null) return;

        var toolbar = _context.OverlayManager.Toolbar;
        if (e.PropertyName == nameof(ToolbarViewModel.IsPaused))
        {
            toolbar.SetPausedState(_context.ToolbarVM.IsPaused);
        }
    }

    private void OnBackdropVMPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_glassBackdrop == null) return;
        if (e.PropertyName == nameof(GlassBackdropViewModel.HideButtonProgress))
            _glassBackdrop.UpdateHideButtonProgress(_context!.BackdropVM.HideButtonProgress);
    }

    #endregion

    #region Message Handlers

    private void ShowSettingsWindow()
    {
        if (_context == null || _settingsWindow != null) return;

        _settingsOpen = true;
        _context.OverlayManager.CursorCircle.Hide();

        try
        {
            // Create SettingsViewModels
            var settingsVM = new SettingsViewModel(
                _context.Settings,
                _context.SettingsService,
                _context.Messenger);

            var settingsWindowVM = new SettingsWindowViewModel(
                settingsVM,
                _context.Messenger,
                _context.Settings.Dwell);

            _settingsWindow = new SettingsWindow(
                settingsWindowVM,
                _context.Settings,
                _context.SettingsService,
                _modelRegistry ?? throw new InvalidOperationException("Model services not initialized. Call SetModelServices() before ShowSettingsWindow()."),
                _modelManagementService ?? throw new InvalidOperationException("Model services not initialized. Call SetModelServices() before ShowSettingsWindow()."),
                _context.Messenger,
                _context.CalibrationService);

            _settingsWindow.SettingsChanged += OnSettingsChanged;
            _settingsWindow.Closed += OnSettingsWindowClosed;
            _settingsWindow.Show();
        }
        catch
        {
            _settingsOpen = false;
            if (_context.CursorVM.IsVisible)
                _context.OverlayManager.CursorCircle.Show();
            throw;
        }
    }

    private void OnSettingsChanged(EyeCtrlSettings s)
    {
        if (_context == null) return;

        _context.OverlayManager.CursorCircle.UpdateAppearance(s.Cursor);
        _context.OverlayManager.Toolbar.Initialize(s.Toolbar.Size, s.Dwell.ToolbarButtonDwellMs);
        _context.OverlayManager.Toolbar.SetMode(s.Toolbar.IsDocked, s.Toolbar.DockedEdge);
        _context.InteractionManager.SelectionMode = s.SelectionMode;
        _context.OverlayManager.Toolbar.SetSelectionModeState(s.SelectionMode);
        _context.Messenger.Send(new SelectionModeChangedMessage(s.SelectionMode));
        LoadKeyboard(_context.OverlayManager.Keyboard, s, _context.InputSimulator, _context.SettingsService);
        Log.Information("Settings saved");
    }

    private void OnSettingsWindowClosed(object? sender, EventArgs e)
    {
        if (_context == null) return;

        _settingsWindow = null;
        _settingsOpen = false;
        if (_context.CursorVM.IsVisible)
            _context.OverlayManager.CursorCircle.Show();
    }

    private void HandleKeyActivation(string key, IReadOnlySet<ModifierKey>? modifiers)
    {
        if (_context == null) return;

        var hasSystemModifiers = modifiers != null &&
            (modifiers.Contains(ModifierKey.Ctrl) || modifiers.Contains(ModifierKey.Alt) || modifiers.Contains(ModifierKey.Win));
        var hasShift = modifiers != null && modifiers.Contains(ModifierKey.Shift);
        var isControlKey = key is "\b" or "\n" or "\t" or " ";

        // Use VK code path when system modifiers are active, or Shift + control keys (e.g., Shift+Tab)
        if (hasSystemModifiers || (hasShift && isControlKey))
        {
            // VK code path: wrap keystroke in modifier down/up
            ushort vk;
            IReadOnlyList<ModifierKey> requiredModifiers = Array.Empty<ModifierKey>();

            switch (key)
            {
                case "\b": vk = (ushort)BackspaceVirtualKeyCode; break;
                case "\n": vk = (ushort)EnterVirtualKeyCode; break;
                case "\t": vk = (ushort)TabVirtualKeyCode; break;
                case " ": vk = SpaceVirtualKeyCode; break;
                default:
                    if (key.Length == 1)
                    {
                        // Use lowercase for letters so CapsLock-induced uppercase
                        // doesn't trick VkKeyScanW into reporting Shift as required
                        var ch = char.IsLetter(key[0]) ? char.ToLowerInvariant(key[0]) : key[0];
                        (vk, requiredModifiers) = Win32InputSimulator.CharToVirtualKey(ch);
                    }
                    else
                        vk = 0;
                    break;
            }

            if (vk != 0)
            {
                // Merge any modifiers the character intrinsically requires
                // (e.g., '!' needs Shift, AltGr chars need Ctrl+Alt)
                if (requiredModifiers.Count > 0)
                {
                    var merged = new HashSet<ModifierKey>(modifiers!);
                    foreach (var req in requiredModifiers)
                        merged.Add(req);
                    _context.InputSimulator.SendModifiedKeyPress(merged, vk);
                }
                else
                {
                    _context.InputSimulator.SendModifiedKeyPress(modifiers!, vk);
                }
            }
            else
            {
                // Character has no VK mapping (e.g., €, ©) — fall back to plain text
                Log.Warning("No VK mapping for key '{Key}' with system modifiers, falling back to plain text", key);
                _context.InputSimulator.SendText(key);
            }
        }
        else
        {
            // Simple path: no system modifiers, just send text or VK
            switch (key)
            {
                case "\b": _context.InputSimulator.SendKeyPress(BackspaceVirtualKeyCode); break;
                case "\n": _context.InputSimulator.SendKeyPress(EnterVirtualKeyCode); break;
                case "\t": _context.InputSimulator.SendKeyPress(TabVirtualKeyCode); break;
                default: _context.InputSimulator.SendText(key); break;
            }
        }
    }

    private void HandlePredictionSelected(int deleteCount, string word)
    {
        if (_context == null) return;

        for (int i = 0; i < deleteCount; i++)
            _context.InputSimulator.SendKeyPress(BackspaceVirtualKeyCode);
        _context.InputSimulator.SendText(word + " ");
    }

    private void HandleSpecialKey(string keyName, IReadOnlySet<ModifierKey>? modifiers)
    {
        if (_context == null) return;

        ushort vk = keyName.ToLowerInvariant() switch
        {
            "escape" => VirtualKeys.Escape,
            "f1" => VirtualKeys.F1,
            "f2" => VirtualKeys.F2,
            "f3" => VirtualKeys.F3,
            "f4" => VirtualKeys.F4,
            "f5" => VirtualKeys.F5,
            "f6" => VirtualKeys.F6,
            "f7" => VirtualKeys.F7,
            "f8" => VirtualKeys.F8,
            "f9" => VirtualKeys.F9,
            "f10" => VirtualKeys.F10,
            "f11" => VirtualKeys.F11,
            "f12" => VirtualKeys.F12,
            "home" => VirtualKeys.Home,
            "end" => VirtualKeys.End,
            "pageup" => VirtualKeys.Prior,
            "pagedown" => VirtualKeys.Next,
            "arrowup" => VirtualKeys.Up,
            "arrowdown" => VirtualKeys.Down,
            "arrowleft" => VirtualKeys.Left,
            "arrowright" => VirtualKeys.Right,
            "insert" => VirtualKeys.Insert,
            "delete" => VirtualKeys.Delete,
            "printscreen" => VirtualKeys.Snapshot,
            _ => 0
        };

        if (vk != 0)
        {
            // Arrow keys, Insert/Delete, Home/End, PageUp/PageDown, and PrintScreen require KEYEVENTF_EXTENDEDKEY
            bool isExtendedKey = vk is VirtualKeys.Up or VirtualKeys.Down or VirtualKeys.Left or VirtualKeys.Right
                or VirtualKeys.Home or VirtualKeys.End or VirtualKeys.Prior or VirtualKeys.Next
                or VirtualKeys.Insert or VirtualKeys.Delete or VirtualKeys.Snapshot;

            if (modifiers != null && modifiers.Count > 0)
                _context.InputSimulator.SendModifiedKeyPress(modifiers, vk, isExtendedKey);
            else
                _context.InputSimulator.SendKeyPress(vk, isExtendedKey);
        }
        else
        {
            Log.Warning("Unknown special key name: {KeyName}", keyName);
        }
    }

    private void HandleActionRequested(ActionRequestedMessage msg)
    {
        if (_context == null) return;

        var pt = msg.IsPreciseTarget ? msg.Point : (_context.GazeEngine.StableCentroid ?? msg.Point);
        Log.Information("Action: {Action} at ({X:F0}, {Y:F0}) [PreciseTarget={IsPreciseTarget}]",
            msg.Action, pt.X, pt.Y, msg.IsPreciseTarget);

        if (msg.Action == InteractionAction.PinNote)
        {
            if (_context is { NoteManager.IsAtCapacity: false })
                _context.NoteManager.CreateNoteAt(pt);

            if (_context is { TargetHighlightVM.IsVisible: true })
            {
                _context.TargetHighlightVM.IsVisible = false;
                _context.OverlayManager.TargetHighlight.HideHighlight();
            }
            return;
        }

        switch (msg.Action)
        {
            case InteractionAction.LeftClick:    _context.InputSimulator.LeftClick(pt);        break;
            case InteractionAction.RightClick:   _context.InputSimulator.RightClick(pt);       break;
            case InteractionAction.DoubleClick:  _context.InputSimulator.DoubleClick(pt);      break;

            case InteractionAction.ScrollUp:     _context.InputSimulator.Scroll(pt, 0, DefaultScrollAmount);    break;
            case InteractionAction.ScrollDown:   _context.InputSimulator.Scroll(pt, 0, -DefaultScrollAmount);   break;
            case InteractionAction.ScrollLeft:   _context.InputSimulator.Scroll(pt, -DefaultScrollAmount, 0);   break;
            case InteractionAction.ScrollRight:  _context.InputSimulator.Scroll(pt, DefaultScrollAmount, 0);    break;
        }

        if (_context.TargetHighlightVM.IsVisible)
        {
            _context.TargetHighlightVM.IsVisible = false;
            _context.OverlayManager.TargetHighlight.HideHighlight();
        }
    }

    public void LoadKeyboard(KeyboardWindow keyboard, EyeCtrlSettings settings,
        IInputSimulator inputSimulator, SettingsService settingsService)
    {
        ArgumentNullException.ThrowIfNull(keyboard);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(inputSimulator);
        ArgumentNullException.ThrowIfNull(settingsService);

        if (string.IsNullOrWhiteSpace(_appDataDir))
            throw new InvalidOperationException("EventCoordinator must be initialized before calling LoadKeyboard.");

        var layoutName = settings.Keyboard.ActiveLayout.ToLowerInvariant();
        var layoutPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            "Resources", "Layouts", $"{layoutName}.json");

        if (File.Exists(layoutPath))
        {
            var layout = KeyboardLayoutLoader.Load(layoutPath);
            keyboard.LoadLayout(layout,
                settings.Keyboard.KeySizeMultiplier,
                settings.Dwell.KeyboardKeyDwellMs,
                settings.Dwell.KeyboardPredictionDwellMs,
                settings.Dwell.DwellCooldownMs);

            var emojiPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Emojis", "emojis.json");
            var recentPath = Path.Combine(_appDataDir!, "recent_emojis.json");
            keyboard.SetEmojiData(emojiPath, recentPath);
            keyboard.SetSkinTone(settings.Keyboard.EmojiSkinTone);
        }

        if (settings.Keyboard.PredictionEnabled && string.IsNullOrEmpty(settings.Keyboard.LanguagePack))
        {
            settings.Keyboard.LanguagePack = LanguageDetector.Detect();
            settingsService.SaveDebounced(settings);
            Log.Information("Auto-detected language: {Lang}", settings.Keyboard.LanguagePack);
        }

        var orchestrator = _predictionFactory.Rebuild(settings, AppDomain.CurrentDomain.BaseDirectory, _appDataDir!, _modelRegistry);
        keyboard.SetOrchestrator(orchestrator);

        keyboard.SetScreenEdge(settings.Keyboard.ScreenEdge);
        keyboard.SetSettings(settings);

        var wavPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "click.wav");
        keyboard.SetSoundService(new KeyboardSoundService(settings.Keyboard, wavPath));
    }

    #endregion

    private void OnInteractionStateChanged(InteractionState state)
    {
        if (state == InteractionState.ScrollMode)
        {
            OnScrollModeEntered();
        }
        else if (_scrollModeActive)
        {
            OnScrollModeExited();
        }
    }

    private void OnScrollModeEntered()
    {
        if (_context == null) return;

        var anchor = _context.InteractionManager.ScrollAnchor!.Value;

        _context.InputSimulator.MoveCursor(anchor);

        // Hide the gaze-following cursor circle so it doesn't compete with the scroll overlay's
        // anchor-locked circle (the locked circle is the whole point of Option D — no feedback loop).
        _context.OverlayManager.CursorCircle.Hide();

        var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        var stopButtonCenter = new PhysicalPoint(
            bounds.Width - DpiHelper.LogicalToPhysicalX(50),
            bounds.Height / 2.0);

        _scrollOverlayVm = new ScrollOverlayViewModel(_context.Messenger, _context.Settings.Scroll, _context.Settings.Dwell);
        _scrollOverlayVm.EnterScrollMode(anchor, stopButtonCenter);
        _scrollOverlayVm.ExitRequested += OnScrollExitRequested;

        _scrollOverlayWindow = new ScrollOverlayWindow();
        _scrollOverlayWindow.Show(_scrollOverlayVm);

        _scrollWorker = new ScrollWorker(_context.InputSimulator);
        _scrollWorker.Start(anchor);

        _scrollOverlayVm.TargetVelocityChanged += OnScrollTargetVelocityChanged;
        _scrollOverlayVm.FeedbackChanged += OnScrollFeedbackChanged;

        _scrollModeActive = true;
    }

    // Defer the exit off the current call stack: ExitRequested fires synchronously from inside
    // ScrollOverlayViewModel.Receive (via the dwell detector), and tearing the VM down there would
    // dispose it mid-Receive. BeginInvoke runs the teardown on a fresh dispatcher turn instead.
    private void OnScrollExitRequested()
        => _context?.Dispatcher.BeginInvoke(() => _context?.InteractionManager.ExitScrollMode());

    private void OnScrollTargetVelocityChanged(double vx, double vy)
        => _scrollWorker?.UpdateTargetVelocity(vx, vy);

    private void OnScrollFeedbackChanged()
    {
        if (_scrollOverlayWindow == null || _scrollOverlayVm == null) return;
        _scrollOverlayWindow.UpdateFeedback(
            _scrollOverlayVm.AnchorDotOffsetX,
            _scrollOverlayVm.AnchorDotOffsetY,
            _scrollOverlayVm.VelocityMagnitude,
            _scrollOverlayVm.StopButtonProgress);
    }

    private void OnScrollModeExited()
    {
        _scrollWorker?.Stop();
        _scrollWorker?.Dispose();
        _scrollWorker = null;

        if (_scrollOverlayVm != null)
        {
            _scrollOverlayVm.TargetVelocityChanged -= OnScrollTargetVelocityChanged;
            _scrollOverlayVm.FeedbackChanged -= OnScrollFeedbackChanged;
            _scrollOverlayVm.ExitRequested -= OnScrollExitRequested;
            _scrollOverlayVm.ExitScrollMode();
            _scrollOverlayVm.Dispose();
            _scrollOverlayVm = null;
        }

        _scrollOverlayWindow?.Close();
        _scrollOverlayWindow = null;

        // Restore the gaze-following cursor circle (matches the calibration/settings restore pattern).
        if (_context != null && _context.CursorVM.IsVisible)
            _context.OverlayManager.CursorCircle.Show();

        _scrollModeActive = false;
    }

    public void Dispose()
    {
        if (_disposed || _context == null) return;

        // Unsubscribe from PropertyChanged events
        _context.CursorVM.PropertyChanged -= OnCursorVMPropertyChanged;
        _context.ActivationVM.PropertyChanged -= OnActivationVMPropertyChanged;
        _context.ManualSelectionVM.PropertyChanged -= OnManualSelectionVMPropertyChanged;
        _context.ManualSelectionVM.PropertyChanged -= OnManualSelectionSideButtonsPropertyChanged;
        _context.ManualSelectionIndicatorVM.PropertyChanged -= OnManualSelectionIndicatorVMPropertyChanged;
        _context.ActionMenuVM.PropertyChanged -= OnActionMenuVMPropertyChanged;
        _context.ToolbarVM.PropertyChanged -= OnToolbarVMPropertyChanged;
        _context.BackdropVM.PropertyChanged -= OnBackdropVMPropertyChanged;

        // Unsubscribe from GazeEngine events
        if (_gazeMovedHandler != null)
        {
            _context.GazeEngine.CursorMoved -= _gazeMovedHandler;
        }

        // Unsubscribe from settings window events if present
        if (_settingsWindow != null)
        {
            _settingsWindow.SettingsChanged -= OnSettingsChanged;
            _settingsWindow.Closed -= OnSettingsWindowClosed;
        }

        // Unsubscribe from drag handlers
        if (_context != null)
        {
            _context.InteractionManager.DragStarted -= OnDragStarted;
            _context.DragTrailVM.PropertyChanged -= OnDragTrailVMPropertyChanged;
        }
        _disposeEscapeHook?.Invoke();
        CloseDragOverlay();

        // Unregister all message handlers
        _context.Messenger.UnregisterAll(this);

        _context.InteractionManager.StateChanged -= OnInteractionStateChanged;

        if (_scrollModeActive) OnScrollModeExited();

        _predictionFactory.Dispose();

        _glassBackdrop?.Hide();
        _glassBackdrop = null;

        _settingsWindow = null;
        _disposed = true;
    }
}
