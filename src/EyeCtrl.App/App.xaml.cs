namespace EyeCtrl.App;

using System.Drawing;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Views;
using EyeCtrl.Core;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.Platform.Gaze;
using EyeCtrl.Platform.Input;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

public partial class App : Application
{
    private ServiceProvider _serviceProvider = null!;
    private System.Windows.Forms.NotifyIcon _trayIcon = null!;
    private System.Windows.Forms.ToolStripMenuItem _pauseMenuItem = null!;
    private bool _keyboardVisible;
    private WordPredictor? _wordPredictor;
    private string _appDataDir = null!;

    /// <summary>
    /// When true, the app uses MouseGazeProvider and skips the splash delay.
    /// Activated by launching with --test-mode CLI argument.
    /// </summary>
    public static bool IsTestMode { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        IsTestMode = e.Args.Contains("--test-mode", StringComparer.OrdinalIgnoreCase);

        // ── Splash screen ──
        SplashWindow? splash = null;
        if (!IsTestMode)
        {
            splash = new SplashWindow();
            splash.Show();
        }

        // ── Settings ──
        _appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EyeCtrl");
        var settingsService = new SettingsService(Path.Combine(_appDataDir, "settings.json"));
        var settings = settingsService.Load();

        // ── Logging ──
        var logsDir = Path.Combine(_appDataDir, "logs");
        Directory.CreateDirectory(logsDir);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(logsDir, "eyectrl-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception (terminating={IsTerminating})",
                args.IsTerminating);
            Log.CloseAndFlush();
        };

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Unhandled dispatcher exception");
        };

        // ── Build DI container ──
        var services = new ServiceCollection();

        // Logging
        services.AddLogging(builder => builder.AddSerilog(dispose: true));

        // Messenger (singleton)
        services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);

        // Settings (singleton instances)
        services.AddSingleton(settings);
        services.AddSingleton(settings.Cursor);
        services.AddSingleton(settings.Dwell);
        services.AddSingleton(settings.Zoom);
        services.AddSingleton(settings.ActionMenu);
        services.AddSingleton(settings.Keyboard);
        services.AddSingleton(settings.Toolbar);
        services.AddSingleton(settings.Theme);
        services.AddSingleton(settings.General);
        services.AddSingleton(settingsService);

        // Platform services
        services.AddSingleton<IInputSimulator, Win32InputSimulator>();
        services.AddSingleton<IGazeProvider>(sp =>
        {
            if (IsTestMode)
                return new MouseGazeProvider();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var factory = new GazeProviderFactory(
                loggerFactory.CreateLogger<GazeProviderFactory>(),
                loggerFactory);
            return factory.Create(settings.General.GazeProviderType,
                SystemParameters.PrimaryScreenWidth,
                SystemParameters.PrimaryScreenHeight);
        });

        // Core engine
        services.AddSingleton<GazeEngine>();

        // ViewModels (singleton -- each registers message handlers in constructor)
        services.AddSingleton<CursorCircleViewModel>();
        services.AddSingleton<ActivationButtonViewModel>();
        services.AddSingleton<ZoomOverlayViewModel>();
        services.AddSingleton<ActionMenuViewModel>();
        services.AddSingleton<ToolbarViewModel>();
        services.AddSingleton<KeyboardViewModel>();
        services.AddSingleton(sp => new SettingsViewModel(
            sp.GetRequiredService<EyeCtrlSettings>(),
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<IMessenger>()));

        // GazeEngineAdapter (bridges Core events <-> MVVM messages)
        services.AddSingleton(sp => new GazeEngineAdapter(
            sp.GetRequiredService<GazeEngine>(),
            sp.GetRequiredService<EyeCtrlSettings>(),
            sp.GetRequiredService<IMessenger>(),
            Dispatcher,
            sp.GetRequiredService<ILogger<GazeEngineAdapter>>()));

        _serviceProvider = services.BuildServiceProvider();

        // ── Resolve services ──
        var gazeEngine = _serviceProvider.GetRequiredService<GazeEngine>();
        var messenger = _serviceProvider.GetRequiredService<IMessenger>();
        var inputSimulator = _serviceProvider.GetRequiredService<IInputSimulator>();
        var gazeProvider = _serviceProvider.GetRequiredService<IGazeProvider>();
        var activeProviderType = GazeProviderFactory.GetProviderType(gazeProvider);
        var version = System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version?.ToString() ?? "unknown";
        Log.Information("Startup: EyeCtrl v{Version}, provider={Provider}, settings={SettingsPath}",
            version, activeProviderType,
            Path.Combine(_appDataDir, "settings.json"));
        var im = gazeEngine.InteractionManager;

        // Resolve ViewModels (triggers RegisterAll in constructors)
        var cursorVM = _serviceProvider.GetRequiredService<CursorCircleViewModel>();
        var activationVM = _serviceProvider.GetRequiredService<ActivationButtonViewModel>();
        var zoomVM = _serviceProvider.GetRequiredService<ZoomOverlayViewModel>();
        var actionMenuVM = _serviceProvider.GetRequiredService<ActionMenuViewModel>();
        var toolbarVM = _serviceProvider.GetRequiredService<ToolbarViewModel>();
        var keyboardVM = _serviceProvider.GetRequiredService<KeyboardViewModel>();

        // Resolve adapter (triggers Core event subscriptions)
        _ = _serviceProvider.GetRequiredService<GazeEngineAdapter>();

        // ── Create overlay windows ──
        var cursorCircle = new CursorCircleWindow();
        var activationButton = new ActivationButtonWindow();
        var zoomOverlay = new ZoomOverlayWindow();
        var zoomSideButtons = new ZoomSideButtonWindow();
        var actionMenu = new ActionMenuWindow();
        var toolbar = new ToolbarWindow();
        var keyboard = new KeyboardWindow();

        // Set DataContext
        cursorCircle.DataContext = cursorVM;
        activationButton.DataContext = activationVM;
        zoomOverlay.DataContext = zoomVM;
        actionMenu.DataContext = actionMenuVM;
        toolbar.DataContext = toolbarVM;
        keyboard.DataContext = keyboardVM;

        // ── Initialize overlays (existing APIs) ──
        cursorCircle.UpdateAppearance(settings.Cursor);
        toolbar.Initialize(settings.Toolbar.Size, settings.Dwell.ToolbarButtonDwellMs);
        toolbar.SetProviderIndicator(activeProviderType);
        LoadKeyboard(keyboard, settings, inputSimulator);

        // ── Bridge ViewModel -> Overlay Windows via PropertyChanged ──

        // CursorCircle: VM.Position -> window position, VM.IsVisible -> show/hide
        cursorVM.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(CursorCircleViewModel.Position):
                    cursorCircle.UpdatePosition(cursorVM.Position);
                    break;
                case nameof(CursorCircleViewModel.IsVisible):
                    if (cursorVM.IsVisible) cursorCircle.Show();
                    else cursorCircle.Hide();
                    break;
            }
        };

        // ActivationButton: VM -> show/hide/dwell
        activationVM.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(ActivationButtonViewModel.IsVisible):
                    if (activationVM.IsVisible)
                    {
                        // Defensive: ensure action menu is hidden
                        actionMenu.Hide();

                        var circleRadius = settings.Cursor.CircleSize / 2;
                        var buttonOffset = circleRadius + 55;
                        activationButton.Show();
                        activationButton.ShowAt(activationVM.Position, buttonOffset);
                    }
                    else
                    {
                        activationButton.HideImmediate();
                    }
                    break;
                case nameof(ActivationButtonViewModel.DwellProgress):
                    activationButton.UpdateDwellProgress(activationVM.DwellProgress);
                    break;
            }
        };

        // ZoomOverlay: VM -> show/hide/reposition with fixation tracking
        Action<ScreenPoint, long>? zoomGazeHandler = null;
        System.ComponentModel.PropertyChangedEventHandler? zoomVisibilityHandler = null;
        zoomVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ZoomOverlayViewModel.Center) && zoomVM.IsVisible)
            {
                // Zoom action menu appeared at a clamped position — realign the overlay
                zoomOverlay.UpdateCenter(zoomVM.Center, zoomVM.Radius, zoomVM.Magnification);
            }
            else if (e.PropertyName == nameof(ZoomOverlayViewModel.IsVisible))
            {
                if (zoomVM.IsVisible)
                {
                    // Defensive: ensure conflicting overlays are hidden
                    cursorCircle.Hide();
                    activationButton.HideImmediate();

                    zoomOverlay.Show(zoomVM.Center, zoomVM.Radius, zoomVM.Magnification);

                    // Unsubscribe previous handlers to prevent accumulation
                    if (zoomGazeHandler != null)
                        gazeEngine.CursorMoved -= zoomGazeHandler;
                    if (zoomVisibilityHandler != null)
                        zoomVM.PropertyChanged -= zoomVisibilityHandler;

                    // Set up zoom fixation detection
                    var zoomFixation = new FixationDetector(
                        settings.Dwell.FixationRadiusPixels / settings.Zoom.MagnificationLevel,
                        settings.Dwell.FixationDetectionMs);
                    zoomFixation.FixationStarted += zoomPoint =>
                    {
                        // GetRealScreenPoint accesses WPF DependencyProperties (Left, Width)
                        // and must run on the UI thread — move the entire body into BeginInvoke
                        Dispatcher.BeginInvoke(() =>
                        {
                            var realPoint = zoomOverlay.GetRealScreenPoint(zoomPoint);
                            im.OnZoomFixation(realPoint);
                        });
                    };

                    // Feed gaze data to zoom fixation detector when in zoom mode
                    var zoomStartTimestamp = 0L;
                    var gracePeriodUs = settings.Zoom.ZoomGracePeriodMs * 1000L;
                    var graceStarted = false;

                    zoomGazeHandler = (pt, ts) =>
                    {
                        if (im.CurrentState != InteractionState.ZoomSelectionActive) return;

                        // Record the first timestamp as zoom start
                        if (!graceStarted)
                        {
                            zoomStartTimestamp = ts;
                            graceStarted = true;
                        }

                        // Skip gaze data during grace period
                        if ((ts - zoomStartTimestamp) < gracePeriodUs) return;

                        zoomFixation.Update(pt, ts);
                    };
                    gazeEngine.CursorMoved += zoomGazeHandler;

                    // Unsubscribe CursorMoved when zoom ends
                    zoomVisibilityHandler = (_, e2) =>
                    {
                        if (e2.PropertyName == nameof(ZoomOverlayViewModel.IsVisible) && !zoomVM.IsVisible)
                            gazeEngine.CursorMoved -= zoomGazeHandler;
                    };
                    zoomVM.PropertyChanged += zoomVisibilityHandler;
                }
                else
                {
                    zoomOverlay.Hide();
                }
            }
        };

        // ZoomSideButtons: VM -> show/hide/progress
        zoomVM.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(ZoomOverlayViewModel.IsVisible):
                    if (zoomVM.IsVisible)
                        zoomSideButtons.ShowAt(zoomVM.CloseButtonCenter, zoomVM.RecaptureButtonCenter);
                    else
                        zoomSideButtons.Hide();
                    break;
                case nameof(ZoomOverlayViewModel.CloseButtonProgress):
                    zoomSideButtons.UpdateProgress("zoom_close", zoomVM.CloseButtonProgress);
                    break;
                case nameof(ZoomOverlayViewModel.RecaptureButtonProgress):
                    zoomSideButtons.UpdateProgress("zoom_recapture", zoomVM.RecaptureButtonProgress);
                    break;
            }
        };

        im.HideZoom += () => Dispatcher.BeginInvoke(() =>
        {
            zoomOverlay.Hide();
            zoomSideButtons.Hide();
        });

        // ZoomCloseMessage -> exit zoom mode
        messenger.Register<ZoomCloseMessage>(this, (_, _) => Dispatcher.BeginInvoke(() =>
        {
            im.OnZoomBack();
        }));

        // ZoomRecaptureMessage -> re-capture at the same fixation point
        messenger.Register<ZoomRecaptureMessage>(this, (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (im.CurrentState == InteractionState.ZoomSelectionActive && im.FixationPoint != null)
            {
                // Re-trigger StartZoom to re-capture
                messenger.Send(new StartZoomMessage(
                    im.FixationPoint,
                    settings.Zoom.CircleRadiusPixels,
                    settings.Zoom.MagnificationLevel));
            }
        }));

        // ActionMenu: VM -> show/hide (still needs UpdateGaze for its internal DwellDetector rendering)
        actionMenuVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ActionMenuViewModel.IsVisible))
            {
                if (actionMenuVM.IsVisible)
                {
                    // Defensive: ensure conflicting overlays are hidden
                    cursorCircle.Hide();
                    activationButton.HideImmediate();

                    actionMenu.Show(actionMenuVM.Center,
                        settings.ActionMenu.VisibleActions,
                        actionMenuVM.ShowBack,
                        actionMenuVM.ShowBack ? settings.Dwell.ZoomActionMenuDwellMs : settings.Dwell.ActionMenuDwellMs,
                        actionMenuVM.EffectiveRingRadius,
                        settings.Cursor.CircleSize,
                        settings.Cursor.CircleColor,
                        settings.Cursor.PositionIndicatorStyle);
                }
                else
                {
                    actionMenu.Hide();
                }
            }
        };

        // Toolbar: VM state changes -> toolbar state
        toolbarVM.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(ToolbarViewModel.IsPaused):
                    toolbar.SetPausedState(toolbarVM.IsPaused);
                    break;
            }
        };

        // ── Message subscriptions for things NOT in ViewModels ──

        // Keyboard toggle (needs GazeEngine.SuppressFixation)
        messenger.Register<KeyboardToggledMessage>(this, (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _keyboardVisible = !_keyboardVisible;
            Log.Information("Keyboard {State}", _keyboardVisible ? "shown" : "hidden");
            if (_keyboardVisible)
            {
                keyboard.Show();
                gazeEngine.SuppressFixation = true;
                im.OnFixationEnded();
            }
            else
            {
                keyboard.Hide();
                gazeEngine.SuppressFixation = false;
            }
        }));

        // Settings window (created on demand)
        SettingsWindow? settingsWindow = null;
        messenger.Register<SettingsRequestedMessage>(this, (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (settingsWindow != null) return;
            settingsWindow = new SettingsWindow(settings, settingsService);
            settingsWindow.SettingsChanged += s =>
            {
                cursorCircle.UpdateAppearance(s.Cursor);
                toolbar.Initialize(s.Toolbar.Size, s.Dwell.ToolbarButtonDwellMs);
                toolbar.SetMode(s.Toolbar.IsDocked, s.Toolbar.DockedEdge);
                im.ZoomEnabled = s.Zoom.Enabled;
                toolbar.SetZoomState(s.Zoom.Enabled);
                LoadKeyboard(keyboard, s, inputSimulator);
                Log.Information("Settings saved");
            };
            settingsWindow.Closed += (_, _) => settingsWindow = null;
            settingsWindow.Show();
        }));

        // Key activation -> input simulator
        messenger.Register<KeyActivatedMessage>(this, (_, msg) => Dispatcher.BeginInvoke(() =>
        {
            switch (msg.Key)
            {
                case "\b": inputSimulator.SendKeyPress(0x08); break;
                case "\n": inputSimulator.SendKeyPress(0x0D); break;
                case "\t": inputSimulator.SendKeyPress(0x09); break;
                default: inputSimulator.SendText(msg.Key); break;
            }
        }));

        // Prediction -> input simulator
        messenger.Register<PredictionSelectedMessage>(this, (_, msg) => Dispatcher.BeginInvoke(() =>
        {
            inputSimulator.SendText(msg.Word + " ");
        }));

        // Action selected -> input simulator
        messenger.Register<ActionRequestedMessage>(this, (_, msg) => Dispatcher.BeginInvoke(() =>
        {
            // Zoom: use the precise real-screen coordinate from GetRealScreenPoint
            // Normal: use centroid tracker for refined accuracy, fall back to fixation point
            var pt = msg.IsFromZoom ? msg.Point : (gazeEngine.StableCentroid ?? msg.Point);
            Log.Information("Action: {Action} at ({X:F0}, {Y:F0}) [zoom={IsFromZoom}]",
                msg.Action, pt.X, pt.Y, msg.IsFromZoom);
            switch (msg.Action)
            {
                case InteractionAction.LeftClick:    inputSimulator.LeftClick(pt);        break;
                case InteractionAction.RightClick:   inputSimulator.RightClick(pt);       break;
                case InteractionAction.DoubleClick:  inputSimulator.DoubleClick(pt);      break;
                case InteractionAction.DragStart:    inputSimulator.MouseDown(pt);        break;
                case InteractionAction.DragEnd:      inputSimulator.MouseUp(pt);          break;
                case InteractionAction.ScrollUp:     inputSimulator.Scroll(pt, 0, 3);    break;
                case InteractionAction.ScrollDown:   inputSimulator.Scroll(pt, 0, -3);   break;
                case InteractionAction.ScrollLeft:   inputSimulator.Scroll(pt, -3, 0);   break;
                case InteractionAction.ScrollRight:  inputSimulator.Scroll(pt, 3, 0);    break;
            }
        }));

        // ── Gaze routing for overlays that still need direct UpdateGaze ──
        gazeEngine.CursorMoved += (point, timestamp) => Dispatcher.BeginInvoke(() =>
        {
            // Toolbar position reporting (VM handles dwell via GazeCursorMovedMessage)
            toolbar.UpdateGaze(point, timestamp);

            // Keyboard gaze (still uses window-level dwell)
            if (_keyboardVisible && keyboard.IsVisible)
                keyboard.UpdateGaze(point, timestamp);

            // Settings gaze (still uses window-level dwell)
            if (settingsWindow?.IsVisible == true)
                settingsWindow.UpdateGaze(point, timestamp);
        });

        // ── System tray ──
        SetupTrayIcon(im);

        // ── Close splash and show persistent overlays ──
        splash?.Close();
        cursorCircle.Show();
        toolbar.Show();
        toolbar.SetMode(settings.Toolbar.IsDocked, settings.Toolbar.DockedEdge);
        toolbar.SetZoomState(settings.Zoom.Enabled);
        toolbar.BindToViewModel();

        if (settings.General.StartPaused)
            im.TogglePause();

        gazeEngine.Start();
    }

    private void LoadKeyboard(KeyboardWindow keyboard, EyeCtrlSettings settings, IInputSimulator inputSimulator)
    {
        var layoutName = settings.Keyboard.ActiveLayout.ToLowerInvariant();
        var layoutPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            "Resources", "Layouts", $"{layoutName}.json");

        if (File.Exists(layoutPath))
        {
            var layout = KeyboardLayoutLoader.Load(layoutPath);
            keyboard.LoadLayout(layout,
                settings.Keyboard.KeySizeMultiplier,
                settings.Dwell.KeyboardKeyDwellMs,
                settings.Dwell.KeyboardPredictionDwellMs);

            var emojiPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Emojis", "emojis.json");
            var recentPath = Path.Combine(_appDataDir, "recent_emojis.json");
            keyboard.SetEmojiData(emojiPath, recentPath);
            keyboard.SetSkinTone(settings.Keyboard.EmojiSkinTone);
        }

        if (settings.Keyboard.PredictionEnabled)
        {
            var dictPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "Resources", "Dictionaries", $"{settings.Keyboard.LanguagePack}.json");
            var userDictPath = Path.Combine(_appDataDir, "user_dictionary.json");
            var provider = new FileDictionaryProvider(dictPath, userDictPath);
            _wordPredictor = new WordPredictor(provider);
            keyboard.SetPredictor(_wordPredictor);
        }

        keyboard.SetScreenEdge(settings.Keyboard.ScreenEdge);

        var wavPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "click.wav");
        keyboard.SetSoundService(new KeyboardSoundService(settings.Keyboard, wavPath));
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
        settingsItem.Click += (_, _) => Dispatcher.BeginInvoke(() =>
            WeakReferenceMessenger.Default.Send(new SettingsRequestedMessage()));

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

        _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(() =>
            WeakReferenceMessenger.Default.Send(new SettingsRequestedMessage()));

        im.StateChanged += state => Dispatcher.BeginInvoke(() =>
        {
            _pauseMenuItem.Text = state == InteractionState.Paused ? "Resume" : "Pause";
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Shutdown");
        _trayIcon.Visible = false;
        _trayIcon.Dispose();

        var gazeEngine = _serviceProvider.GetRequiredService<GazeEngine>();
        var settings = _serviceProvider.GetRequiredService<EyeCtrlSettings>();
        var settingsService = _serviceProvider.GetRequiredService<SettingsService>();

        gazeEngine.Stop();
        settingsService.Save(settings);
        _wordPredictor?.SaveUserDictionary();

        var adapter = _serviceProvider.GetRequiredService<GazeEngineAdapter>();
        adapter.Dispose();
        gazeEngine.Dispose();
        _serviceProvider.Dispose();

        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
