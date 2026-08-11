namespace EyeCtrl.App.Services;

using System;
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
    private EventCoordinationContext? _context;
    // Shared modal flags — read on the gaze thread (SetupGazeRouting's early-out), written on the UI
    // thread. The volatility lives on ModalInteractionState itself; this single object is shared with
    // the keyboard/settings sub-coordinators (T5/T7) so there is no stale-flag window.
    private readonly ModalInteractionState _modal = new();
    private volatile bool _toolbarInitialized;
    private SettingsWindow? _settingsWindow;
    private bool _disposed;
    private Action<PhysicalPoint, long>? _gazeMovedHandler;
    private ToolbarSnapshot? _toolbarSnapshot;
    private ModelRegistryService? _modelRegistry;
    private ModelManagementService? _modelManagementService;
    private AppRestartCoordinator? _restartCoordinator;
    private ScrollModeController? _scrollModeController;
    private ToolbarLifecycleCoordinator? _toolbar;
    private QuickActionsCoordinator? _quickActions;
    private DragCoordinator? _dragCoordinator;
    private InputActionDispatcher? _inputDispatcher;
    private KeyboardCoordinator? _keyboard;
    private OverlayBindingCoordinator? _overlay;

    /// <summary>
    /// Gets the word predictor instance used for keyboard predictions, if enabled.
    /// </summary>
    public WordPredictor? WordPredictor => _keyboard?.WordPredictor;
    public PredictionOrchestrator? Orchestrator => _keyboard?.Orchestrator;

    public void SetModelServices(ModelRegistryService modelRegistry, ModelDownloadService modelDownload, ModelManagementService modelManagement)
    {
        // EC keeps the 3-arg public signature (App.xaml.cs calls it with 3 args). The download
        // service is dead and dropped at the seam; only the registry reaches KeyboardCoordinator.
        _modelRegistry = modelRegistry;
        _modelManagementService = modelManagement;
        _keyboard?.SetModelServices(modelRegistry, modelManagement);
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

        _scrollModeController = new ScrollModeController(context);

        SetupMessageHandlers();
        SetupGazeRouting();
        _toolbar = new ToolbarLifecycleCoordinator(_modal);
        _toolbar.Initialize(context);
        _overlay = new OverlayBindingCoordinator(_modal, _toolbar);
        _overlay.Initialize(context);
        _quickActions = new QuickActionsCoordinator();
        _quickActions.Initialize(context);
        _keyboard = new KeyboardCoordinator(_modal, _quickActions, _toolbar);
        _keyboard.Initialize(context);
        _dragCoordinator = new DragCoordinator();
        _dragCoordinator.Initialize(context);

        UpdateTrackStatusVisibility();
        _context.Settings.TrackStatus.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_context.Settings.TrackStatus.Enabled))
                _context.Dispatcher.BeginInvoke(UpdateTrackStatusVisibility);
        };

        // Task Switcher visibility binding + the overlay VM PropertyChanged handlers moved to
        // OverlayBindingCoordinator (cluster 1) — they are pure VM→window glue.

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

    private void SetupMessageHandlers()
    {
        if (_context == null) return;

        // ManualSelection / action-menu / backdrop-dismiss / calibration registrations moved to
        // OverlayBindingCoordinator (cluster 1) — they are overlay glue.

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

        // ShowKeyboardRequestMessage / HideKeyboardRequestMessage moved to KeyboardCoordinator (cluster 3).
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

        // The keyboard snapshot compare + reload now lives on KeyboardCoordinator (cluster 3),
        // exposed as the ReloadIfChanged seam that SettingsUiCoordinator (T7) will consume.
        _keyboard?.ReloadIfChanged(s, _context.InputSimulator, _context.SettingsService);

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
        // Keep the four argument-null guards: LoadKeyboard_ThrowsArgumentNullException_WhenKeyboardIsNull
        // calls this on an UNINITIALIZED EventCoordinator and expects ArgumentNullException —
        // without these guards, the forward would dereference _keyboard (null pre-Initialize) and
        // throw NullReferenceException instead. The _appDataDir InvalidOperationException guard moved
        // INTO KeyboardCoordinator.LoadKeyboard (the field moved with it).
        ArgumentNullException.ThrowIfNull(keyboard);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(inputSimulator);
        ArgumentNullException.ThrowIfNull(settingsService);

        _keyboard!.LoadKeyboard(keyboard, settings, inputSimulator, settingsService);
    }

    // SetKeyboardVisible / OnEmojiSkinToneChanged moved to KeyboardCoordinator (cluster 3) —
    // they own the _modal.KeyboardVisible write, the toolbar/quick-actions suspend calls, and the
    // _keyboardSnapshot refresh.

    #endregion

    public void Dispose()
    {
        if (_disposed) return;

        // Dispose the keyboard sub-coordinator (handles its own SkinToneChanged unsubscribe,
        // message cleanup, and PredictionStackFactory disposal — the factory moved here in cluster 3).
        _keyboard?.Dispose();
        _keyboard = null;

        // Dispose the toolbar sub-coordinator (handles its own gaze-handler unsubscribe + message
        // cleanup, including the StateChangedMessage registration that moved there in cluster 6).
        _toolbar?.Dispose();
        _toolbar = null;

        // Dispose the quick-actions sub-coordinator (handles its own event/message cleanup).
        _quickActions?.Dispose();
        _quickActions = null;

        // Dispose the overlay-binding sub-coordinator (handles its own VM PropertyChanged
        // unsubscribes + TaskSwitcherVM, message cleanup, and glass backdrop hide — the bindings
        // and backdrop/cleanup moved there in cluster 1).
        _overlay?.Dispose();
        _overlay = null;

        if (_context == null)
        {
            _disposed = true;
            return;
        }

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

        // SkinToneChanged -= moved to KeyboardCoordinator.Dispose (cluster 3).

        _scrollModeController?.Dispose();

        // _glassBackdrop cleanup moved to OverlayBindingCoordinator.Dispose (cluster 1).

        _settingsWindow = null;
        _disposed = true;
    }
}
