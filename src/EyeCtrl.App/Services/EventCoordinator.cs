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
    // Inset (logical px) from the right screen edge for drag/manual-selection edge buttons.
    private const double RightEdgeInsetLogical = 30;

    private EventCoordinationContext? _context;
    // volatile: read on the gaze thread by SetupGazeRouting's early-out, written on the UI thread.
    private volatile bool _keyboardVisible;
    private volatile bool _settingsOpen;
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
    private ToolbarAutoHideController? _toolbarAutoHide;
    private ToolbarVisibilityAdapter? _toolbarVisibility;
    private Action<EyeCtrl.Core.Models.GazePoint>? _autoHideGazeHandler; // held so Dispose can unsubscribe (the lambda captures the controller local, not this field)
    private QuickActionsSummonController? _quickActionsSummon;
    private Action<GazePoint>? _quickActionsGazeHandler;   // GazePoint packs X/Y/ts/IsValid
    private PropertyChangedEventHandler? _quickActionsSettingsHandler;
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

    /// <summary>Hands the restart coordinator to the settings window at construction. Called once at startup.</summary>
    public void SetRestartCoordinator(AppRestartCoordinator coordinator) => _restartCoordinator = coordinator;

    /// <summary>Tray fallback: force-show the toolbar regardless of auto-hide state.</summary>
    public void ForceShowToolbar()
    {
        if (_context == null) return;
        _context.Dispatcher.BeginInvoke(() =>
        {
            _toolbarVisibility?.ForceShow();
            // Also flip the controller into Visible so it doesn't immediately re-hide.
            _toolbarAutoHide?.ResetToVisible();
        });
    }

    public void Initialize(EventCoordinationContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EyeCtrl");

        _scrollModeController = new ScrollModeController(context);

        SetupPropertyChangedHandlers();
        SetupMessageHandlers();
        SetupGazeRouting();
        SetupToolbarAutoHide();
        SetupQuickActionsSummon();
        SetupBackdrop();
        SetupDragHandlers();
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
        _context.Messenger.Register<EventCoordinator, TaskSwitcherSelectionMessage>(this, (_, msg) =>
            _context.Dispatcher.BeginInvoke(() => HandleTaskSwitcherSelection(msg)));
        _context.Messenger.Register<EventCoordinator, QuickActionSelectionMessage>(this, (_, msg) =>
        {
            // Window actions resolve the foreground HWND SYNCHRONOUSLY, on the same tick as the
            // dwell completion (B2 spec §4 BLOCKER A.1) — the quick-actions dwell chain already
            // runs on the UI dispatcher, so a deferred re-query could target a window the user
            // was never looking at. The HWND then rides to the executor, exactly as
            // TaskSwitcherSelectionMessage carries its own. Every other action type is
            // window-independent and keeps the deferred hop unchanged.
            IntPtr? hwnd = msg.Action.Type == QuickActionType.WindowAction
                ? _context.WindowManager.TryGetEligibleForegroundWindow()
                : null;
            _context.Dispatcher.BeginInvoke(() => ExecuteQuickAction(msg.Action, hwnd));
        });
        // Window actions: NO BeginInvoke. The toolbar dwell chain is already on the UI dispatcher,
        // so Messenger delivery runs on the same tick as dwell completion. Querying the foreground
        // HWND inline (spec §4 BLOCKER A.1) avoids a deferred hop that could let the foreground
        // change between dwell completion and the eligibility query. Contrast with the Task Switcher
        // registration above, whose HWND rides the message and is therefore safe to defer.
        _context.Messenger.Register<EventCoordinator, WindowActionRequestedMessage>(this, (_, msg) =>
            HandleWindowAction(msg.Action));
    }

    private void HandleTaskSwitcherSelection(TaskSwitcherSelectionMessage msg)
    {
        if (_context == null) return;
        _context.WindowManager.BringToFront(msg.Hwnd);
        // HideOverlay is public on the VM so the coordinator can dismiss after a selection
        // (in addition to the toggle path inside Receive).
        _context.OverlayManager.TaskSwitcher.HideOverlay();
    }

    /// <summary>
    /// Routes a toolbar window-action dwell (Minimize/MaximizeRestore/Close) to the foreground
    /// window. The foreground HWND is queried once, synchronously, at op-fire time
    /// (spec §4 BLOCKER A.1) — the registration has no BeginInvoke, so this runs on the same
    /// tick as the dwell completion and hits exactly the window the user was looking at.
    /// </summary>
    private void HandleWindowAction(WindowAction action)
    {
        if (_context == null) return;

        var hwnd = _context.WindowManager.TryGetEligibleForegroundWindow();
        if (hwnd is null)
        {
            // Foreground was the shell, this process, zero, or an invalid/destroyed window.
            Log.Information("Window-action {Action} skipped: no eligible foreground window", action);
            return;
        }

        switch (action)
        {
            case WindowAction.Minimize:
                _context.WindowManager.Minimize(hwnd.Value);
                break;
            case WindowAction.MaximizeRestore:
                _context.WindowManager.MaximizeOrRestore(hwnd.Value);
                break;
            case WindowAction.Close:
                var posted = _context.WindowManager.CloseWindow(hwnd.Value);
                if (!posted)
                    Log.Information("Window-action Close: PostMessage(WM_CLOSE) returned false (UIPI-blocked by elevated foreground?)");
                break;
        }
    }

    /// <summary>
    /// Executes the user-selected Quick Action, dispatching by <see cref="QuickActionType"/>.
    /// Errors are caught and logged so a failing action never tears down the gaze thread.
    /// </summary>
    private void ExecuteQuickAction(QuickAction action, IntPtr? windowActionHwnd = null)
    {
        if (_context == null) return;
        try
        {
            switch (action.Type)
            {
                case QuickActionType.KeyboardShortcut:
                    ExecuteKeyboardShortcut(action.Payload);
                    break;
                case QuickActionType.CannedText:
                    _context.InputSimulator.SendText(action.Payload);
                    break;
                case QuickActionType.LaunchExe:
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(action.Payload) { UseShellExecute = true });
                    break;
                case QuickActionType.LaunchWindowsApp:
                    _context.WindowsAppLauncher.ActivateApplication(action.Payload);
                    break;
                case QuickActionType.LaunchUrl:
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(action.Payload) { UseShellExecute = true });
                    break;
                case QuickActionType.MediaControl:
                    ExecuteMediaControl(action.Payload);
                    break;
                case QuickActionType.WindowAction:
                    ExecuteWindowAction(action.Payload, windowActionHwnd);
                    break;
                default:
                    Log.Warning("Unknown QuickActionType: {Type}", action.Type);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "QuickAction execution failed: {Type} '{Label}'", action.Type, action.Label);
        }
    }

    /// <summary>
    /// Parses a keyboard chord (e.g. "Ctrl+Shift+Enter") and sends it as a modified key press.
    /// Single-character keys are resolved via <see cref="Win32InputSimulator.CharToVirtualKey"/>;
    /// named keys (e.g. "Enter", "F5", "Left") via <see cref="KeyNameMapper.TryMap"/>.
    /// </summary>
    private void ExecuteKeyboardShortcut(string chord)
    {
        if (_context == null) return;
        var parts = chord.Split('+');
        var mods = new HashSet<ModifierKey>();
        string key = parts[^1].Trim();   // trim whitespace so "Ctrl + C" works (modifiers are trimmed below)

        for (int i = 0; i < parts.Length - 1; i++)
        {
            var mod = parts[i].Trim() switch
            {
                "Ctrl" => (ModifierKey?)ModifierKey.Ctrl,
                "Shift" => ModifierKey.Shift,
                "Alt" => ModifierKey.Alt,
                "Win" => ModifierKey.Win,
                _ => null   // unknown modifier → drop
            };
            if (mod.HasValue) mods.Add(mod.Value);
        }

        ushort vk = 0;
        bool isExtended = false;
        IReadOnlyList<ModifierKey> requiredModifiers = Array.Empty<ModifierKey>();
        if (key.Length == 1)
        {
            var ch = char.IsLetter(key[0]) ? char.ToLowerInvariant(key[0]) : key[0];
            (vk, requiredModifiers) = Win32InputSimulator.CharToVirtualKey(ch);
        }

        if (vk == 0 && KeyNameMapper.TryMap(key, out var mappedVk, out var mappedExtended))
        {
            vk = mappedVk;
            isExtended = mappedExtended;
        }

        if (vk != 0)
        {
            if (requiredModifiers.Count == 0)
            {
                _context.InputSimulator.SendModifiedKeyPress(mods, vk, isExtendedKey: isExtended);
                return;
            }

            var merged = new HashSet<ModifierKey>(mods);
            foreach (var requiredModifier in requiredModifiers)
                merged.Add(requiredModifier);

            _context.InputSimulator.SendModifiedKeyPress(merged, vk, isExtendedKey: isExtended);
        }
        else
        {
            Log.Warning("QuickAction: could not map keyboard chord '{Chord}'", chord);
        }
    }

    /// <summary>
    /// Sends a media-transport virtual-key press for the given command name.
    /// Media keys are extended keys (KEYEVENTF_EXTENDEDKEY).
    /// </summary>
    private void ExecuteMediaControl(string command)
    {
        if (_context == null) return;
        ushort vk = command switch
        {
            "PlayPause" => 0xB3, "Stop" => 0xB2,
            "NextTrack" => 0xB0, "PreviousTrack" => 0xB1,
            "VolumeUp" => 0xAF, "VolumeDown" => 0xAE, "VolumeMute" => 0xAD,
            _ => 0
        };
        if (vk != 0)
            _context.InputSimulator.SendKeyPress(vk, isExtendedKey: true);
    }

    /// <summary>
    /// Applies a window-management quick action to <paramref name="hwnd"/>. The HWND is resolved by
    /// the <see cref="QuickActionSelectionMessage"/> recipient synchronously on the dwell-completion
    /// tick and passed in here — this method must NEVER re-query the foreground, or a foreground
    /// change during the dispatcher hop could redirect Close to a different window (B2 spec §4
    /// BLOCKER A.1). Mirrors how TaskSwitcherSelectionMessage carries its HWND.
    /// </summary>
    /// <param name="payload">The <see cref="WindowAction"/> enum member name from the stored action.</param>
    /// <param name="hwnd">The eligible foreground window, or null if none was eligible.</param>
    /// <remarks>
    /// Exception safety is INHERITED from the caller: this runs inside <c>ExecuteQuickAction</c>'s
    /// try/catch. Both failure paths here return after logging rather than throwing, so that catch
    /// is not exercised today — but if this method is ever called from anywhere else, it needs its
    /// own guard, or a Win32 fault would escape onto the dispatcher.
    /// </remarks>
    private void ExecuteWindowAction(string payload, IntPtr? hwnd)
    {
        if (_context == null) return;

        if (!Enum.TryParse<WindowAction>(payload, out var windowAction))
        {
            Log.Warning("QuickAction: unknown window action payload '{Payload}'", payload);
            return;
        }

        if (hwnd is null)
        {
            // Foreground was the shell, this process, zero, or an invalid/destroyed window.
            Log.Information("Window-action {Action} skipped: no eligible foreground window", windowAction);
            return;
        }

        switch (windowAction)
        {
            case WindowAction.Minimize:
                _context.WindowManager.Minimize(hwnd.Value);
                break;
            case WindowAction.MaximizeRestore:
                _context.WindowManager.MaximizeOrRestore(hwnd.Value);
                break;
            case WindowAction.Close:
                if (!_context.WindowManager.CloseWindow(hwnd.Value))
                    Log.Information("Window-action Close: PostMessage(WM_CLOSE) returned false (UIPI-blocked by elevated foreground?)");
                break;
        }
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
            _context.Dispatcher.BeginInvoke(() => SetKeyboardVisible(!_keyboardVisible, manageSuppression: true)));

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
                        _toolbarVisibility?.SetActive(false);
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
                        _toolbarAutoHide?.ReassertVisibility();
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
                        _toolbarAutoHide?.ReassertVisibility();
                        if (_context.CursorVM.IsVisible) cursorCircle.Show();
                        calWindow.DismissCalibration();
                        break;
                }
            }));

        // InteractionManager state change: mirror Paused into the toolbar auto-hide controller.
        // This is EventCoordinator's only StateChangedMessage registration (no prior one to extend).
        _context.Messenger.Register<EventCoordinator, StateChangedMessage>(this, (_, msg) =>
            _toolbarAutoHide?.SetPaused(msg.NewState == InteractionState.Paused));
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
            if (_toolbarInitialized && !_keyboardVisible && !_settingsOpen)
                return;

            _context.Dispatcher.BeginInvoke(() =>
            {
                toolbar.UpdateGaze(point, timestamp);
                _toolbarInitialized = true;

                if (_keyboardVisible && keyboard.IsVisible)
                    keyboard.UpdateGaze(point, timestamp);

                if (_settingsWindow?.IsVisible ?? false)
                    _settingsWindow.UpdateGaze(point, timestamp);
            });
        };

        _context.GazeEngine.CursorMoved += _gazeMovedHandler;
    }

    private void SetupToolbarAutoHide()
    {
        if (_context == null) return;

        var s = _context.Settings.Toolbar;
        // Two distinct gates:
        //  - providerIsRealTracker (immutable for the process): whether the subscription is wired at all.
        //    Mouse/test mode never subscribes → zero overhead, no auto-hide ever.
        //  - enabled (mutable via UpdateEnabled when IsDocked is toggled at runtime): the per-sample
        //    Feed() guard (Feed early-returns when !_enabled). Subscribe under any real tracker even
        //    if the toolbar starts FLOATING — otherwise a runtime dock-toggle couldn't re-enable
        //    auto-hide (the subscription would never exist). Feed's guard makes an unsubscribed
        //    floating toolbar a cheap no-op.
        bool providerIsRealTracker = EyeCtrl.App.App.ActiveProviderType != Core.Enums.GazeProviderType.Mouse;
        bool enabled = providerIsRealTracker && s.IsDocked;

        // Cache the toolbar rect in physical px from the live window.
        var toolbarRect = CaptureToolbarRect();

        var controller = new ToolbarAutoHideController(
            enabled: enabled,
            edge: s.DockedEdge,
            screenW: System.Windows.SystemParameters.PrimaryScreenWidth * DpiHelper.ScaleX,
            screenH: System.Windows.SystemParameters.PrimaryScreenHeight * DpiHelper.ScaleY,
            toolbarRect: toolbarRect,
            hideDelayMs: 5000,
            summonDelayMs: 400,
            offScreenMarginPx: 50,
            calibrationActive: () => _context.CalibrationService.IsSessionActive,
            // Read per call, not cached, so a mid-session recalibration takes effect. Identity while
            // no calibration is loaded, which makes this a no-op until one is. Deliberately NOT
            // passed to QuickActionsSummonController below: its IsPastEdge test is purely off-screen,
            // where the transform extrapolates outside the domain it was fit to.
            toScreen: p => _context.CalibrationService.CurrentTransform?.Apply(p) ?? p);

        var vis = new ToolbarVisibilityAdapter(_context.OverlayManager, _context.Dispatcher, _context.ToolbarVM);
        controller.Attach(vis);

        _toolbarAutoHide = controller;
        _toolbarVisibility = vis;

        // Subscribe under any real tracker (NOT gated on `enabled`/IsDocked) so a runtime dock-toggle
        // can activate auto-hide. Feed() early-returns when !_enabled, so samples are a cheap no-op
        // while the toolbar is floating or the feature otherwise disabled.
        if (providerIsRealTracker)
        {
            // Store the handler in a field so Dispose can -= it. (The lambda captures the `controller`
            // local; nulling _toolbarAutoHide alone would NOT unsubscribe it.)
            _autoHideGazeHandler = sample => controller.Feed(sample, sample.TimestampMicroseconds);
            _context.GazeEngine.GazeSampled += _autoHideGazeHandler;
        }

        // CaptureToolbarRect() above ran during Initialize, BEFORE ShowOverlays shows + lays out the
        // toolbar (App.OnStartup order: Initialize → ShowOverlays → gazeEngine.Start). At that point
        // ActualWidth/ActualHeight are 0 (the toolbar is SizeToContent and unmeasured). Enqueue a
        // deferred re-capture at Loaded priority so the controller's hit-test rect is correct by the
        // time the first gaze sample arrives. (Spec Edge case #7: wait one layout pass before caching.)
        RefreshToolbarRect();
    }

    private void UpdateQuickActionsEffectiveEdge()
    {
        if (_context == null) return;
        _context.QuickActionsGridVM.EffectiveSummonEdge = ComputeActualSummonEdge(_context.Settings.QuickActions.SummonEdge);
    }

    private void AttachQuickActionsSettingsHandler()
    {
        if (_quickActionsSettingsHandler != null || _context == null) return;

        _quickActionsSettingsHandler = (_, e) =>
        {
            if (_context == null) return;

            if (e.PropertyName == nameof(QuickActionsSettings.Enabled))
            {
                if (_context.Settings.QuickActions.Enabled)
                {
                    if (_quickActionsSummon == null)
                        SetupQuickActionsSummon();
                    else
                        _quickActionsSummon.SetSuspended(false);
                }
                else
                {
                    _quickActionsSummon?.SetSuspended(true);
                }
                return;
            }

            if (_quickActionsSummon == null || e.PropertyName != nameof(QuickActionsSettings.SummonEdge)) return;
            UpdateQuickActionsEffectiveEdge();
            _quickActionsSummon.UpdateEdge(_context.QuickActionsGridVM.EffectiveSummonEdge);
        };
        _context.Settings.QuickActions.PropertyChanged += _quickActionsSettingsHandler;
    }

    private void SetupQuickActionsSummon()
    {
        var s = _context!.Settings.QuickActions;
        AttachQuickActionsSettingsHandler();

        if (_quickActionsSummon != null)
        {
            _quickActionsSummon.SetSuspended(!s.Enabled);
            return;
        }

        if (!s.Enabled) return;

        // Hot-edge summon is a gaze gesture — disabled in mouse mode (the OS clamps the cursor to
        // the screen so it can't go "past" an edge, and on multi-monitor a Right/Bottom edge could
        // misfire on cursor travel). The toolbar QuickActions button remains the mouse-mode path.
        if (EyeCtrl.App.App.ActiveProviderType == Core.Enums.GazeProviderType.Mouse) return;

        // Resolve actual summon edge (avoid conflict with main toolbar)
        var actualEdge = ComputeActualSummonEdge(s.SummonEdge);

        // Match the toolbar's screen-size source (EventCoordinator.cs:452): WPF SystemParameters
        // are logical DIPs, so ×ScaleX/×ScaleY yields physical px. Do NOT use WinForms
        // Screen.Bounds — it is ALREADY physical under PerMonitorV2, so ×ScaleX double-scales
        // (would break Right/Bottom summon edges on scaled displays; the default "Left" hides it).
        var controller = new QuickActionsSummonController(
            actualEdge,
            System.Windows.SystemParameters.PrimaryScreenWidth * DpiHelper.ScaleX,
            System.Windows.SystemParameters.PrimaryScreenHeight * DpiHelper.ScaleY,
            s.SummonThresholdMs,
            s.DismissTimeoutMs,
            calibrationActive: () => _context.CalibrationService.IsSessionActive);

        // Gaze-thread → UI-thread marshal: both events hop through the dispatcher
        // before touching the VM (mirrors ToolbarAutoHideController's visibility adapter).
        var gridVM = _context.QuickActionsGridVM;
        // Keep the VM's effective edge in sync with the resolved edge (the controller watches the
        // resolved edge via ComputeActualSummonEdge; the grid window must anchor there too, or it
        // renders at the raw configured edge which may differ when it collides with the toolbar dock).
        UpdateQuickActionsEffectiveEdge();
        controller.SummonRequested += () =>
            _context.Dispatcher.BeginInvoke(() =>
            {
                // Toolbar button / mouse-mode may have already shown it; only show when hidden.
                if (!gridVM.IsVisible)
                {
                    UpdateQuickActionsEffectiveEdge();   // edge may have changed since last show
                    gridVM.Receive(new QuickActionsToggledMessage()); // → Reload + show
                    // Do NOT call EnterSummoned here: Feed already set the controller to Summoned
                    // with the gaze-away dismiss timer armed (_summonOpened=true). EnterSummoned
                    // would clear that flag and disable auto-dismiss for hot-edge summons.
                }
            });
        controller.DismissRequested += () =>
            _context.Dispatcher.BeginInvoke(() =>
            {
                if (gridVM.IsVisible)
                {
                    gridVM.HideOverlay();      // hide WITHOUT round-tripping through Receive/toggle
                    controller.ResetToIdle();  // explicit-state hook (no re-entrant DismissRequested)
                }
            });

        // Feed takes a GazePoint (validity packed inside) — no unwrap/rewrap.
        _quickActionsGazeHandler = sample =>
            controller.Feed(sample, sample.TimestampMicroseconds);
        _context.GazeEngine.GazeSampled += _quickActionsGazeHandler;

        _quickActionsSummon = controller;

        // React to toolbar edge changes — re-evaluate conflict (not just pass-through)
        _context.Settings.Toolbar.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ToolbarSettings.DockedEdge))
            {
                UpdateQuickActionsEffectiveEdge();
                _quickActionsSummon?.UpdateEdge(gridVM.EffectiveSummonEdge);
            }
        };

        // H1 fix — bidirectional controller↔VM sync. Subscribe to grid VM IsVisible so that
        // toolbar-button shows (which set IsVisible=true without going through the controller)
        // and any other VM-driven show/hide keep the controller honest.
        gridVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(QuickActionsGridViewModel.IsVisible)) return;
            _context.Dispatcher.BeginInvoke(() =>
            {
                if (gridVM.IsVisible)
                {
                    // Only mark VM-driven shows (toolbar button, etc.), which leave the controller
                    // Idle. A hot-edge summon already set it to Summoned WITH the dismiss timer
                    // armed; calling EnterSummoned again would clear _summonOpened and kill
                    // auto-dismiss. So sync only when the controller didn't initiate the show.
                    if (controller.State == QuickActionsSummonController.SummonState.Idle)
                        controller.EnterSummoned();
                }
                else
                {
                    controller.ResetToIdle();
                }
            });
        };
    }

    private string ComputeActualSummonEdge(string configuredEdge)
    {
        if (configuredEdge == _context!.Settings.Toolbar.DockedEdge)
            return configuredEdge switch
            {
                "Left" => "Right", "Right" => "Left",
                "Top" => "Bottom", "Bottom" => "Top",
                _ => "Left"
            };
        return configuredEdge;
    }

    private PhysicalRect CaptureToolbarRect()
    {
        var t = _context!.OverlayManager.Toolbar;
        // Convert logical window bounds (WPF DIPs) to physical px.
        double x = DpiHelper.LogicalToPhysicalX(t.Left);
        double y = DpiHelper.LogicalToPhysicalY(t.Top);
        double w = DpiHelper.LogicalToPhysicalX(t.ActualWidth);
        double h = DpiHelper.LogicalToPhysicalY(t.ActualHeight);
        return new PhysicalRect(x, y, w, h);
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

        // DragDismissManualSelectionMessage is intentionally NOT registered here. Only
        // DragTrailViewModel reacts to it (transitions DragSubState back to FreeDrag). The
        // previous EventCoordinator handler only invoked a defensive ManualSelection overlay
        // hide inside ScrollModeController; that hide was a no-op because Dragging and
        // ManualSelectionActive are mutually exclusive InteractionStates, so consuming the
        // message here would just discard it without effect.

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

        _settingsOpen = true;
        _toolbarAutoHide?.SetSuspended(true);
        _quickActionsSummon?.SetSuspended(true);
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
            _settingsOpen = false;
            // Window construction threw before Show() — OnSettingsWindowClosed will never fire, so
            // undo the suspend we set alongside _settingsOpen = true above.
            _toolbarAutoHide?.SetSuspended(false);
            _quickActionsSummon?.SetSuspended(false);
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
            _context.OverlayManager.Toolbar.Initialize(s.Toolbar.Size, s.Dwell.ToolbarButtonDwellMs, s.Toolbar.ShowWindowActions);
            _context.OverlayManager.Toolbar.SetMode(s.Toolbar.IsDocked, s.Toolbar.DockedEdge);

            // Re-evaluate enablement (IsDocked may have flipped to false → disable + force-show),
            // refresh the cached edge, and reposition the pause indicator along the new edge.
            bool nowEnabled = EyeCtrl.App.App.ActiveProviderType != Core.Enums.GazeProviderType.Mouse && s.Toolbar.IsDocked;
            _toolbarAutoHide?.UpdateEnabled(nowEnabled);
            _toolbarAutoHide?.UpdateEdge(s.Toolbar.DockedEdge);
            _toolbarVisibility?.PositionIndicator(s.Toolbar.DockedEdge);
            RefreshToolbarRect();

            // Reconcile the VM AFTER the View has rebuilt for the new button set. The View's
            // CollectionChanged handler (subscribed in BindToViewModel) binds the freshly-added
            // ToolbarButtonViewModel instances; on removal it unbinds so a later re-add binds anew.
            // SetWindowActionsVisible(false) atomically drops Buttons + _buttonLookup +
            // _positionedButtons + DwellTarget per id, else an invisible Close stays dwellable
            // at its last screen position (the hidden-toolbar hazard).
            _context.ToolbarVM.SetWindowActionsVisible(s.Toolbar.ShowWindowActions);

            _toolbarSnapshot = toolbarSnapshot;
        }

        var keyboardSnapshot = KeyboardStackSnapshot.Capture(s);
        if (_keyboardSnapshot != keyboardSnapshot)
        {
            LoadKeyboard(_context.OverlayManager.Keyboard, s, _context.InputSimulator, _context.SettingsService);
        }

        Log.Information("Settings saved");
    }

    private void RefreshToolbarRect()
    {
        if (_context == null || _toolbarAutoHide == null) return;
        _context.Dispatcher.BeginInvoke(new Action(() =>
        {
            _toolbarAutoHide.UpdateToolbarRect(CaptureToolbarRect());
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnSettingsWindowClosed(object? sender, EventArgs e)
    {
        if (_context == null) return;

        _settingsWindow = null;
        _settingsOpen = false;
        _toolbarAutoHide?.SetSuspended(false);
        _quickActionsSummon?.SetSuspended(false);
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

        _keyboardSnapshot = KeyboardStackSnapshot.Capture(settings);
    }

    // manageSuppression: true for the toolbar toggle (deliberately pauses fixation while the
    // keyboard is up); false for the text-field auto-show/hide request path, which must NOT touch
    // SuppressFixation (it may be owned elsewhere, e.g. note editing, and typing shouldn't pause
    // fixation for the rest of the app).
    private void SetKeyboardVisible(bool visible, bool manageSuppression)
    {
        if (_context == null) return;

        _toolbarAutoHide?.SetSuspended(visible);
        _quickActionsSummon?.SetSuspended(visible);
        _keyboardVisible = visible;
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

        // Unsubscribe the auto-hide gaze handler. The handler was stored in a field (in
        // SetupToolbarAutoHide) because it captures the controller local — nulling
        // _toolbarAutoHide would NOT unsubscribe it.
        if (_autoHideGazeHandler != null && _context != null)
            _context.GazeEngine.GazeSampled -= _autoHideGazeHandler;
        _autoHideGazeHandler = null;
        _toolbarAutoHide = null;
        _toolbarVisibility = null;
        if (_quickActionsGazeHandler != null && _context != null)
            _context.GazeEngine.GazeSampled -= _quickActionsGazeHandler;
        _quickActionsGazeHandler = null;
        if (_quickActionsSettingsHandler != null && _context != null)
            _context.Settings.QuickActions.PropertyChanged -= _quickActionsSettingsHandler;
        _quickActionsSettingsHandler = null;
        _quickActionsSummon = null;

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

        // Unsubscribe from drag handlers (_context is non-null — guarded at the top of Dispose)
        _context.InteractionManager.DragStarted -= OnDragStarted;
        _context.DragTrailVM.PropertyChanged -= OnDragTrailVMPropertyChanged;
        _disposeEscapeHook?.Invoke();
        CloseDragOverlay();

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
