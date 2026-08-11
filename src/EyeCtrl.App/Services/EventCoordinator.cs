namespace EyeCtrl.App.Services;

using System;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using CommunityToolkit.Mvvm.Messaging;

/// <summary>
/// Coordinates event handlers between ViewModels, overlay windows, and the core engine.
/// Extracted from App.xaml.cs to improve Single Responsibility Principle compliance.
/// </summary>
public class EventCoordinator : IEventCoordinator
{
    private EventCoordinationContext? _context;
    // Shared modal flags — written on the UI thread, read on the gaze thread (now inside
    // ToolbarLifecycleCoordinator.SetupGazeRouting). The volatility lives on ModalInteractionState
    // itself; this single object is shared with the keyboard/settings sub-coordinators (T5/T7) so
    // there is no stale-flag window.
    private readonly ModalInteractionState _modal = new();
    private bool _disposed;
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

        // Hand the settings sub-coordinator to the toolbar sub-coordinator: the per-sample gaze
        // route (which lives on ToolbarLifecycleCoordinator after the T8 fold) forwards to the
        // settings window through this seam. Done after _settingsUi is constructed — ToolbarLifecycle
        // is constructed earlier in task order.
        _toolbar.AttachSettingsUi(_settingsUi);
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

        // Dispose the toolbar sub-coordinator (handles its own gaze-handler unsubscribe — both
        // the auto-hide GazeSampled handler and the per-sample CursorMoved route that moved there
        // in cluster 8 — plus message cleanup, including the StateChangedMessage registration that
        // moved there in cluster 6).
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
