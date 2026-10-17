namespace EyeCtrl.App.Services;

using System;
using EyeCtrl.App.Diagnostics.SubPixel;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
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
    private RepeatModeController? _repeatModeController;
    private LostGazeExitCoordinator? _lostGazeExit;
    private QuickActionDispatcher? _quickActionDispatcher;
    private KeyboardInteractionDispatcher? _keyboardInteractionDispatcher;
    private KeyboardCoordinator? _keyboard;
    private OverlayVmBindingCoordinator? _overlayVm;
    private OverlayMessageCoordinator? _overlayMessage;
    private SettingsUiCoordinator? _settingsUi;
    private ActivationOfferCoordinator? _activationOffers;
    private SubPixelProbeCoordinator? _probe;
    private KeyboardProbe? _keyboardProbe;

    /// <summary>
    /// Gets the word predictor instance used for keyboard predictions, if enabled.
    /// </summary>
    public WordPredictor? WordPredictor => _keyboard?.WordPredictor;
    public PredictionOrchestrator? Orchestrator => _keyboard?.Orchestrator;

    public void SetModelServices(ModelRegistryService modelRegistry, ModelDownloadService modelDownload, ModelManagementService modelManagement)
    {
        // EC keeps the 3-arg public signature (App.xaml.cs calls it with 3 args). The download
        // service is dead and dropped at the seam. KeyboardCoordinator takes only the registry
        // (for the prediction-stack rebuild); SettingsUiCoordinator takes registry+management (for
        // the settings-window construction).
        //
        // Fail LOUD when called before Initialize. The sub-coordinators that store these only exist
        // post-Initialize, so a null-conditional forward would DROP the services silently and the
        // mis-order would resurface much later as ShowSettingsWindow's "Model services not
        // initialized" — on a user-visible UI path, far from the cause. (The pre-decomposition code
        // stored them in EC's own fields, so it tolerated either order; this guard replaces that
        // tolerance with a startup-time failure.)
        RequireInitialized(nameof(SetModelServices));
        _keyboard!.SetModelServices(modelRegistry);
        _settingsUi!.SetModelServices(modelRegistry, modelManagement);
    }

    /// <summary>Whether a Settings window exists (set when it opens, cleared when it closes).</summary>
    internal bool IsSettingsOpen => _modal.SettingsOpen;

    /// <summary>
    /// Raised on the UI thread after <see cref="IsSettingsOpen"/> flips. A wrapper over
    /// <c>ModalInteractionState.SettingsOpenChanged</c>, which is a single delegate field with
    /// other subscribers: always <c>+=</c> / <c>-=</c> through here, never assign.
    /// </summary>
    internal event Action<bool> SettingsOpenChanged
    {
        add => _modal.SettingsOpenChanged += value;
        remove => _modal.SettingsOpenChanged -= value;
    }

    /// <summary>Whether the sub-pixel probe window is up (SP-8 fixup 01 F1: the restart notice
    /// host treats it exactly like Settings).</summary>
    internal bool IsProbeOpen => _modal.ProbeOpen;

    /// <summary>
    /// Raised on the UI thread after <see cref="IsProbeOpen"/> flips. A wrapper over
    /// <c>ModalInteractionState.ProbeOpenChanged</c>, whose contract says subscribers read the flag,
    /// not the argument (a re-entrant flip delivers a stale value): always <c>+=</c> / <c>-=</c>
    /// through here, never assign.
    /// </summary>
    internal event Action<bool> ProbeOpenChanged
    {
        add => _modal.ProbeOpenChanged += value;
        remove => _modal.ProbeOpenChanged -= value;
    }

    /// <summary>Hands the restart coordinator to the settings window at construction. Called once at startup.</summary>
    public void SetRestartCoordinator(AppRestartCoordinator coordinator)
    {
        // Same fail-loud rationale as SetModelServices above.
        RequireInitialized(nameof(SetRestartCoordinator));
        _settingsUi!.SetRestartCoordinator(coordinator);
    }

    /// <summary>Hands the supervision toggle (ticket 04) to the settings window at construction.</summary>
    public void SetSupervisionToggle(SupervisionToggleService service)
    {
        RequireInitialized(nameof(SetSupervisionToggle));
        _settingsUi!.SetSupervisionToggle(service);
    }

    /// <summary>
    /// Guards the sub-coordinator forwards that carry startup state (model services, restart
    /// coordinator, keyboard load). Throws when <see cref="Initialize"/> has not run, so a
    /// startup-order mistake surfaces at startup instead of silently dropping the hand-off.
    /// </summary>
    private void RequireInitialized(string member)
    {
        // Disposed is checked too: Dispose nulls the sub-coordinator fields but leaves _context set,
        // so an _context-only check would pass and then NullReference on the forward below.
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_context == null)
            throw new InvalidOperationException(
                $"EventCoordinator must be initialized before calling {member}.");
    }

    /// <summary>Tray fallback: force-show the toolbar regardless of auto-hide state. A live probe
    /// run ends first — the interlock's generation bump is also the only guard a queued start has
    /// against this trigger (build plan C9/C7; W3 (e), W9 (b)).</summary>
    public void ForceShowToolbar()
    {
        if (_context == null) return;
        _probe?.InterlockForceShowToolbar(
            () => _context.Dispatcher.BeginInvoke(() => _toolbar?.ForceShow()));
    }

    /// <summary>
    /// The toolbar lifecycle sub-coordinator, exposed for App's post-initialize hand-offs: the
    /// adaptation preview's auto-hide suspension reaches it through the DI
    /// <see cref="ToolbarLifecycleBridge"/> (Task 10, P8), which App attaches immediately after
    /// <see cref="Initialize"/>. Null before Initialize and after Dispose.
    /// </summary>
    internal ToolbarLifecycleCoordinator? ToolbarLifecycle => _toolbar;

    public void Initialize(EventCoordinationContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));

        // The note gate (P2 D1). Pause is POLLED, not subscribed: pause is entered and left only by
        // TogglePause on the UI thread, so a per-sample read is exact and simpler than a state-change
        // event that fires on every fixation edge. The suspend predicate ORs three cheap UI-thread
        // reads: the activation-offer coordinator's modal flag (null-safe — the coordinator is
        // constructed further below, so the flag reads false until then), the interaction states
        // whose surfaces own the screen, and the modes menu's visibility. The eye-care exercise and
        // calibration flags reach the manager as messages instead (EyeCareService /
        // CalibrationStateChangedMessage), so they are not part of this lambda.
        context.NoteManager.SetPausePoll(
            () => context.InteractionManager.CurrentState == InteractionState.Paused);
        context.NoteManager.SetSuspendPredicate(() =>
            (_activationOffers?.ModalSuspended ?? false)
            || NoteManager.InteractionStateSuspendsNotes(context.InteractionManager.CurrentState)
            || context.ModesMenuVM.IsVisible);

        // The notes' keyboard probe (P1 D5), wired once like the suspend predicate above: the
        // summon/release of the keyboard around Edit, the ⌨ control, the keyboard-gate on note
        // dwell and the foreign-hide detection all read through it.
        _keyboardProbe = new KeyboardProbe(
            context.KeyboardVM, context.Dispatcher, context.Messenger,
            () => context.OverlayManager.Keyboard,
            () => context.OverlayManager.EdgeButtonBar.EdgeBarVm);
        context.NoteManager.SetKeyboardProbe(_keyboardProbe);

        _scrollModeController = new ScrollModeController(context);

        // SetupMessageHandlers is gone — every messenger registration now lives in the coordinator
        // that owns the handler (cluster 1/3/6/7: overlay, keyboard, toolbar, settings).
        _toolbar = new ToolbarLifecycleCoordinator(_modal);
        _toolbar.Initialize(context);
        // Overlay surface split (T10) along the stimulus axis: VmBinding owns the property-change-
        // driven VM→window bindings + the glass backdrop; Message owns the message-driven overlay
        // registrations and reaches back into VmBinding for the one shared HideBackdrop seam.
        // VmBinding is constructed first — Message depends on it.
        _overlayVm = new OverlayVmBindingCoordinator(_modal);
        _overlayVm.Initialize(context);
        _overlayMessage = new OverlayMessageCoordinator(_toolbar, _overlayVm);
        _overlayMessage.Initialize(context);
        _quickActions = new QuickActionsCoordinator(_modal);
        _quickActions.Initialize(context);
        _keyboard = new KeyboardCoordinator(_modal, _quickActions, _toolbar);
        _keyboard.Initialize(context);
        _dragCoordinator = new DragCoordinator();
        _dragCoordinator.Initialize(context);

        _repeatModeController = new RepeatModeController();
        _repeatModeController.Initialize(context);

        // Ends drag/scroll/repeat when the gaze is gone (ticket 02). It only sends the cancel
        // messages the drag, scroll and repeat owners above already handle.
        _lostGazeExit = new LostGazeExitCoordinator();
        _lostGazeExit.Initialize(context);

        // Ticket 8: the activation offer's geometry, chrome zones and modal-surface suspension.
        // Constructed after the toolbar coordinator (it subscribes to its visibility event) and
        // before any gaze sample arrives.
        _activationOffers = new ActivationOfferCoordinator(_toolbar, _modal);
        _activationOffers.Initialize(context);

        // Task Switcher visibility binding + the overlay VM PropertyChanged handlers moved to
        // OverlayVmBindingCoordinator (cluster 1) — they are pure VM→window glue.

        // The input-action surface was split (T9) along the quick-actions-vs-keyboard/interaction
        // seam: QuickActionDispatcher owns Execute*/task-switcher/quick-action selection, and
        // KeyboardInteractionDispatcher owns key activation/prediction/special-key/action-requested.
        // They are independent (no shared state, no cross-calls) so construction order is free.
        _quickActionDispatcher = new QuickActionDispatcher();
        _quickActionDispatcher.Initialize(context);
        _keyboardInteractionDispatcher = new KeyboardInteractionDispatcher();
        _keyboardInteractionDispatcher.Initialize(context);

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

        // The sub-pixel probe (build plan C7) is constructed LAST: its host wraps the toolbar,
        // keyboard and quick-actions seams, and its C10 entry is threaded back into the settings
        // window it just built. Its messenger registration therefore follows SettingsUiCoordinator's,
        // which is what W9 pins (review 04 N7).
        _probe = new SubPixelProbeCoordinator(_modal, new ProbeHost(
            _modal, context.GazeEngine, context.OverlayManager.CursorCircle, context.CursorVM,
            context.ToolbarVM, _toolbar, _keyboard, _quickActions, context.ModesMenuVM,
            context.TaskSwitcherVM, context.QuickActionsGridVM, context.Messenger, context.Settings));
        _probe.Initialize(context);
        _settingsUi.SetProbeEntry(
            () => _probe.GetBlockReason(),
            () => _probe.RequestStartAuto(() => _settingsUi.CloseSettingsWindowForProbe()),
            () => _probe.CanResumeFromResults());
    }

    public void LoadKeyboard(KeyboardWindow keyboard, EyeCtrlSettings settings,
        IInputSimulator inputSimulator, SettingsService settingsService)
    {
        // Keep the four argument-null guards: LoadKeyboard_ThrowsArgumentNullException_WhenKeyboardIsNull
        // calls this on an UNINITIALIZED EventCoordinator and expects ArgumentNullException —
        // without these guards, the forward would dereference _keyboard (null pre-Initialize) and
        // throw NullReferenceException instead.
        ArgumentNullException.ThrowIfNull(keyboard);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(inputSimulator);
        ArgumentNullException.ThrowIfNull(settingsService);

        // KeyboardCoordinator carries its own _appDataDir InvalidOperationException guard (the field
        // moved there with the method), but that guard is UNREACHABLE from an uninitialized EC —
        // _keyboard itself is null until Initialize runs. Without this check the pre-decomposition
        // "must be initialized" diagnostic would degrade to a bare NullReferenceException.
        RequireInitialized(nameof(LoadKeyboard));

        _keyboard!.LoadKeyboard(keyboard, settings, inputSimulator, settingsService);
    }

    // ShowSettingsWindow / OnSettingsChanged / OnSettingsWindowClosed / UpdateTrackStatusVisibility
    // moved to SettingsUiCoordinator (cluster 7) — they own the _modal.SettingsOpen write, the
    // toolbar/quick-actions suspend calls, the toolbar-snapshot-gated re-init, and the settings-
    // window construction. SetKeyboardVisible / OnEmojiSkinToneChanged moved to KeyboardCoordinator
    // (cluster 3).

    /// <summary>See <see cref="DragCoordinator.ReleaseHeldButtonForFatalExit"/>: App's
    /// fatal-exception paths reach the drag coordinator through here (it is not in DI).</summary>
    internal void ReleaseHeldDragButtonForFatalExit() => _dragCoordinator?.ReleaseHeldButtonForFatalExit();

    public void Dispose()
    {
        if (_disposed) return;

        // Dispose the settings sub-coordinator FIRST: it closes the settings window (if open) with
        // its Closed handler detached first, so OnSettingsWindowClosed does NOT re-fire and touch the
        // suspend state of the coordinators being torn down below. (Handles its own message cleanup
        // — the SettingsRequested/CloseSettings registrations.)
        _settingsUi?.Dispose();
        _settingsUi = null;

        // The probe goes next: its restore needs the toolbar, keyboard and summon seams alive, and
        // its own messenger registration must go before those coordinators' teardown noise.
        _probe?.Dispose();
        _probe = null;

        // The notes' keyboard probe (P1 D5) unsubscribes its own sources (the keyboard VM, the
        // keyboard window, the edge bar VM and the edge-toggled message).
        _keyboardProbe?.Dispose();
        _keyboardProbe = null;

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

        // Dispose the overlay sub-coordinators (T10 split). Message first — it only unregisters its
        // messenger handlers; VmBinding owns the glass backdrop window, so it goes last among the
        // two (and also unsubscribes the 8 overlay VMs + TaskSwitcherVM). The bindings and backdrop
        // cleanup moved here in cluster 1.
        _overlayMessage?.Dispose();
        _overlayMessage = null;
        _overlayVm?.Dispose();
        _overlayVm = null;

        if (_context == null)
        {
            _disposed = true;
            return;
        }

        // Dispose the lost-gaze exit first: it sends into the drag/scroll/repeat owners below.
        _lostGazeExit?.Dispose();

        // Dispose the drag sub-coordinator (handles its own event/message cleanup).
        _dragCoordinator?.Dispose();

        // Dispose the repeat-mode sub-coordinator (stop button, tick timer, escape hook).
        _repeatModeController?.Dispose();

        // Dispose the activation-offer coordinator (chrome-zone and geometry subscriptions).
        _activationOffers?.Dispose();
        _activationOffers = null;

        // Dispose the input-action sub-coordinators (each handles its own message cleanup — each
        // registers as itself, so each UnregisterAll(this) is correctly keyed).
        _quickActionDispatcher?.Dispose();
        _keyboardInteractionDispatcher?.Dispose();

        // Unregister all message handlers (defensive: after cluster 7, EC itself registers nothing —
        // every registration lives in a sub-coordinator — but this stays as a safety net.)
        _context.Messenger.UnregisterAll(this);

        // SkinToneChanged -= moved to KeyboardCoordinator.Dispose (cluster 3).

        _scrollModeController?.Dispose();

        // _glassBackdrop cleanup moved to OverlayVmBindingCoordinator.Dispose (cluster 1).

        _disposed = true;
    }
}
