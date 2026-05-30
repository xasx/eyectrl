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
            version, activeProviderType,
            Path.Combine(_appDataDir, "settings.json"));
        var im = gazeEngine.InteractionManager;

        // Resolve ViewModels (triggers RegisterAll in constructors)
        var cursorVM = _serviceProvider.GetRequiredService<CursorCircleViewModel>();
        var activationVM = _serviceProvider.GetRequiredService<ActivationButtonViewModel>();
        var ManualSelectionVM = _serviceProvider.GetRequiredService<ManualSelectionOverlayViewModel>();
        var actionMenuVM = _serviceProvider.GetRequiredService<ActionMenuViewModel>();
        var toolbarVM = _serviceProvider.GetRequiredService<ToolbarViewModel>();
        var keyboardVM = _serviceProvider.GetRequiredService<KeyboardViewModel>();
        var ManualSelectionIndicatorVM = _serviceProvider.GetRequiredService<ManualSelectionGazeIndicatorViewModel>();
        var backdropVM = _serviceProvider.GetRequiredService<GlassBackdropViewModel>();
        var calibrationVM = _serviceProvider.GetRequiredService<CalibrationViewModel>();

        // Resolve adapter (triggers Core event subscriptions)
        _ = _serviceProvider.GetRequiredService<GazeEngineAdapter>();

        // Resolve ManualSelectionDwellService (triggers message subscriptions)
        _ = _serviceProvider.GetRequiredService<ManualSelectionDwellService>();

        // Configure ManualSelection indicator with settings
        ManualSelectionIndicatorVM.Configure(settings);

        // ── Initialize overlay windows via OverlayManager ──
        var overlayManager = _serviceProvider.GetRequiredService<IOverlayManager>();
        var viewModels = new OverlayViewModels(cursorVM, activationVM, ManualSelectionVM, actionMenuVM, toolbarVM, keyboardVM, ManualSelectionIndicatorVM, calibrationVM);
        overlayManager.Initialize(viewModels, settings, activeProviderType);

        // Load calibration
        var calibrationPath = Path.Combine(_appDataDir, "calibration.json");
        var screen = new PhysicalPoint(
            SystemParameters.PrimaryScreenWidth,
            SystemParameters.PrimaryScreenHeight);
        var monitorName = System.Windows.Forms.Screen.PrimaryScreen?.DeviceName ?? "";
        _serviceProvider.GetRequiredService<CalibrationService>().Load(calibrationPath, screen, monitorName);

        // Get references to overlay windows for event handlers
        var cursorCircle = overlayManager.CursorCircle;
        var toolbar = overlayManager.Toolbar;

        // ── Event Coordination ──
        // All PropertyChanged and message handlers are now managed by EventCoordinator
        _eventCoordinator = new EventCoordinator();
        var coordinationContext = new EventCoordinationContext(
            cursorVM, activationVM, ManualSelectionVM, ManualSelectionIndicatorVM, actionMenuVM, toolbarVM, keyboardVM, backdropVM,
            overlayManager, gazeEngine, im, messenger, Dispatcher, settingsService, settings, inputSimulator);
        _eventCoordinator.Initialize(coordinationContext);

        var modelRegistry = _serviceProvider.GetRequiredService<ModelRegistryService>();
        var modelDownload = _serviceProvider.GetRequiredService<ModelDownloadService>();
        var modelManagement = _serviceProvider.GetRequiredService<ModelManagementService>();
        _eventCoordinator.SetModelServices(modelRegistry, modelDownload, modelManagement);

        // Load keyboard using EventCoordinator
        _eventCoordinator.LoadKeyboard(overlayManager.Keyboard, settings, inputSimulator, settingsService);

        // ── System tray ──
        _trayIconManager = new TrayIconManager(im, Dispatcher, () => Shutdown());

        // ── Close splash and show persistent overlays ──
        splash?.Close();
        cursorCircle.Show();
        toolbar.Show();
        toolbar.SetMode(settings.Toolbar.IsDocked, settings.Toolbar.DockedEdge);
        toolbar.SetManualSelectionState(settings.ManualSelection.Enabled);
        toolbar.BindToViewModel();

        if (settings.General.StartPaused)
            im.TogglePause();

        gazeEngine.Start();
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
            _eventCoordinator?.WordPredictor?.SaveUserDictionary();

            var adapter = _serviceProvider.GetRequiredService<GazeEngineAdapter>();
            adapter.Dispose();
            gazeEngine.Dispose();
            _serviceProvider.Dispose(); // Disposes all IDisposable singletons including ManualSelectionDwellService
        }

        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
