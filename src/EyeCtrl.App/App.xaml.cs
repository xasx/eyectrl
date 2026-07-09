namespace EyeCtrl.App;

using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Calibration;
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
using EyeCtrl.EyeCare;
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
    private const long MicrosecondsPerMillisecond = MathConstants.MicrosecondsPerMillisecond; // Time conversion factor
    private const int DpiStandardScale = 96; // Standard Windows DPI (100% scaling)

    private ServiceProvider _serviceProvider = null!;
    private TrayIconManager _trayIconManager = null!;
    private EventCoordinator _eventCoordinator = null!;
    private string _appDataDir = null!;

    /// <summary>
    /// When true, the app uses MouseGazeProvider and skips the splash delay.
    /// Activated by launching with --test-mode CLI argument.
    /// </summary>
    public static bool IsTestMode { get; private set; }

    /// <summary>
    /// When true, each raw <see cref="Core.GazeEngine.GazeSampled"/> point is logged with its
    /// validity and an on/off-screen classification per screen edge. This is a THROWAWAY probe
    /// to validate the off-screen-valid-coordinate assumption behind the toolbar auto-hide
    /// summon gesture (see docs/superpowers/specs/2026-07-09-toolbar-auto-hide-design.md).
    /// Activated by launching with --log-gaze. Remove once the assumption is settled.
    /// </summary>
    public static bool LogGaze { get; private set; }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        IsTestMode = e.Args.Contains("--test-mode", StringComparer.OrdinalIgnoreCase);
        LogGaze = e.Args.Contains("--log-gaze", StringComparer.OrdinalIgnoreCase);

        SplashWindow? splash = null;
        try
        {
            // ── Splash screen ──
            if (!IsTestMode)
            {
                splash = new SplashWindow();
                splash.Show();
            }

            InitializeDpi(splash);

            var (settings, settingsFilePath) = InitializeSettingsAndLogging();

            // ── Build DI container ──
            _serviceProvider = AppBootstrapper.BuildServiceProvider(settings, settingsFilePath, Dispatcher, IsTestMode);

            // ── Resolve services ──
            var settingsService = _serviceProvider.GetRequiredService<SettingsService>();
            var gazeEngine = _serviceProvider.GetRequiredService<GazeEngine>();
            var messenger = _serviceProvider.GetRequiredService<IMessenger>();
            var inputSimulator = _serviceProvider.GetRequiredService<IInputSimulator>();
            var gazeProvider = _serviceProvider.GetRequiredService<IGazeProvider>();
            var activeProviderType = GazeProviderFactory.GetProviderType(gazeProvider);
            var version = System.Reflection.Assembly.GetExecutingAssembly()
                .GetName().Version?.ToString() ?? "unknown";
            Log.Information("Startup: EyeCtrl v{Version}, provider={Provider}, settings={SettingsPath}",
                version, activeProviderType, settingsFilePath);
            var im = gazeEngine.InteractionManager;

            // ── Resolve ViewModels ──
            var (cursorVm, activationVm, manualSelectionVm, actionMenuVm, toolbarVm, keyboardVm,
                 manualSelectionIndicatorVm, backdropVm, calibrationVm, targetHighlightVm, edgeButtonBarVm) = ResolveViewModels(settings);

            // Resolve adapter (triggers Core event subscriptions)
            _ = _serviceProvider.GetRequiredService<GazeEngineAdapter>();

            // Resolve ManualSelectionDwellService (triggers message subscriptions)
            _ = _serviceProvider.GetRequiredService<ManualSelectionDwellService>();

            // ── Initialize overlay windows via OverlayManager ──
            var overlayManager = _serviceProvider.GetRequiredService<IOverlayManager>();
            var viewModels = new OverlayViewModels(cursorVm, activationVm, manualSelectionVm, actionMenuVm, toolbarVm, keyboardVm, manualSelectionIndicatorVm, calibrationVm, targetHighlightVm, edgeButtonBarVm);
            var (cursorCircle, toolbar) = InitializeOverlays(overlayManager, viewModels, settings, activeProviderType);

            // Load calibration. Screen size must be in PHYSICAL pixels to match what
            // CompleteSession persists (logical DIPs here would discard calibration on any non-100% DPI).
            var calibrationPath = Path.Combine(_appDataDir, "calibration.json");
            var screen = new PhysicalPoint(
                SystemParameters.PrimaryScreenWidth * DpiHelper.ScaleX,
                SystemParameters.PrimaryScreenHeight * DpiHelper.ScaleY);
            var monitorName = System.Windows.Forms.Screen.PrimaryScreen?.DeviceName ?? "";
            _serviceProvider.GetRequiredService<CalibrationService>().Load(calibrationPath, screen, monitorName);

            // ── Event Coordination ──
            // All PropertyChanged and message handlers are now managed by EventCoordinator
            _eventCoordinator = new EventCoordinator();
            var noteManager = _serviceProvider.GetRequiredService<INoteManager>();
            var dragTrailVm = _serviceProvider.GetRequiredService<DragTrailViewModel>();
            var coordinationContext = new EventCoordinationContext(
                cursorVm, activationVm, manualSelectionVm, manualSelectionIndicatorVm, actionMenuVm, toolbarVm, keyboardVm, backdropVm, dragTrailVm,
                overlayManager, gazeEngine, im, messenger, Dispatcher, settingsService, settings, targetHighlightVm, inputSimulator,
                _serviceProvider.GetRequiredService<CalibrationService>(),
                noteManager);
            _eventCoordinator.Initialize(coordinationContext);

            var modelRegistry = _serviceProvider.GetRequiredService<ModelRegistryService>();
            var modelDownload = _serviceProvider.GetRequiredService<ModelDownloadService>();
            var modelManagement = _serviceProvider.GetRequiredService<ModelManagementService>();
            _eventCoordinator.SetModelServices(modelRegistry, modelDownload, modelManagement);

            // Load keyboard using EventCoordinator
            _eventCoordinator.LoadKeyboard(overlayManager.Keyboard, settings, inputSimulator, settingsService);

            // ── System tray ──
            _trayIconManager = new TrayIconManager(im, Dispatcher, messenger, () => Shutdown());

            // ── Close splash and show persistent overlays (last, after all wiring) ──
            ShowOverlays(splash, cursorCircle, toolbar, settings);

            if (settings.General.StartPaused)
                im.TogglePause();

            gazeEngine.Start();

            // External dwell pipe server (cooperating-app control activation).
            _serviceProvider.GetRequiredService<ExternalDwellService>().Start();

            // Eye-care break reminder: subscribe to raw gaze samples for scheduler + fatigue monitor.
            // Resolving IEyeCareService constructs the singleton and its EyeCareHooks,
            // which registers for GazeCursorMovedMessage to feed gaze into overlays.
            // Subscription lives for the process lifetime (eyeCare + gazeEngine are app-lifetime
            // singletons), so no explicit unsubscribe is needed.
            var eyeCare = _serviceProvider.GetRequiredService<IEyeCareService>();
            gazeEngine.GazeSampled += sample =>
            {
                // canInterrupt only in FreeLook — never surface a break over the action menu,
                // keyboard, or manual-selection overlay (the break is held until FreeLook).
                eyeCare.OnGazeSample(
                    sample,
                    isPaused: im.CurrentState == InteractionState.Paused,
                    canInterrupt: im.CurrentState == InteractionState.FreeLook);
            };

            // THROWAWAY PROBE (--log-gaze): classify each raw sample against the physical screen
            // bounds to settle whether the Tobii Stream Engine emits VALID coordinates past the
            // edges (the assumption behind the toolbar auto-hide summon gesture). Logs to Serilog
            // and the console. Remove this block once the assumption is confirmed/denied.
            // See docs/superpowers/specs/2026-07-09-toolbar-auto-hide-design.md (Open question #1).
            if (LogGaze)
            {
                var screenW = screen.X;
                var screenH = screen.Y;
                const double margin = 50.0; // matches the spec's OffScreenMarginPx dead-band
                Log.Information(
                    "GAZE-PROBE: screen={W}x{H}px (physical), off-screen margin={Margin}px",
                    screenW, screenH, margin);
                gazeEngine.GazeSampled += sample =>
                {
                    if (!sample.IsValid)
                    {
                        Log.Information("GAZE-PROBE: INVALID t={T}", sample.TimestampMicroseconds);
                        return;
                    }

                    // Classify which edges (if any) the valid sample lies past.
                    var pastTop = sample.Y < -margin;
                    var pastBottom = sample.Y > screenH + margin;
                    var pastLeft = sample.X < -margin;
                    var pastRight = sample.X > screenW + margin;
                    var offScreen = pastTop || pastBottom || pastLeft || pastRight;
                    var classification = offScreen
                        ? $"OFF-SCREEN[{(pastTop ? "T" : "")}{(pastBottom ? "B" : "")}{(pastLeft ? "L" : "")}{(pastRight ? "R" : "")}]"
                        : "on-screen";

                    // Mark samples just inside the edge band too, in case the fallback summon
                    // predicate (edge-band-inside) is needed instead.
                    var nearEdgeBand = 50.0;
                    var nearTop = sample.Y >= 0 && sample.Y < nearEdgeBand;
                    var nearBottom = sample.Y <= screenH && sample.Y > screenH - nearEdgeBand;
                    var nearLeft = sample.X >= 0 && sample.X < nearEdgeBand;
                    var nearRight = sample.X <= screenW && sample.X > screenW - nearEdgeBand;
                    var near = nearTop || nearBottom || nearLeft || nearRight;
                    var nearTag = near
                        ? $" NEAR[{(nearTop ? "T" : "")}{(nearBottom ? "B" : "")}{(nearLeft ? "L" : "")}{(nearRight ? "R" : "")}]"
                        : "";

                    Log.Information(
                        "GAZE-PROBE: VALID t={T} x={X:F1} y={Y:F1} {Class}{Near}",
                        sample.TimestampMicroseconds, sample.X, sample.Y, classification, nearTag);
                };
            }
        }
        catch (Exception ex)
        {
            splash?.Close();
            Log.Fatal(ex, "Failed to start EyeCtrl");
            Log.CloseAndFlush();
            MessageBox.Show(
                $"EyeCtrl failed to start:\n{ex.Message}",
                "EyeCtrl — Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Shutdown");

        _trayIconManager?.Dispose();
        _eventCoordinator?.Dispose();

        if (_serviceProvider != null)
        {
            var gazeEngine = _serviceProvider.GetRequiredService<GazeEngine>();
            var settings = _serviceProvider.GetRequiredService<EyeCtrlSettings>();
            var settingsService = _serviceProvider.GetRequiredService<SettingsService>();

            gazeEngine.Stop();
            settingsService.Save(settings);
            // (User dictionary is saved by EventCoordinator.Dispose above, via PredictionStackFactory.)

            var adapter = _serviceProvider.GetRequiredService<GazeEngineAdapter>();
            adapter.Dispose();
            gazeEngine.Dispose();
            _serviceProvider.GetRequiredService<ExternalDwellService>().Dispose();
            _serviceProvider.Dispose(); // Disposes all IDisposable singletons including ManualSelectionDwellService
        }

        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private (CursorCircleViewModel CursorVm,
             ActivationButtonViewModel ActivationVm,
             ManualSelectionOverlayViewModel ManualSelectionVm,
             ActionMenuViewModel ActionMenuVm,
             ToolbarViewModel ToolbarVm,
             KeyboardViewModel KeyboardVm,
             ManualSelectionGazeIndicatorViewModel ManualSelectionIndicatorVm,
             GlassBackdropViewModel BackdropVm,
             CalibrationViewModel CalibrationVm,
             TargetHighlightOverlayViewModel TargetHighlightVm,
             EdgeButtonBarViewModel EdgeButtonBarVm) ResolveViewModels(EyeCtrlSettings settings)
    {
        var vms = AppBootstrapper.ResolveViewModels(_serviceProvider, settings);
        var backdropVm = _serviceProvider.GetRequiredService<GlassBackdropViewModel>();
        return (vms.CursorCircle, vms.ActivationButton, vms.ManualSelectionOverlay, vms.ActionMenu,
                vms.Toolbar, vms.Keyboard, vms.ManualSelectionIndicator, backdropVm,
                vms.Calibration, vms.TargetHighlight, vms.EdgeButtonBar);
    }

    private (CursorCircleWindow CursorCircle, ToolbarWindow Toolbar) InitializeOverlays(
        IOverlayManager overlayManager,
        OverlayViewModels viewModels,
        EyeCtrlSettings settings,
        GazeProviderType providerType)
    {
        overlayManager.Initialize(viewModels, settings, providerType);
        return (overlayManager.CursorCircle, overlayManager.Toolbar);
    }

    private static void ShowOverlays(
        SplashWindow? splash,
        CursorCircleWindow cursorCircle,
        ToolbarWindow toolbar,
        EyeCtrlSettings settings)
    {
        splash?.Close();
        cursorCircle.Show();
        toolbar.Show();
        toolbar.SetMode(settings.Toolbar.IsDocked, settings.Toolbar.DockedEdge);
        toolbar.SetSelectionModeState(settings.SelectionMode);
        toolbar.BindToViewModel();
    }

    /// <summary>
    /// Initializes <see cref="DpiHelper"/> from the splash window's composition target.
    /// In test mode no splash is shown, so it falls back to the Win32 system DPI.
    /// </summary>
    private static void InitializeDpi(SplashWindow? splash)
    {
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
    }

    /// <summary>
    /// Resolves the app data directory (stored in <see cref="_appDataDir"/>), loads settings
    /// while migrating newly-introduced defaults, initializes Serilog, and registers the global
    /// (AppDomain + dispatcher) exception handlers.
    /// </summary>
    private (EyeCtrlSettings Settings, string SettingsFilePath) InitializeSettingsAndLogging()
    {
        _appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EyeCtrl");
        var settingsFilePath = Path.Combine(_appDataDir, "settings.json");
        var tempSettingsService = new SettingsService(settingsFilePath);
        var settings = tempSettingsService.Load();

        // Migrate settings for newly-introduced features
        settings.ActionMenu.EnsureDefaults();

        var logsDir = Path.Combine(_appDataDir, "logs");
        LoggingInitializer.Initialize(logsDir);

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

        return (settings, settingsFilePath);
    }
}
