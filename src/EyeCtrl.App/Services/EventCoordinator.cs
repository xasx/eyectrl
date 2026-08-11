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
    private bool _disposed;
    private Action<PhysicalPoint, long>? _gazeMovedHandler;
    private ScrollModeController? _scrollModeController;
    private ToolbarLifecycleCoordinator? _toolbar;
    private QuickActionsCoordinator? _quickActions;
    private DragCoordinator? _dragCoordinator;
    private InputActionDispatcher? _inputDispatcher;
    private KeyboardCoordinator? _keyboard;
    private OverlayBindingCoordinator? _overlay;
    private SettingsUiCoordinator? _settingsUi;

    /// <summary>
    /// Gets the word predictor instance used for keyboard predictions, if enabled.
    /// </summary>
    public WordPredictor? WordPredictor => _keyboard?.WordPredictor;
    public PredictionOrchestrator? Orchestrator => _keyboard?.Orchestrator;

    public void SetModelServices(ModelRegistryService modelRegistry, ModelDownloadService modelDownload, ModelManagementService modelManagement)
    {
        // EC keeps the 3-arg public signature (App.xaml.cs calls it with 3 args). The download
        // service is dead and dropped at the seam; the registry+management are forwarded to both
        // KeyboardCoordinator (for the prediction-stack rebuild) and SettingsUiCoordinator (for the
        // settings-window construction).
        _keyboard?.SetModelServices(modelRegistry, modelManagement);
        _settingsUi?.SetModelServices(modelRegistry, modelManagement);
    }

    /// <summary>Hands the restart coordinator to the settings window at construction. Called once at startup.</summary>
    public void SetRestartCoordinator(AppRestartCoordinator coordinator) => _settingsUi?.SetRestartCoordinator(coordinator);

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

        // SetupMessageHandlers is gone — every messenger registration now lives in the coordinator
        // that owns the handler (cluster 1/3/6/7: overlay, keyboard, toolbar, settings).
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

        // Task Switcher visibility binding + the overlay VM PropertyChanged handlers moved to
        // OverlayBindingCoordinator (cluster 1) — they are pure VM→window glue.

        _inputDispatcher = new InputActionDispatcher();
        _inputDispatcher.Initialize(context);

        // SettingsUiCoordinator is constructed LAST (7th): it consumes the suspend seams on the
        // toolbar/quick-actions coordinators and the ReloadIfChanged seam on the keyboard
        // coordinator, so all three must be initialized before it.
        _settingsUi = new SettingsUiCoordinator(_modal, _quickActions, _toolbar, _keyboard);
        _settingsUi.Initialize(context);
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

                // The _settingsWindow reference moved to SettingsUiCoordinator (cluster 7); route
                // the sample through its seam (visibility check lives inside).
                _settingsUi?.UpdateSettingsGazeIfVisible(point, timestamp);
            });
        };

        _context.GazeEngine.CursorMoved += _gazeMovedHandler;
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

    // ShowSettingsWindow / OnSettingsChanged / OnSettingsWindowClosed / UpdateTrackStatusVisibility
    // moved to SettingsUiCoordinator (cluster 7) — they own the _modal.SettingsOpen write, the
    // toolbar/quick-actions suspend calls, the toolbar-snapshot-gated re-init, and the settings-
    // window construction. SetKeyboardVisible / OnEmojiSkinToneChanged moved to KeyboardCoordinator
    // (cluster 3).

    public void Dispose()
    {
        if (_disposed) return;

        // Dispose the settings sub-coordinator FIRST: it closes the settings window if open, which
        // unsuspends the toolbar/quick-actions while those coordinators are still alive to receive it.
        // (Handles its own message cleanup — the SettingsRequested/CloseSettings registrations.)
        _settingsUi?.Dispose();
        _settingsUi = null;

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

        // Dispose the drag sub-coordinator (handles its own event/message cleanup).
        _dragCoordinator?.Dispose();

        // Dispose the input-action sub-coordinator (handles its own message cleanup).
        _inputDispatcher?.Dispose();

        // Unregister all message handlers (defensive: after cluster 7, EC itself registers nothing —
        // every registration lives in a sub-coordinator — but this stays as a safety net.)
        _context.Messenger.UnregisterAll(this);

        // SkinToneChanged -= moved to KeyboardCoordinator.Dispose (cluster 3).

        _scrollModeController?.Dispose();

        // _glassBackdrop cleanup moved to OverlayBindingCoordinator.Dispose (cluster 1).

        _disposed = true;
    }
}
