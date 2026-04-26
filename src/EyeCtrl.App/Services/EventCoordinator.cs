namespace EyeCtrl.App.Services;

using System;
using System.ComponentModel;
using System.IO;
using EyeCtrl.App;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Views;
using EyeCtrl.Platform.Input;
using EyeCtrl.Core;
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
    private WordPredictor? _wordPredictor;
    private string? _appDataDir;

    /// <summary>
    /// Gets the word predictor instance used for keyboard predictions, if enabled.
    /// </summary>
    public WordPredictor? WordPredictor => _wordPredictor;

    public void Initialize(EventCoordinationContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EyeCtrl");

        SetupPropertyChangedHandlers();
        SetupMessageHandlers();
        SetupGazeRouting();
    }

    private void SetupPropertyChangedHandlers()
    {
        if (_context == null) return;

        var cursorCircle = _context.OverlayManager.CursorCircle;
        var activationButton = _context.OverlayManager.ActivationButton;
        var zoomOverlay = _context.OverlayManager.ZoomOverlay;
        var zoomSideButtons = _context.OverlayManager.ZoomSideButtons;
        var zoomIndicator = _context.OverlayManager.ZoomIndicator;
        var actionMenu = _context.OverlayManager.ActionMenu;
        var toolbar = _context.OverlayManager.Toolbar;

        // CursorCircle: VM.Position -> window position, VM.IsVisible -> show/hide
        _context.CursorVM.PropertyChanged += OnCursorVMPropertyChanged;

        // ActivationButton: VM -> show/hide/dwell
        _context.ActivationVM.PropertyChanged += OnActivationVMPropertyChanged;

        // ZoomOverlay: VM -> show/hide/reposition
        _context.ZoomVM.PropertyChanged += OnZoomVMPropertyChanged;

        // ZoomGazeIndicator: VM property changes -> window updates
        _context.ZoomIndicatorVM.PropertyChanged += OnZoomIndicatorVMPropertyChanged;

        // ZoomSideButtons: VM -> show/hide/progress
        _context.ZoomVM.PropertyChanged += OnZoomSideButtonsPropertyChanged;

        // ActionMenu: VM -> show/hide
        _context.ActionMenuVM.PropertyChanged += OnActionMenuVMPropertyChanged;

        // Toolbar: VM state changes -> toolbar state
        _context.ToolbarVM.PropertyChanged += OnToolbarVMPropertyChanged;
    }

    private void SetupMessageHandlers()
    {
        if (_context == null) return;

        var im = _context.InteractionManager;
        var zoomOverlay = _context.OverlayManager.ZoomOverlay;
        var zoomIndicator = _context.OverlayManager.ZoomIndicator;
        var keyboard = _context.OverlayManager.Keyboard;

        // ZoomDwellCompleted: Handle coordinate conversion
        _context.Messenger.Register<EventCoordinator, ZoomDwellCompletedMessage>(this, (_, msg) =>
            _context.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    zoomIndicator.Hide();
                    var realPoint = zoomOverlay.GetRealPhysicalPoint(msg.PrecisePoint);
                    im.OnZoomDwell(realPoint);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error handling zoom dwell completion");
                }
            }));

        // ResetZoomIndicator: Trigger animation
        _context.Messenger.Register<EventCoordinator, ResetZoomIndicatorMessage>(this, (_, msg) =>
            _context.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (!zoomIndicator.IsVisible) return;
                    var targetDiameter = _context.Settings.Zoom.GazeIndicatorOuterDiameter;
                    zoomIndicator.AnimateReset(targetDiameter, _context.ZoomIndicatorVM.ResetAnimationMs);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error animating zoom indicator reset");
                }
            }));

        // ZoomCloseMessage -> exit zoom mode
        _context.Messenger.Register<EventCoordinator, ZoomCloseMessage>(this, (_, _) =>
            _context.Dispatcher.BeginInvoke(() => im.OnZoomBack()));

        // ZoomRecaptureMessage -> re-capture at same fixation point
        _context.Messenger.Register<EventCoordinator, ZoomRecaptureMessage>(this, (_, _) =>
            _context.Dispatcher.BeginInvoke(() =>
            {
                if (im.CurrentState == InteractionState.ZoomSelectionActive && im.FixationPoint != null)
                {
                    _context.Messenger.Send(new StartZoomMessage(
                        im.FixationPoint.Value,
                        _context.Settings.Zoom.CircleRadiusPixels,
                        _context.Settings.Zoom.MagnificationLevel,
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

    private void OnZoomVMPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_context == null) return;

        var zoomOverlay = _context.OverlayManager.ZoomOverlay;
        var cursorCircle = _context.OverlayManager.CursorCircle;
        var activationButton = _context.OverlayManager.ActivationButton;
        var zoomIndicator = _context.OverlayManager.ZoomIndicator;

        if (e.PropertyName == nameof(ZoomOverlayViewModel.Center) && _context.ZoomVM.IsVisible)
        {
            zoomOverlay.UpdateCenter(_context.ZoomVM.Center, _context.ZoomVM.Radius, _context.ZoomVM.Magnification);
        }
        else if (e.PropertyName == nameof(ZoomOverlayViewModel.IsVisible))
        {
            if (_context.ZoomVM.IsVisible)
            {
                cursorCircle.Hide();
                activationButton.HideImmediate();
                zoomOverlay.Show(_context.ZoomVM.Center, _context.ZoomVM.Radius, _context.ZoomVM.Magnification, _context.ZoomVM.GazePoint);
                zoomIndicator.Show();
            }
            else
            {
                zoomOverlay.Hide();
                zoomIndicator.Hide();
            }
        }
    }

    private void OnZoomIndicatorVMPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_context == null) return;

        var zoomIndicator = _context.OverlayManager.ZoomIndicator;
        switch (e.PropertyName)
        {
            case nameof(ZoomGazeIndicatorViewModel.IndicatorX):
            case nameof(ZoomGazeIndicatorViewModel.IndicatorY):
                zoomIndicator.UpdatePosition(_context.ZoomIndicatorVM.IndicatorX, _context.ZoomIndicatorVM.IndicatorY);
                break;
            case nameof(ZoomGazeIndicatorViewModel.OuterCircleDiameter):
                zoomIndicator.UpdateOuterDiameter(_context.ZoomIndicatorVM.OuterCircleDiameter);
                break;
            case nameof(ZoomGazeIndicatorViewModel.IsVisible):
                if (_context.ZoomIndicatorVM.IsVisible) zoomIndicator.Show();
                else zoomIndicator.Hide();
                break;
        }
    }

    private void OnZoomSideButtonsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_context == null) return;

        var zoomSideButtons = _context.OverlayManager.ZoomSideButtons;
        switch (e.PropertyName)
        {
            case nameof(ZoomOverlayViewModel.IsVisible):
                if (_context.ZoomVM.IsVisible)
                    zoomSideButtons.ShowAt(_context.ZoomVM.CloseButtonCenter, _context.ZoomVM.RecaptureButtonCenter);
                else
                    zoomSideButtons.Hide();
                break;
            case nameof(ZoomOverlayViewModel.CloseButtonProgress):
                zoomSideButtons.UpdateProgress("zoom_close", _context.ZoomVM.CloseButtonProgress);
                break;
            case nameof(ZoomOverlayViewModel.RecaptureButtonProgress):
                zoomSideButtons.UpdateProgress("zoom_recapture", _context.ZoomVM.RecaptureButtonProgress);
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

                actionMenu.Show(
                    _context.ActionMenuVM.Center,
                    _context.Settings.ActionMenu.VisibleActions,
                    _context.ActionMenuVM.ShowBack,
                    _context.ActionMenuVM.ShowBack ? _context.Settings.Dwell.ZoomActionMenuDwellMs : _context.Settings.Dwell.ActionMenuDwellMs,
                    _context.ActionMenuVM.EffectiveRingRadius,
                    _context.Settings.Cursor.CircleSize,
                    _context.Settings.Cursor.CircleColor,
                    _context.Settings.Cursor.PositionIndicatorStyle,
                    _context.ActionMenuVM.ZoomCenter,
                    _context.ActionMenuVM.ZoomRadius,
                    _context.ActionMenuVM.GazePoint);
            }
            else
            {
                actionMenu.Hide();
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
                _context.SettingsService);

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
        _context.InteractionManager.ZoomEnabled = s.Zoom.Enabled;
        _context.OverlayManager.Toolbar.SetZoomState(s.Zoom.Enabled);
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
            "escape" => NativeMethods.VK_ESCAPE,
            "f1" => NativeMethods.VK_F1,
            "f2" => NativeMethods.VK_F2,
            "f3" => NativeMethods.VK_F3,
            "f4" => NativeMethods.VK_F4,
            "f5" => NativeMethods.VK_F5,
            "f6" => NativeMethods.VK_F6,
            "f7" => NativeMethods.VK_F7,
            "f8" => NativeMethods.VK_F8,
            "f9" => NativeMethods.VK_F9,
            "f10" => NativeMethods.VK_F10,
            "f11" => NativeMethods.VK_F11,
            "f12" => NativeMethods.VK_F12,
            "home" => NativeMethods.VK_HOME,
            "end" => NativeMethods.VK_END,
            "pageup" => NativeMethods.VK_PRIOR,
            "pagedown" => NativeMethods.VK_NEXT,
            "arrowup" => NativeMethods.VK_UP,
            "arrowdown" => NativeMethods.VK_DOWN,
            "arrowleft" => NativeMethods.VK_LEFT,
            "arrowright" => NativeMethods.VK_RIGHT,
            "insert" => NativeMethods.VK_INSERT,
            "delete" => NativeMethods.VK_DELETE,
            "printscreen" => NativeMethods.VK_SNAPSHOT,
            _ => 0
        };

        if (vk != 0)
        {
            // Arrow keys, Insert/Delete, Home/End, PageUp/PageDown, and PrintScreen require KEYEVENTF_EXTENDEDKEY
            bool isExtendedKey = vk is NativeMethods.VK_UP or NativeMethods.VK_DOWN or NativeMethods.VK_LEFT or NativeMethods.VK_RIGHT
                or NativeMethods.VK_HOME or NativeMethods.VK_END or NativeMethods.VK_PRIOR or NativeMethods.VK_NEXT
                or NativeMethods.VK_INSERT or NativeMethods.VK_DELETE or NativeMethods.VK_SNAPSHOT;

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

        var pt = msg.IsFromZoom ? msg.Point : (_context.GazeEngine.StableCentroid ?? msg.Point);
        Log.Information("Action: {Action} at ({X:F0}, {Y:F0}) [zoom={IsFromZoom}]",
            msg.Action, pt.X, pt.Y, msg.IsFromZoom);

        switch (msg.Action)
        {
            case InteractionAction.LeftClick:    _context.InputSimulator.LeftClick(pt);        break;
            case InteractionAction.RightClick:   _context.InputSimulator.RightClick(pt);       break;
            case InteractionAction.DoubleClick:  _context.InputSimulator.DoubleClick(pt);      break;
            case InteractionAction.DragStart:    _context.InputSimulator.MouseDown(pt);        break;
            case InteractionAction.DragEnd:      _context.InputSimulator.MouseUp(pt);          break;
            case InteractionAction.ScrollUp:     _context.InputSimulator.Scroll(pt, 0, DefaultScrollAmount);    break;
            case InteractionAction.ScrollDown:   _context.InputSimulator.Scroll(pt, 0, -DefaultScrollAmount);   break;
            case InteractionAction.ScrollLeft:   _context.InputSimulator.Scroll(pt, -DefaultScrollAmount, 0);   break;
            case InteractionAction.ScrollRight:  _context.InputSimulator.Scroll(pt, DefaultScrollAmount, 0);    break;
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

        if (settings.Keyboard.PredictionEnabled)
        {
            if (string.IsNullOrEmpty(settings.Keyboard.LanguagePack))
            {
                settings.Keyboard.LanguagePack = LanguageDetector.Detect();
                settingsService.SaveDebounced(settings);
                Log.Information("Auto-detected language: {Lang}", settings.Keyboard.LanguagePack);
            }

            var langCode = settings.Keyboard.LanguagePack;
            var dictPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "Resources", "Dictionaries", $"{langCode}.json");
            var provider = new FileDictionaryProvider(dictPath, _appDataDir!, langCode);
            _wordPredictor = new WordPredictor(provider, settings.Keyboard.ActiveLayout);

            // Phase 8: Apply decay after loading to clean up old patterns using user settings
            _wordPredictor.ApplyDecay(settings.Keyboard.DecayEnabled, settings.Keyboard.DecayHalfLifeDays);

            keyboard.SetPredictor(_wordPredictor);
        }

        keyboard.SetScreenEdge(settings.Keyboard.ScreenEdge);
        keyboard.SetSettings(settings);

        var wavPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "click.wav");
        keyboard.SetSoundService(new KeyboardSoundService(settings.Keyboard, wavPath));
    }

    #endregion

    public void Dispose()
    {
        if (_disposed || _context == null) return;

        // Unsubscribe from PropertyChanged events
        _context.CursorVM.PropertyChanged -= OnCursorVMPropertyChanged;
        _context.ActivationVM.PropertyChanged -= OnActivationVMPropertyChanged;
        _context.ZoomVM.PropertyChanged -= OnZoomVMPropertyChanged;
        _context.ZoomVM.PropertyChanged -= OnZoomSideButtonsPropertyChanged;
        _context.ZoomIndicatorVM.PropertyChanged -= OnZoomIndicatorVMPropertyChanged;
        _context.ActionMenuVM.PropertyChanged -= OnActionMenuVMPropertyChanged;
        _context.ToolbarVM.PropertyChanged -= OnToolbarVMPropertyChanged;

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

        // Unregister all message handlers
        _context.Messenger.UnregisterAll(this);

        _settingsWindow = null;
        _disposed = true;
    }
}
