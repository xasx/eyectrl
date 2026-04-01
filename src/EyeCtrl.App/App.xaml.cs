namespace EyeCtrl.App;

using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Services;
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
    // UI Layout Constants
    private const int ActivationButtonOffsetPixels = 55; // Distance from cursor circle edge to activation button center (logical DIPs)
    private const int RetainedLogFileCount = 7; // Number of daily log files to keep
    private const long MicrosecondsPerMillisecond = 1000L; // Time conversion factor
    private const int DpiStandardScale = 96; // Standard Windows DPI (100% scaling)
    private const int DefaultScrollAmount = 3; // Default mouse wheel scroll delta
    private const int BackspaceVirtualKeyCode = 0x08;
    private const int EnterVirtualKeyCode = 0x0D;
    private const int TabVirtualKeyCode = 0x09;
    private const int ScreenCaptureDelayMs = 100; // Delay before capturing screen to let WPF render
    private const int FadeInAnimationMs = 150; // Animation duration for activation button fade-in
    private const int FadeOutAnimationMs = 100; // Animation duration for activation button fade-out

    private ServiceProvider _serviceProvider = null!;
    private System.Windows.Forms.NotifyIcon _trayIcon = null!;
    private System.Windows.Forms.ToolStripMenuItem _pauseMenuItem = null!;
    private Icon? _customTrayIcon;
    private bool _keyboardVisible;
    private WordPredictor? _wordPredictor;
    private string _appDataDir = null!;

    // Event handlers for cleanup
    private System.ComponentModel.PropertyChangedEventHandler? _cursorVmPropertyChangedHandler;
    private System.ComponentModel.PropertyChangedEventHandler? _activationVmPropertyChangedHandler;
    private System.ComponentModel.PropertyChangedEventHandler? _zoomVmPropertyChangedHandler;
    private System.ComponentModel.PropertyChangedEventHandler? _zoomVmSideButtonsPropertyChangedHandler;
    private System.ComponentModel.PropertyChangedEventHandler? _actionMenuVmPropertyChangedHandler;
    private System.ComponentModel.PropertyChangedEventHandler? _toolbarVmPropertyChangedHandler;

    /// <summary>
    /// When true, the app uses MouseGazeProvider and skips the splash delay.
    /// Activated by launching with --test-mode CLI argument.
    /// </summary>
    public static bool IsTestMode { get; private set; }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

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

        // ── DPI initialization ──
        if (splash != null)
        {
            var source = PresentationSource.FromVisual(splash);
            if (source?.CompositionTarget != null)
            {
                var m = source.CompositionTarget.TransformToDevice;
                DpiHelper.Initialize(m.M11, m.M22);
            }
        }
        if (IsTestMode)
        {
            var dpi = GetDpiForSystem();
            var scale = dpi / (double)DpiStandardScale;
            DpiHelper.Initialize(scale, scale);
        }

        // ── Settings ──
        _appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EyeCtrl");
        var settingsFilePath = Path.Combine(_appDataDir, "settings.json");
        var tempSettingsService = new SettingsService(settingsFilePath);
        var settings = tempSettingsService.Load();

        // ── Logging ──
        var logsDir = Path.Combine(_appDataDir, "logs");
        Directory.CreateDirectory(logsDir);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(logsDir, "eyectrl-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: RetainedLogFileCount,
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
        // Only register root settings - sub-settings accessed via properties
        services.AddSingleton(settings);
        services.AddSingleton(sp => new SettingsService(
            settingsFilePath,
            sp.GetRequiredService<ILogger<SettingsService>>()));

        // Platform services
        services.AddSingleton<IMouseCoordinateMapper, MouseCoordinateMapper>();
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
                SystemParameters.PrimaryScreenWidth * DpiHelper.ScaleX,
                SystemParameters.PrimaryScreenHeight * DpiHelper.ScaleY);
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
        services.AddSingleton<ZoomGazeIndicatorViewModel>();
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
        var zoomIndicatorVM = _serviceProvider.GetRequiredService<ZoomGazeIndicatorViewModel>();

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
        var zoomIndicator = new ZoomGazeIndicatorWindow();
        zoomIndicator.Configure(
            settings.Zoom.GazeIndicatorOuterDiameter,
            settings.Zoom.GazeIndicatorCenterDotDiameter,
            settings.Zoom.GazeIndicatorStrokeThickness,
            settings.Zoom.GazeIndicatorOpacity);

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
        LoadKeyboard(keyboard, settings, inputSimulator, settingsService);

        // ── Bridge ViewModel -> Overlay Windows via PropertyChanged ──

        bool settingsOpen = false;

        // CursorCircle: VM.Position -> window position, VM.IsVisible -> show/hide
        _cursorVmPropertyChangedHandler = (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(CursorCircleViewModel.Position):
                    cursorCircle.UpdatePosition(cursorVM.Position);
                    break;
                case nameof(CursorCircleViewModel.IsVisible):
                    if (cursorVM.IsVisible && !settingsOpen) cursorCircle.Show();
                    else cursorCircle.Hide();
                    break;
            }
        };
        cursorVM.PropertyChanged += _cursorVmPropertyChangedHandler;

        // ActivationButton: VM -> show/hide/dwell
        _activationVmPropertyChangedHandler = (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(ActivationButtonViewModel.IsVisible):
                    if (activationVM.IsVisible)
                    {
                        // Defensive: ensure action menu is hidden
                        actionMenu.Hide();

                        var circleRadius = settings.Cursor.CircleSize / 2;
                        var buttonOffset = circleRadius + ActivationButtonOffsetPixels;
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
        activationVM.PropertyChanged += _activationVmPropertyChangedHandler;

        // ZoomOverlay: VM -> show/hide/reposition with fixation tracking
        Action<ScreenPoint, long>? zoomGazeHandler = null;
        System.ComponentModel.PropertyChangedEventHandler? zoomVisibilityHandler = null;
        _zoomVmPropertyChangedHandler = (_, e) =>
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

                    zoomOverlay.Show(zoomVM.Center, zoomVM.Radius, zoomVM.Magnification, zoomVM.GazePoint);
                    zoomIndicator.Show();

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
                            try
                            {
                                zoomIndicator.Hide();
                                var realPoint = zoomOverlay.GetRealScreenPoint(zoomPoint);
                                im.OnZoomFixation(realPoint);
                            }
                            catch (Exception ex)
                            {
                                Log.Error(ex, "Error in zoom fixation started handler");
                            }
                        });
                    };
                    zoomFixation.FixationEnded += () =>
                    {
                        Dispatcher.BeginInvoke(() =>
                        {
                            try
                            {
                                if (!zoomIndicator.IsVisible) return; // already hidden by fixation completion
                                zoomIndicatorVM.ResetFixation();
                                zoomIndicator.AnimateReset(
                                    settings.Zoom.GazeIndicatorOuterDiameter,
                                    settings.Zoom.GazeIndicatorResetAnimationMs);
                            }
                            catch (Exception ex)
                            {
                                Log.Error(ex, "Error in zoom fixation ended handler");
                            }
                        });
                    };

                    // Feed gaze data to zoom fixation detector when in zoom mode
                    var zoomStartTimestamp = 0L;
                    var gracePeriodUs = settings.Zoom.ZoomGracePeriodMs * MicrosecondsPerMillisecond;
                    var graceStarted = false;

                    zoomGazeHandler = (pt, ts) =>
                    {
                        if (im.CurrentState != InteractionState.ZoomSelectionActive) return;

                        // Update indicator position (even during grace period)
                        Dispatcher.BeginInvoke(() =>
                        {
                            try
                            {
                                zoomIndicatorVM.UpdateGazePosition(pt);
                                zoomIndicator.UpdatePosition(zoomIndicatorVM.IndicatorX, zoomIndicatorVM.IndicatorY);
                            }
                            catch (Exception ex)
                            {
                                Log.Error(ex, "Error updating zoom indicator position");
                            }
                        });

                        // Record the first timestamp as zoom start
                        if (!graceStarted)
                        {
                            zoomStartTimestamp = ts;
                            graceStarted = true;
                        }

                        // Skip gaze data during grace period
                        if ((ts - zoomStartTimestamp) < gracePeriodUs) return;

                        // Reject gaze outside the zoom circle.
                        // pt and zoomVM.Center are both physical px (gaze coords / FixationPoint, never scaled).
                        // CircleRadiusPixels is logical DIP, so multiply by ScaleX to match.
                        var physicalZoomRadius = settings.Zoom.CircleRadiusPixels * DpiHelper.ScaleX;
                        var dx = pt.X - zoomVM.Center.X;
                        var dy = pt.Y - zoomVM.Center.Y;
                        if (dx * dx + dy * dy > physicalZoomRadius * physicalZoomRadius)
                        {
                            zoomFixation.Reset();
                            // FixationDetector.Reset() does NOT fire FixationEnded, so reset UI manually:
                            Dispatcher.BeginInvoke(() =>
                            {
                                try
                                {
                                    zoomIndicatorVM.ResetFixation();
                                    zoomIndicator.AnimateReset(
                                        settings.Zoom.GazeIndicatorOuterDiameter,
                                        settings.Zoom.GazeIndicatorResetAnimationMs);
                                }
                                catch (Exception ex)
                                {
                                    Log.Error(ex, "Error resetting zoom indicator after gaze moved outside");
                                }
                            });
                            return;
                        }

                        zoomFixation.Update(pt, ts);
                        // Update fixation progress on indicator
                        var progress = zoomFixation.GetProgress();
                        Dispatcher.BeginInvoke(() =>
                        {
                            try
                            {
                                zoomIndicatorVM.UpdateFixationProgress(progress);
                                zoomIndicator.UpdateOuterDiameter(zoomIndicatorVM.OuterCircleDiameter);
                            }
                            catch (Exception ex)
                            {
                                Log.Error(ex, "Error updating zoom fixation progress");
                            }
                        });
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
                    zoomIndicator.Hide();
                }
            }
        };
        zoomVM.PropertyChanged += _zoomVmPropertyChangedHandler;

        // ZoomSideButtons: VM -> show/hide/progress
        _zoomVmSideButtonsPropertyChangedHandler = (_, e) =>
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
        zoomVM.PropertyChanged += _zoomVmSideButtonsPropertyChangedHandler;

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
                    settings.Zoom.MagnificationLevel,
                    im.FixationPoint));
            }
        }));

        // ActionMenu: VM -> show/hide (still needs UpdateGaze for its internal DwellDetector rendering)
        _actionMenuVmPropertyChangedHandler = (_, e) =>
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
                        settings.Cursor.PositionIndicatorStyle,
                        actionMenuVM.ZoomCenter,
                        actionMenuVM.ZoomRadius,
                        actionMenuVM.GazePoint);
                }
                else
                {
                    actionMenu.Hide();
                }
            }
        };
        actionMenuVM.PropertyChanged += _actionMenuVmPropertyChangedHandler;

        // Toolbar: VM state changes -> toolbar state
        _toolbarVmPropertyChangedHandler = (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(ToolbarViewModel.IsPaused):
                    toolbar.SetPausedState(toolbarVM.IsPaused);
                    break;
            }
        };
        toolbarVM.PropertyChanged += _toolbarVmPropertyChangedHandler;

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
            settingsOpen = true;
            cursorCircle.Hide();
            try
            {
                settingsWindow = new SettingsWindow(settings, settingsService);
                settingsWindow.SettingsChanged += s =>
                {
                    cursorCircle.UpdateAppearance(s.Cursor);
                    toolbar.Initialize(s.Toolbar.Size, s.Dwell.ToolbarButtonDwellMs);
                    toolbar.SetMode(s.Toolbar.IsDocked, s.Toolbar.DockedEdge);
                    im.ZoomEnabled = s.Zoom.Enabled;
                    toolbar.SetZoomState(s.Zoom.Enabled);
                    LoadKeyboard(keyboard, s, inputSimulator, settingsService);
                    Log.Information("Settings saved");
                };
                settingsWindow.Closed += (_, _) =>
                {
                    settingsWindow = null;
                    settingsOpen = false;
                    if (cursorVM.IsVisible) cursorCircle.Show();
                };
                settingsWindow.Show();
            }
            catch
            {
                settingsOpen = false;
                if (cursorVM.IsVisible) cursorCircle.Show();
                throw;
            }
        }));

        // Key activation -> input simulator
        messenger.Register<KeyActivatedMessage>(this, (_, msg) => Dispatcher.BeginInvoke(() =>
        {
            switch (msg.Key)
            {
                case "\b": inputSimulator.SendKeyPress(BackspaceVirtualKeyCode); break;
                case "\n": inputSimulator.SendKeyPress(EnterVirtualKeyCode); break;
                case "\t": inputSimulator.SendKeyPress(TabVirtualKeyCode); break;
                default: inputSimulator.SendText(msg.Key); break;
            }
        }));

        // Prediction -> input simulator
        messenger.Register<PredictionSelectedMessage>(this, (_, msg) => Dispatcher.BeginInvoke(() =>
        {
            for (int i = 0; i < msg.DeleteCount; i++)
                inputSimulator.SendKeyPress(BackspaceVirtualKeyCode); // Backspace to erase typed prefix
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
                case InteractionAction.ScrollUp:     inputSimulator.Scroll(pt, 0, DefaultScrollAmount);    break;
                case InteractionAction.ScrollDown:   inputSimulator.Scroll(pt, 0, -DefaultScrollAmount);   break;
                case InteractionAction.ScrollLeft:   inputSimulator.Scroll(pt, -DefaultScrollAmount, 0);   break;
                case InteractionAction.ScrollRight:  inputSimulator.Scroll(pt, DefaultScrollAmount, 0);    break;
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
            if (settingsWindow?.IsVisible ?? false)
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

    private void LoadKeyboard(KeyboardWindow keyboard, EyeCtrlSettings settings,
        IInputSimulator inputSimulator, SettingsService settingsService)
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
                settings.Dwell.KeyboardPredictionDwellMs,
                settings.Dwell.DwellCooldownMs);

            var emojiPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Emojis", "emojis.json");
            var recentPath = Path.Combine(_appDataDir, "recent_emojis.json");
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
            var provider = new FileDictionaryProvider(dictPath, _appDataDir, langCode);
            _wordPredictor = new WordPredictor(provider, settings.Keyboard.ActiveLayout);
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

        // Load custom icon from Assets directory
        var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "eyectrl.ico");
        Icon trayIcon = SystemIcons.Application;

        if (File.Exists(iconPath))
        {
            try
            {
                _customTrayIcon = new Icon(iconPath);
                trayIcon = _customTrayIcon;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load custom icon from {Path}, using system default", iconPath);
            }
        }

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = trayIcon,
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
        _customTrayIcon?.Dispose();

        var gazeEngine = _serviceProvider.GetRequiredService<GazeEngine>();
        var settings = _serviceProvider.GetRequiredService<EyeCtrlSettings>();
        var settingsService = _serviceProvider.GetRequiredService<SettingsService>();

        gazeEngine.Stop();
        settingsService.Save(settings);
        _wordPredictor?.SaveUserDictionary();

        // Unsubscribe from PropertyChanged events to prevent memory leaks
        var cursorVM = _serviceProvider.GetRequiredService<CursorCircleViewModel>();
        var activationVM = _serviceProvider.GetRequiredService<ActivationButtonViewModel>();
        var zoomVM = _serviceProvider.GetRequiredService<ZoomOverlayViewModel>();
        var actionMenuVM = _serviceProvider.GetRequiredService<ActionMenuViewModel>();
        var toolbarVM = _serviceProvider.GetRequiredService<ToolbarViewModel>();

        if (_cursorVmPropertyChangedHandler != null)
            cursorVM.PropertyChanged -= _cursorVmPropertyChangedHandler;
        if (_activationVmPropertyChangedHandler != null)
            activationVM.PropertyChanged -= _activationVmPropertyChangedHandler;
        if (_zoomVmPropertyChangedHandler != null)
            zoomVM.PropertyChanged -= _zoomVmPropertyChangedHandler;
        if (_zoomVmSideButtonsPropertyChangedHandler != null)
            zoomVM.PropertyChanged -= _zoomVmSideButtonsPropertyChangedHandler;
        if (_actionMenuVmPropertyChangedHandler != null)
            actionMenuVM.PropertyChanged -= _actionMenuVmPropertyChangedHandler;
        if (_toolbarVmPropertyChangedHandler != null)
            toolbarVM.PropertyChanged -= _toolbarVmPropertyChangedHandler;

        // Dispose all ViewModels to unregister message handlers
        (cursorVM as IDisposable)?.Dispose();
        (activationVM as IDisposable)?.Dispose();
        (zoomVM as IDisposable)?.Dispose();
        (actionMenuVM as IDisposable)?.Dispose();
        (toolbarVM as IDisposable)?.Dispose();
        (_serviceProvider.GetRequiredService<KeyboardViewModel>() as IDisposable)?.Dispose();
        (_serviceProvider.GetRequiredService<ZoomGazeIndicatorViewModel>() as IDisposable)?.Dispose();
        (_serviceProvider.GetRequiredService<SettingsViewModel>() as IDisposable)?.Dispose();

        var adapter = _serviceProvider.GetRequiredService<GazeEngineAdapter>();
        adapter.Dispose();
        gazeEngine.Dispose();
        _serviceProvider.Dispose();

        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
