namespace EyeCtrl.App.Services;

using System;
using System.ComponentModel;
using System.IO;
using System.Windows.Interop;
using EyeCtrl.App;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services.Coordinators;
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
    // Inset (logical px) from the right screen edge for drag/manual-selection edge buttons.
    private const double RightEdgeInsetLogical = 30;

    private EventCoordinationContext? _context;
    // Shared modal flags — read on the gaze thread (SetupGazeRouting's early-out), written on the UI
    // thread. The volatility lives on ModalInteractionState itself; this single object is shared with
    // the keyboard/settings sub-coordinators (T5/T7) so there is no stale-flag window.
    private readonly ModalInteractionState _modal = new();
    private volatile bool _toolbarInitialized;
    private SettingsWindow? _settingsWindow;
    private bool _disposed;
    private Action<PhysicalPoint, long>? _gazeMovedHandler;
    private readonly PredictionStackFactory _predictionFactory = new();
    private KeyboardStackSnapshot? _keyboardSnapshot;
    private ToolbarSnapshot? _toolbarSnapshot;
    private GlassBackdropWindow? _glassBackdrop;
    private string? _appDataDir;
    private ModelRegistryService? _modelRegistry;
    private ModelDownloadService? _downloadService;
    private ModelManagementService? _modelManagementService;
    private AppRestartCoordinator? _restartCoordinator;
    private ScrollModeController? _scrollModeController;
    private ToolbarLifecycleCoordinator? _toolbar;
    private QuickActionsCoordinator? _quickActions;
    private DragCoordinator? _dragCoordinator;
    private InputActionDispatcher? _inputDispatcher;

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

    /// <summary>Hands the restart coordinator to the settings window at construction. Called once at startup.</summary>
    public void SetRestartCoordinator(AppRestartCoordinator coordinator) => _restartCoordinator = coordinator;

    /// <summary>Tray fallback: force-show the toolbar regardless of auto-hide state.</summary>
    public void ForceShowToolbar()
    {
        if (_context == null) return;
        _context.Dispatcher.BeginInvoke(() => _toolbar?.ForceShow());
    }

    public void Initialize(EventCoordinationContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EyeCtrl");

        _scrollModeController = new ScrollModeController(context);

        SetupPropertyChangedHandlers();
        SetupMessageHandlers();
        SetupGazeRouting();
        _toolbar = new ToolbarLifecycleCoordinator(_modal);
        _toolbar.Initialize(context);
        _quickActions = new QuickActionsCoordinator();
        _quickActions.Initialize(context);
        SetupBackdrop();
        _dragCoordinator = new DragCoordinator();
        _dragCoordinator.Initialize(context);
        _context.OverlayManager.Keyboard.SkinToneChanged += OnEmojiSkinToneChanged;

        UpdateTrackStatusVisibility();
        _context.Settings.TrackStatus.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_context.Settings.TrackStatus.Enabled))
                _context.Dispatcher.BeginInvoke(UpdateTrackStatusVisibility);
        };

        // Task Switcher: the VM receives TaskSwitcherToggledMessage itself (via IRecipient +
        // ObservableRecipient auto-register) and toggles its own IsVisible + tiles. The
        // coordinator only watches IsVisible to show/hide the fullscreen window, and handles
        // TaskSwitcherSelectionMessage to bring the chosen window to front.
        _context.TaskSwitcherVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_context.TaskSwitcherVM.IsVisible))
            {
                _context.Dispatcher.BeginInvoke(() =>
                {
                    if (_context.TaskSwitcherVM.IsVisible)
                        _context.OverlayManager.TaskSwitcher.ShowFullScreen();
                    else
                        _context.OverlayManager.TaskSwitcher.HideOverlay();
                });
            }
        };
        _inputDispatcher = new InputActionDispatcher();
        _inputDispatcher.Initialize(context);
    }

    private void UpdateTrackStatusVisibility()
    {
        if (_context == null) return;
        var trackStatusWindow = _context.OverlayManager.TrackStatus;
        if (_context.Settings.TrackStatus.Enabled)
            trackStatusWindow.StartTracking();
        else
            trackStatusWindow.StopTracking();
    }

    private void SetupPropertyChangedHandlers()
    {
        if (_context == null) return;

        var cursorCircle = _context.OverlayManager.CursorCircle;
        var activationButton = _context.OverlayManager.ActivationButton;
        var ManualSelectionOverlay = _context.OverlayManager.ManualSelectionOverlay;
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

        // ManualSelection: VM visibility -> edge bar show/hide
        _context.ManualSelectionVM.PropertyChanged += OnManualSelectionVisibilityChanged;

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
            _context.Dispatcher.BeginInvoke(() => SetKeyboardVisible(!_modal.KeyboardVisible, manageSuppression: true)));

        // Settings window
        _context.Messenger.Register<EventCoordinator, SettingsRequestedMessage>(this, (_, _) =>
            _context.Dispatcher.BeginInvoke(() => ShowSettingsWindow()));

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
            _context.Dispatcher.BeginInvoke(() => SetKeyboardVisible(true, manageSuppression: false));
            Log.Debug("Keyboard shown for text input (target handle: {Handle})", msg.TargetHandle);
        });

        // HideKeyboardRequestMessage -> hide keyboard
        _context.Messenger.Register<EventCoordinator, HideKeyboardRequestMessage>(this, (_, _) =>
        {
            if (_context?.OverlayManager.Keyboard == null) return;
            _context.Dispatcher.BeginInvoke(() => SetKeyboardVisible(false, manageSuppression: false));
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
                        // Deactivate toolbar dwell while gaze is being collected at calibration
                        // targets — they can land on screen edges/corners where a docked toolbar
                        // lives, and would otherwise dwell-activate a hidden/stale button position.
                        _toolbar?.SetActive(false);
                        // Hide the cursor circle so it doesn't compete with the calibration dot.
                        cursorCircle.Hide();
                        calWindow.UpdateVisualState(msg.State, msg.ProgressPercent);
                        calWindow.Show();
                        break;
                    case CalibrationState.Active:
                    case CalibrationState.Failed:
                        _context.GazeEngine.SuppressFixation = false;
                        // Re-activate toolbar dwell via the auto-hide controller, which is
                        // state-aware: it re-asserts a hide if the toolbar auto-hid while Feed was
                        // suppressed (calibration suppresses Feed on IsSessionActive, which flips
                        // false at CompleteSession — before the terminal CalibrationState arrives).
                        // NOTE: CalibrationState.Active means "calibration COMPLETE and transform
                        // applied" (success), NOT "in progress" — wiring it to SetActive(false)
                        // would permanently kill toolbar dwell on configs where ShowToolbar() never
                        // fires (floating toolbar / mouse provider — auto-hide disabled, no
                        // recovery short of restart). ReassertVisibility preserves that: when
                        // auto-hide is disabled (_enabled == false) the else-branch activates.
                        _toolbar?.ReassertVisibility();
                        // Restore the cursor circle so the user can aim at Retry/Accept.
                        if (_context.CursorVM.IsVisible) cursorCircle.Show();
                        calWindow.UpdateVisualState(msg.State, msg.ProgressPercent);
                        calWindow.Show();
                        break;
                    default:
                        // CalibrationState.Idle / CalibrationState.Cancelled — calibration is over
                        // (not in progress), so toolbar dwell resumes — but only if the toolbar
                        // hasn't since auto-hidden (same state-aware path as the Active/Failed case).
                        _context.GazeEngine.SuppressFixation = false;
                        _toolbar?.ReassertVisibility();
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
        {
            // Perf: skip the per-sample UI-thread dispatch when nothing consumes it. The toolbar
            // only needs the first sample (to lazily init its target positions); after that its
            // dwell is driven by GazeCursorMovedMessage on the messenger, not this route. The
            // keyboard and settings window need gaze only while open. Flags are volatile and read
            // on the gaze thread; a one-frame-stale read at worst delays a dispatch by a frame.
            if (_toolbarInitialized && !_modal.KeyboardVisible && !_modal.SettingsOpen)
                return;

            _context.Dispatcher.BeginInvoke(() =>
            {
                toolbar.UpdateGaze(point, timestamp);
                _toolbarInitialized = true;

                if (_modal.KeyboardVisible && keyboard.IsVisible)
                    keyboard.UpdateGaze(point, timestamp);

                if (_settingsWindow?.IsVisible ?? false)
                    _settingsWindow.UpdateGaze(point, timestamp);
            });
        };

        _context.GazeEngine.CursorMoved += _gazeMovedHandler;
    }

    private void SetupBackdrop()
    {
        if (_context == null)
            return;
        _glassBackdrop = _context.OverlayManager.GlassBackdrop;
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
                if (_context.CursorVM.IsVisible && !_modal.SettingsOpen) cursorCircle.Show();
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
                ManualSelectionOverlay.Show(_context.ManualSelectionVM.Center, _context.ManualSelectionVM.Radius, _context.ManualSelectionVM.Magnification, _context.ManualSelectionVM.GazePoint, _context.ManualSelectionVM.Recapture);
                ManualSelectionIndicator.Show();
                // Keep the gaze dot above the opaque magnifier: the Show() above is a no-op if the
                // indicator's own VM already showed it before the magnifier, leaving it hidden
                // underneath. Force it back to the top of the top-most band.
                ManualSelectionIndicator.RaiseToTop();
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

    private void OnManualSelectionVisibilityChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_context == null) return;

        if (e.PropertyName == nameof(ManualSelectionOverlayViewModel.IsVisible))
        {
            if (_context.ManualSelectionVM.IsVisible)
            {
                var anchor = ScreenGeometry.RightEdgeAnchor(RightEdgeInsetLogical, UIConstants.ButtonRadius);
                var dwell = _context.Settings.Dwell.ManualSelectionSideButtonDwellMs;
                // Match the former side-button layout: full-size buttons (UIConstants.ButtonRadius)
                // separated so their edges are ManualSelectionSideButtonGap apart, centred on the anchor.
                var radius = UIConstants.ButtonRadius;
                var offset = DpiHelper.LogicalToPhysicalY(radius + UIConstants.ManualSelectionSideButtonGap / 2);
                var defs = new[]
                {
                    new EdgeButtonDefinition("ManualSelection_close", "\u2715", dwell,
                        m => m.Send(new ManualSelectionCloseMessage()), VerticalOffset: -offset,
                        RadiusLogical: radius),
                    new EdgeButtonDefinition("ManualSelection_recapture", "\u21BB", dwell,
                        m => m.Send(new ManualSelectionRecaptureMessage()), VerticalOffset: offset,
                        RadiusLogical: radius),
                };
                _context.OverlayManager.EdgeButtonBar.ShowButtons(defs, anchor);
            }
            else
            {
                _context.OverlayManager.EdgeButtonBar.Hide();
            }
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

        _modal.SettingsOpen = true;
        _toolbar?.SetAutoHideSuspended(true);
        _quickActions?.SetSummonSuspended(true);
        _context.OverlayManager.CursorCircle.Hide();

        try
        {
            // Create SettingsViewModels
            var settingsVM = new SettingsViewModel(
                _context.Settings,
                _context.SettingsService,
                _context.Messenger);

            var quickActionsEditor = new SettingsQuickActionsEditorViewModel(
                _context.QuickActionsStore, _context.InstalledAppProvider);
            quickActionsEditor.Load();

            var settingsWindowVM = new SettingsWindowViewModel(
                settingsVM,
                quickActionsEditor,
                _context.Messenger,
                _context.Settings.Dwell);

            _settingsWindow = new SettingsWindow(
                settingsWindowVM,
                _context.Settings,
                _context.SettingsService,
                _modelRegistry ?? throw new InvalidOperationException("Model services not initialized. Call SetModelServices() before ShowSettingsWindow()."),
                _modelManagementService ?? throw new InvalidOperationException("Model services not initialized. Call SetModelServices() before ShowSettingsWindow()."),
                _context.Messenger,
                _context.CalibrationService,
                _restartCoordinator ?? throw new InvalidOperationException(
                    "Restart coordinator not set; SetRestartCoordinator must be called at startup."));

            _settingsWindow.SettingsChanged += OnSettingsChanged;
            _settingsWindow.Closed += OnSettingsWindowClosed;
            _settingsWindow.Show();
        }
        catch
        {
            _modal.SettingsOpen = false;
            // Window construction threw before Show() — OnSettingsWindowClosed will never fire, so
            // undo the suspend we set alongside _modal.SettingsOpen = true above.
            _toolbar?.SetAutoHideSuspended(false);
            _quickActions?.SetSummonSuspended(false);
            if (_context.CursorVM.IsVisible)
                _context.OverlayManager.CursorCircle.Show();
            throw;
        }
    }

    private void OnSettingsChanged(EyeCtrlSettings s)
    {
        if (_context == null) return;

        _context.OverlayManager.CursorCircle.UpdateAppearance(s.Cursor);
        _context.InteractionManager.SelectionMode = s.SelectionMode;
        _context.OverlayManager.Toolbar.SetSelectionModeState(s.SelectionMode);
        _context.Messenger.Send(new SelectionModeChangedMessage(s.SelectionMode));

        var toolbarSnapshot = ToolbarSnapshot.Capture(s);
        if (_toolbarSnapshot != toolbarSnapshot)
        {
            _context.OverlayManager.Toolbar.Initialize(s.Toolbar.Size, s.Dwell.ToolbarButtonDwellMs);
            _context.OverlayManager.Toolbar.SetMode(s.Toolbar.IsDocked, s.Toolbar.DockedEdge);

            // Re-evaluate enablement (IsDocked may have flipped to false → disable + force-show),
            // refresh the cached edge, and reposition the pause indicator along the new edge.
            bool nowEnabled = EyeCtrl.App.App.ActiveProviderType != Core.Enums.GazeProviderType.Mouse && s.Toolbar.IsDocked;
            _toolbar?.UpdateEnabled(nowEnabled);
            _toolbar?.UpdateEdge(s.Toolbar.DockedEdge);
            _toolbar?.PositionIndicator(s.Toolbar.DockedEdge);
            _toolbar?.RefreshToolbarRect();

            _toolbarSnapshot = toolbarSnapshot;
        }

        var keyboardSnapshot = KeyboardStackSnapshot.Capture(s);
        if (_keyboardSnapshot != keyboardSnapshot)
        {
            LoadKeyboard(_context.OverlayManager.Keyboard, s, _context.InputSimulator, _context.SettingsService);
        }

        Log.Information("Settings saved");
    }

    private void OnSettingsWindowClosed(object? sender, EventArgs e)
    {
        if (_context == null) return;

        _settingsWindow = null;
        _modal.SettingsOpen = false;
        _toolbar?.SetAutoHideSuspended(false);
        _quickActions?.SetSummonSuspended(false);
        if (_context.CursorVM.IsVisible)
            _context.OverlayManager.CursorCircle.Show();
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

        _keyboardSnapshot = KeyboardStackSnapshot.Capture(settings);
    }

    // manageSuppression: true for the toolbar toggle (deliberately pauses fixation while the
    // keyboard is up); false for the text-field auto-show/hide request path, which must NOT touch
    // SuppressFixation (it may be owned elsewhere, e.g. note editing, and typing shouldn't pause
    // fixation for the rest of the app).
    private void SetKeyboardVisible(bool visible, bool manageSuppression)
    {
        if (_context == null) return;

        _toolbar?.SetAutoHideSuspended(visible);
        _quickActions?.SetSummonSuspended(visible);
        _modal.KeyboardVisible = visible;
        Log.Information("Keyboard {State}", visible ? "shown" : "hidden");

        if (visible)
        {
            _context.OverlayManager.Keyboard.Show();
            if (manageSuppression)
            {
                _context.GazeEngine.SuppressFixation = true;
                _context.InteractionManager.OnFixationEnded();
            }
            _context.Messenger.Send(new ShowKeyboardMessage());
        }
        else
        {
            _context.OverlayManager.Keyboard.Hide();
            if (manageSuppression)
                _context.GazeEngine.SuppressFixation = false;
            _context.Messenger.Send(new HideKeyboardMessage());
        }
    }

    private void OnEmojiSkinToneChanged(int tone)
    {
        if (_context == null) return;
        _context.Settings.Keyboard.EmojiSkinTone = tone;
        _context.SettingsService.SaveDebounced(_context.Settings);
        _keyboardSnapshot = KeyboardStackSnapshot.Capture(_context.Settings);
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;

        // The prediction factory is field-initialized (never null) and its Dispose is idempotent;
        // dispose it regardless of whether Initialize ran — a constructed-but-uninitialized
        // coordinator (_context == null) must still release it rather than skip disposal.
        _predictionFactory.Dispose();

        // Dispose the toolbar sub-coordinator (handles its own gaze-handler unsubscribe + message
        // cleanup, including the StateChangedMessage registration that moved there in cluster 6).
        _toolbar?.Dispose();
        _toolbar = null;

        // Dispose the quick-actions sub-coordinator (handles its own event/message cleanup).
        _quickActions?.Dispose();
        _quickActions = null;

        if (_context == null)
        {
            _disposed = true;
            return;
        }

        // Unsubscribe from PropertyChanged events
        _context.CursorVM.PropertyChanged -= OnCursorVMPropertyChanged;
        _context.ActivationVM.PropertyChanged -= OnActivationVMPropertyChanged;
        _context.ManualSelectionVM.PropertyChanged -= OnManualSelectionVMPropertyChanged;
        _context.ManualSelectionVM.PropertyChanged -= OnManualSelectionVisibilityChanged;
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

        // Dispose the drag sub-coordinator (handles its own event/message cleanup).
        _dragCoordinator?.Dispose();

        // Dispose the input-action sub-coordinator (handles its own message cleanup).
        _inputDispatcher?.Dispose();

        // Unregister all message handlers
        _context.Messenger.UnregisterAll(this);

        _context.OverlayManager.Keyboard.SkinToneChanged -= OnEmojiSkinToneChanged;

        _scrollModeController?.Dispose();

        _glassBackdrop?.Hide();
        _glassBackdrop = null;

        _settingsWindow = null;
        _disposed = true;
    }
}
