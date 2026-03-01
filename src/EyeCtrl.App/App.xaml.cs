namespace EyeCtrl.App;

using System.Drawing;
using System.IO;
using System.Windows;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Views;
using EyeCtrl.Core;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.Platform.Gaze;
using EyeCtrl.Platform.Input;

public partial class App : Application
{
    // Core services
    private SettingsService _settingsService = null!;
    private EyeCtrlSettings _settings = null!;
    private GazeEngine _gazeEngine = null!;
    private IInputSimulator _inputSimulator = null!;

    // Overlays
    private CursorCircleWindow _cursorCircle = null!;
    private ActivationButtonWindow _activationButton = null!;
    private ActionMenuWindow _actionMenu = null!;
    private ToolbarWindow _toolbar = null!;
    private ZoomOverlayWindow _zoomOverlay = null!;
    private KeyboardWindow _keyboard = null!;
    private SettingsWindow? _settingsWindow;

    // Activation button dwell tracking
    private readonly DwellDetector _activationDwell = new();

    // Zoom fixation tracking
    private FixationDetector? _zoomFixation;

    // Prediction
    private WordPredictor? _wordPredictor;

    // System tray
    private System.Windows.Forms.NotifyIcon _trayIcon = null!;
    private System.Windows.Forms.ToolStripMenuItem _pauseMenuItem = null!;

    // State
    private string _appDataDir = null!;
    private bool _keyboardVisible;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ── Splash screen ──
        var splash = new SplashWindow();
        splash.Show();

        // ── Settings ──
        _appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "EyeCtrl");
        _settingsService = new SettingsService(Path.Combine(_appDataDir, "settings.json"));
        _settings = _settingsService.Load();

        // ── Platform services ──
        IGazeProvider gazeProvider;
        try
        {
            gazeProvider = new TobiiInteractionLibProvider(
                SystemParameters.PrimaryScreenWidth,
                SystemParameters.PrimaryScreenHeight);
        }
        catch
        {
            // Tobii not available — fall back to mouse-based gaze simulation
            gazeProvider = new MouseGazeProvider();
        }
        _inputSimulator = new Win32InputSimulator();

        // ── GazeEngine (creates filter, fixation detector, InteractionManager) ──
        _gazeEngine = new GazeEngine(gazeProvider, _inputSimulator, _settings);
        var im = _gazeEngine.InteractionManager;

        // ── Create overlay windows ──
        _cursorCircle = new CursorCircleWindow();
        _activationButton = new ActivationButtonWindow();
        _actionMenu = new ActionMenuWindow();
        _toolbar = new ToolbarWindow();
        _zoomOverlay = new ZoomOverlayWindow();
        _keyboard = new KeyboardWindow();

        // ── Initialize overlays ──
        _cursorCircle.UpdateAppearance(_settings.Cursor);
        _toolbar.Initialize(_settings.Toolbar.Size, _settings.Dwell.ToolbarButtonDwellMs);
        LoadKeyboard();

        // ── Wire activation button dwell detector ──
        _activationDwell.TargetActivated += _ =>
            Dispatcher.BeginInvoke(() => im.OnActivationButtonDwelled());
        _activationDwell.TargetProgress += (_, progress) =>
            Dispatcher.BeginInvoke(() => _activationButton.UpdateDwellProgress(progress));

        // ── Wire InteractionManager → overlay windows ──

        im.ShowActivationButton += point => Dispatcher.BeginInvoke(() =>
        {
            // Place activation button just outside the cursor circle (above it)
            var circleRadius = _settings.Cursor.CircleSize / 2;
            var buttonOffset = circleRadius + 55; // half button (50) + 5px gap
            _activationButton.Show();
            _activationButton.ShowAt(point, buttonOffset);
            _activationDwell.ClearTargets();
            _activationDwell.AddTarget(new DwellTarget("activate",
                new ScreenPoint(point.X, point.Y - buttonOffset), 50,
                _settings.Dwell.ActivationButtonDwellMs));
        });

        im.HideActivationButton += () => Dispatcher.BeginInvoke(() =>
        {
            _activationButton.HideImmediate();
            _activationDwell.ClearTargets();
        });

        im.ShowActionMenu += point => Dispatcher.BeginInvoke(() =>
        {
            var showBack = im.CurrentState == InteractionState.ZoomActionMenuVisible;
            var dwellMs = showBack
                ? _settings.Dwell.ZoomActionMenuDwellMs
                : _settings.Dwell.ActionMenuDwellMs;
            _actionMenu.Show(point, _settings.ActionMenu.VisibleActions, showBack, dwellMs);
        });

        im.HideActionMenu += () => Dispatcher.BeginInvoke(() => _actionMenu.Hide());

        im.StartZoom += point => Dispatcher.BeginInvoke(() =>
        {
            _zoomOverlay.Show(point,
                _settings.Zoom.CircleRadiusPixels,
                _settings.Zoom.MagnificationLevel);

            _zoomFixation = new FixationDetector(
                _settings.Dwell.FixationRadiusPixels / _settings.Zoom.MagnificationLevel,
                _settings.Dwell.FixationDetectionMs);
            _zoomFixation.FixationStarted += zoomPoint =>
            {
                var realPoint = _zoomOverlay.GetRealScreenPoint(zoomPoint);
                Dispatcher.BeginInvoke(() => im.OnZoomFixation(realPoint));
            };
        });

        im.EndZoom += () => Dispatcher.BeginInvoke(() =>
        {
            _zoomOverlay.Hide();
            _zoomFixation = null;
        });

        im.StateChanged += state => Dispatcher.BeginInvoke(() =>
        {
            _toolbar.SetPausedState(state == InteractionState.Paused);
            if (state == InteractionState.Paused)
                _cursorCircle.Hide();
            else
                _cursorCircle.Show();
        });

        // ── Wire ActionMenu → InteractionManager ──
        _actionMenu.ActionSelected += action => im.OnActionSelected(action);
        _actionMenu.BackSelected += () => im.OnZoomBack();

        // ── Wire Toolbar events ──
        _toolbar.PauseToggled += () => im.TogglePause();
        _toolbar.ZoomToggled += () =>
        {
            im.ZoomEnabled = !im.ZoomEnabled;
            _settings.Zoom.Enabled = im.ZoomEnabled;
            _toolbar.SetZoomState(im.ZoomEnabled);
        };
        _toolbar.KeyboardToggled += () => Dispatcher.BeginInvoke(ToggleKeyboard);
        _toolbar.SettingsRequested += () => Dispatcher.BeginInvoke(OpenSettings);

        // ── Wire Keyboard events → InputSimulator ──
        _keyboard.KeyActivated += text =>
        {
            switch (text)
            {
                case "\b": _inputSimulator.SendKeyPress(0x08); break; // VK_BACK
                case "\n": _inputSimulator.SendKeyPress(0x0D); break; // VK_RETURN
                case "\t": _inputSimulator.SendKeyPress(0x09); break; // VK_TAB
                default: _inputSimulator.SendText(text); break;
            }
        };
        _keyboard.PredictionSelected += word =>
        {
            _inputSimulator.SendText(word + " ");
        };

        // ── Wire gaze to all overlays ──
        _gazeEngine.CursorMoved += (point, timestamp) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                // Cursor circle follows gaze only when not paused
                if (im.CurrentState != InteractionState.Paused)
                    _cursorCircle.UpdatePosition(point);

                // Toolbar always receives gaze
                _toolbar.UpdateGaze(point, timestamp);

                // Action menu receives gaze when visible
                if (_actionMenu.IsVisible)
                    _actionMenu.UpdateGaze(point, timestamp);

                // Keyboard receives gaze when visible
                if (_keyboardVisible && _keyboard.IsVisible)
                    _keyboard.UpdateGaze(point, timestamp);

                // Settings window receives gaze when open
                if (_settingsWindow?.IsVisible == true)
                    _settingsWindow.UpdateGaze(point, timestamp);

                // Activation button dwell tracking during Fixation state
                if (im.CurrentState == InteractionState.Fixation)
                    _activationDwell.Update(point, timestamp);

                // Zoom fixation tracking during ZoomActive state
                if (im.CurrentState == InteractionState.ZoomActive && _zoomFixation != null)
                    _zoomFixation.Update(point, timestamp);
            });
        };

        // ── System tray icon ──
        SetupTrayIcon(im);

        // ── Close splash and show persistent overlays ──
        splash.Close();
        _cursorCircle.Show();
        _toolbar.Show();
        _toolbar.SetMode(_settings.Toolbar.IsDocked, _settings.Toolbar.DockedEdge);
        _toolbar.SetZoomState(_settings.Zoom.Enabled);

        if (_settings.General.StartPaused)
            im.TogglePause();

        _gazeEngine.Start();
    }

    private void LoadKeyboard()
    {
        var layoutName = _settings.Keyboard.ActiveLayout.ToLowerInvariant();
        var layoutPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            "Resources", "Layouts", $"{layoutName}.json");

        if (File.Exists(layoutPath))
        {
            var layout = KeyboardLayoutLoader.Load(layoutPath);
            _keyboard.LoadLayout(layout,
                _settings.Keyboard.KeySizeMultiplier,
                _settings.Dwell.KeyboardKeyDwellMs,
                _settings.Dwell.KeyboardPredictionDwellMs);

            // Emoji data paths (lazy-loaded on first use)
            var emojiPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Emojis", "emojis.json");
            var recentPath = Path.Combine(_appDataDir, "recent_emojis.json");
            _keyboard.SetEmojiData(emojiPath, recentPath);
            _keyboard.SetSkinTone(_settings.Keyboard.EmojiSkinTone);
        }

        if (_settings.Keyboard.PredictionEnabled)
        {
            var dictPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "Resources", "Dictionaries", $"{_settings.Keyboard.LanguagePack}.json");
            var userDictPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "EyeCtrl", "user_dictionary.json");

            var provider = new FileDictionaryProvider(dictPath, userDictPath);
            _wordPredictor = new WordPredictor(provider);
            _keyboard.SetPredictor(_wordPredictor);
        }

        _keyboard.SetScreenEdge(_settings.Keyboard.ScreenEdge);
    }

    private void ToggleKeyboard()
    {
        _keyboardVisible = !_keyboardVisible;
        if (_keyboardVisible)
        {
            _keyboard.Show();
            // Suppress fixation detection so the activation button doesn't appear over the keyboard
            _gazeEngine.SuppressFixation = true;
            _gazeEngine.InteractionManager.OnFixationEnded(); // dismiss any active fixation
        }
        else
        {
            _keyboard.Hide();
            _gazeEngine.SuppressFixation = false;
        }
    }

    private void OpenSettings()
    {
        if (_settingsWindow != null) return;

        _settingsWindow = new SettingsWindow(_settings, _settingsService);
        _settingsWindow.SettingsChanged += OnSettingsSaved;
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private void OnSettingsSaved(EyeCtrlSettings settings)
    {
        // Update overlay appearances with new settings
        _cursorCircle.UpdateAppearance(settings.Cursor);

        _toolbar.Initialize(settings.Toolbar.Size, settings.Dwell.ToolbarButtonDwellMs);
        _toolbar.SetMode(settings.Toolbar.IsDocked, settings.Toolbar.DockedEdge);

        _gazeEngine.InteractionManager.ZoomEnabled = settings.Zoom.Enabled;
        _toolbar.SetZoomState(settings.Zoom.Enabled);

        // Reload keyboard with new settings
        LoadKeyboard();
    }

    private void SetupTrayIcon(InteractionManager im)
    {
        _pauseMenuItem = new System.Windows.Forms.ToolStripMenuItem("Pause");
        _pauseMenuItem.Click += (_, _) =>
        {
            im.TogglePause();
            _pauseMenuItem.Text = im.CurrentState == InteractionState.Paused ? "Resume" : "Pause";
        };

        var settingsItem = new System.Windows.Forms.ToolStripMenuItem("Settings");
        settingsItem.Click += (_, _) => Dispatcher.BeginInvoke(OpenSettings);

        var exitItem = new System.Windows.Forms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => Dispatcher.BeginInvoke(() => Shutdown());

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(_pauseMenuItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "EyeCtrl",
            Visible = true,
            ContextMenuStrip = menu
        };

        _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(OpenSettings);

        // Keep pause menu in sync with toolbar toggle
        im.StateChanged += state => Dispatcher.BeginInvoke(() =>
        {
            _pauseMenuItem.Text = state == InteractionState.Paused ? "Resume" : "Pause";
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _gazeEngine?.Stop();
        _keyboard.GetRecentStore()?.Save();
        _settings.Keyboard.EmojiSkinTone = _keyboard.GetSkinTone();
        _settingsService.Save(_settings);
        _wordPredictor?.SaveUserDictionary();
        _gazeEngine?.Dispose();
        base.OnExit(e);
    }
}
