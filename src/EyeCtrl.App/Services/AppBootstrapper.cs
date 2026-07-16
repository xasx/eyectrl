namespace EyeCtrl.App.Services;

using System.Windows.Threading;
using EyeCtrl.App.Overlays;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.Platform.Gaze;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Forms;
using EyeCtrl.Platform.Input;
using EyeCtrl.Platform.Services;
using EyeCtrl.ViewModels;
using EyeCtrl.EyeCare;
using EyeCtrl.EyeCare.Exercises;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

/// <summary>
/// Handles dependency injection container configuration for the application.
/// Extracted from App.xaml.cs to improve Single Responsibility Principle compliance.
/// </summary>
public class AppBootstrapper
{
    /// <summary>
    /// Configures and builds the service provider with all application dependencies.
    /// </summary>
    /// <param name="settings">The loaded application settings</param>
    /// <param name="settingsFilePath">Path to the settings file</param>
    /// <param name="dispatcher">The UI thread dispatcher</param>
    /// <param name="isTestMode">Whether the app is running in test mode</param>
    /// <returns>Configured service provider</returns>
    public static ServiceProvider BuildServiceProvider(
        EyeCtrlSettings settings,
        string settingsFilePath,
        Dispatcher dispatcher,
        bool isTestMode)
    {
        var services = new ServiceCollection();
        var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EyeCtrl");

        // Logging
        services.AddLogging(builder => builder.AddSerilog(dispose: true));

        // Messenger (singleton)
        services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);

        // Settings (singleton instances)
        // Only register root settings - sub-settings accessed via properties
        services.AddSingleton(settings);
        services.AddSingleton(sp => new SettingsService(
            settingsFilePath,
            SettingsWriter.App,
            sp.GetRequiredService<ILogger<SettingsService>>()));

        // Platform services
        services.AddSingleton<IMouseCoordinateMapper, MouseCoordinateMapper>();
        services.AddSingleton<IInputSimulator, Win32InputSimulator>();

        // Startup registration
        services.AddSingleton<IStartupRegistrar, WindowsStartupRegistrar>();

        // Window management (Task Switcher)
        services.AddSingleton<IWindowManager, Win32WindowManager>();
        services.AddSingleton<IAppIconProvider, Win32AppIconProvider>();

        // Quick Actions
        services.AddSingleton<IQuickActionsStore>(sp =>
        {
            var path = Path.Combine(appDataDir, "actions.json");
            return new QuickActionsStore(path);
        });
        services.AddSingleton<IWindowsAppLauncher, WindowsAppLauncher>();
        services.AddSingleton<IInstalledAppProvider, Win32InstalledAppProvider>();
        services.AddSingleton<IGazeProvider>(sp =>
        {
            if (isTestMode)
                return new MouseGazeProvider();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var factory = new GazeProviderFactory(
                loggerFactory.CreateLogger<GazeProviderFactory>(),
                loggerFactory);
            return factory.Create(settings.General.GazeProviderType,
                System.Windows.SystemParameters.PrimaryScreenWidth * DpiHelper.ScaleX,
                System.Windows.SystemParameters.PrimaryScreenHeight * DpiHelper.ScaleY);
        });

        // Model download services
        services.AddSingleton<HttpClient>(_ => new HttpClient { Timeout = TimeSpan.FromMinutes(30) });
        services.AddSingleton(sp =>
        {
            var modelsDir = Path.Combine(appDataDir, "models");

            var manifestPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "models.json");
            ModelManifest manifest;
            if (File.Exists(manifestPath))
            {
                try
                {
                    var json = File.ReadAllText(manifestPath);
                    manifest = JsonSerializer.Deserialize<ModelManifest>(json) ?? new ModelManifest();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to load bundled model manifest from {Path}; falling back to empty manifest", manifestPath);
                    manifest = new ModelManifest();
                }
            }
            else
            {
                manifest = new ModelManifest();
            }

            return new ModelRegistryService(
                modelsDir,
                manifest,
                settings,
                sp.GetRequiredService<ILogger<ModelRegistryService>>());
        });
        services.AddSingleton(sp => new ModelDownloadService(
            Path.Combine(appDataDir, "models"),
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<ILogger<ModelDownloadService>>()));
        services.AddSingleton<ModelManagementService>();

        // Calibration
        services.AddSingleton(sp => new CalibrationService(sp.GetService<ILogger<CalibrationService>>()));

        // ControlFinder (AutoSelection mode)
        services.AddSingleton<IControlFinder, ControlFinder>();

        // Core engine
        services.AddSingleton(sp => new GazeEngine(
            sp.GetRequiredService<IGazeProvider>(),
            sp.GetRequiredService<IInputSimulator>(),
            sp.GetRequiredService<EyeCtrlSettings>(),
            sp.GetRequiredService<CalibrationService>()));

        // ViewModels (singleton -- each registers message handlers in constructor)
        services.AddSingleton<CursorCircleViewModel>();
        services.AddSingleton<ActivationButtonViewModel>();
        services.AddSingleton<ManualSelectionOverlayViewModel>();
        services.AddSingleton<ActionMenuViewModel>();
        services.AddSingleton<ToolbarViewModel>();
        services.AddSingleton<KeyboardViewModel>();
        services.AddSingleton<ManualSelectionGazeIndicatorViewModel>();
        services.AddSingleton<GlassBackdropViewModel>();
        services.AddSingleton(sp => new CalibrationViewModel(
            sp.GetRequiredService<EyeCtrlSettings>().Calibration,
            sp.GetRequiredService<CalibrationService>(),
            sp.GetRequiredService<IMessenger>(),
            sp.GetRequiredService<IGazeProvider>(),
            new PhysicalPoint(
                System.Windows.SystemParameters.PrimaryScreenWidth * DpiHelper.ScaleX,
                System.Windows.SystemParameters.PrimaryScreenHeight * DpiHelper.ScaleY),
            System.Windows.Forms.Screen.PrimaryScreen?.DeviceName ?? "",
            Path.Combine(appDataDir, "calibration.json")));
        services.AddSingleton<TargetHighlightOverlayViewModel>();
        services.AddSingleton<DragTrailViewModel>();
        services.AddSingleton<EdgeButtonBarViewModel>();
        services.AddSingleton<TrackStatusViewModel>();
        services.AddSingleton<TaskSwitcherViewModel>();
        services.AddSingleton<QuickActionsGridViewModel>();

        // GazeEngineAdapter (bridges Core events <-> MVVM messages)
        services.AddSingleton(sp => new GazeEngineAdapter(
            sp.GetRequiredService<GazeEngine>(),
            sp.GetRequiredService<EyeCtrlSettings>(),
            sp.GetRequiredService<IMessenger>(),
            dispatcher,
            sp.GetRequiredService<ILogger<GazeEngineAdapter>>(),
            sp.GetRequiredService<IControlFinder>()));

        // ManualSelectionDwellService (handles ManualSelection dwell detection and indicator updates)
        services.AddSingleton(sp => new ManualSelectionDwellService(
            sp.GetRequiredService<IMessenger>(),
            sp.GetRequiredService<ILogger<ManualSelectionDwellService>>(),
            sp.GetRequiredService<EyeCtrlSettings>(),
            dispatcher,
            sp.GetRequiredService<GazeEngine>().InteractionManager));

        // ExternalDwellService: lets cooperating apps register dwellable controls over a named pipe.
        services.AddSingleton(sp => new ExternalDwellService(
            sp.GetRequiredService<EyeCtrlSettings>().ExternalDwell,
            sp.GetRequiredService<GazeEngine>(),
            dispatcher,
            sp.GetRequiredService<ILogger<ExternalDwellService>>(),
            sp.GetRequiredService<IMessenger>()));

        // NoteManager (pinned notes lifecycle)
        services.AddSingleton<INoteManager>(sp => new NoteManager(
            sp.GetRequiredService<IMessenger>()));

        // OverlayManager (manages all overlay window lifecycle)
        services.AddSingleton<IOverlayManager>(sp => new OverlayManager(
            dispatcher,
            sp.GetRequiredService<ILogger<OverlayManager>>(),
            sp.GetRequiredService<IAppIconProvider>()));

        // Eye-care
        services.AddSingleton(sp => new EyeCareBannerWindow(
            sp.GetRequiredService<EyeCtrlSettings>().Dwell.ActivationButtonDwellMs));
        services.AddSingleton<EyeCareExerciseWindow>();
        services.AddSingleton<ExerciseRunner>(sp =>
        {
            var screen = new PhysicalPoint(
                System.Windows.SystemParameters.PrimaryScreenWidth * DpiHelper.ScaleX / 2,
                System.Windows.SystemParameters.PrimaryScreenHeight * DpiHelper.ScaleY / 2);
            return new ExerciseRunner(BuildExercisePool(screen), seed: Environment.TickCount);
        });
        services.AddSingleton<IEyeCareHooks>(sp => new EyeCareHooks(
            sp.GetRequiredService<IMessenger>(),
            dispatcher,
            sp.GetRequiredService<EyeCareBannerWindow>(),
            sp.GetRequiredService<EyeCareExerciseWindow>(),
            new PhysicalPoint(
                System.Windows.SystemParameters.PrimaryScreenWidth * DpiHelper.ScaleX / 2,
                System.Windows.SystemParameters.PrimaryScreenHeight * DpiHelper.ScaleY / 2),
            sp.GetRequiredService<EyeCtrlSettings>().EyeCare.BannerAutoSnoozeSeconds));
        services.AddSingleton<IEyeCareService, EyeCareService>(sp => new EyeCareService(
            sp.GetRequiredService<EyeCtrlSettings>().EyeCare,
            sp.GetRequiredService<ExerciseRunner>(),
            sp.GetRequiredService<IEyeCareHooks>()));

        return services.BuildServiceProvider();
    }

    public static OverlayViewModels ResolveViewModels(ServiceProvider sp, EyeCtrlSettings settings)
    {
        var manualSelectionIndicatorVm = sp.GetRequiredService<ManualSelectionGazeIndicatorViewModel>();
        manualSelectionIndicatorVm.Configure(settings);

        return new OverlayViewModels(
            CursorCircle: sp.GetRequiredService<CursorCircleViewModel>(),
            ActivationButton: sp.GetRequiredService<ActivationButtonViewModel>(),
            ManualSelectionOverlay: sp.GetRequiredService<ManualSelectionOverlayViewModel>(),
            ActionMenu: sp.GetRequiredService<ActionMenuViewModel>(),
            Toolbar: sp.GetRequiredService<ToolbarViewModel>(),
            Keyboard: sp.GetRequiredService<KeyboardViewModel>(),
            ManualSelectionIndicator: manualSelectionIndicatorVm,
            Calibration: sp.GetRequiredService<CalibrationViewModel>(),
            TargetHighlight: sp.GetRequiredService<TargetHighlightOverlayViewModel>(),
            EdgeButtonBar: sp.GetRequiredService<EdgeButtonBarViewModel>(),
            TrackStatus: sp.GetRequiredService<TrackStatusViewModel>(),
            TaskSwitcher: sp.GetRequiredService<TaskSwitcherViewModel>(),
            QuickActionsGrid: sp.GetRequiredService<QuickActionsGridViewModel>());
    }

    private static IExercise[] BuildExercisePool(PhysicalPoint center)
    {
        return new IExercise[]
        {
            new NearFarFocusExercise(center, new PhysicalPoint(center.X * 2 - 100, center.Y * 2 - 100), dwellMs: 3000, onTargetRadiusPixels: 60),
            new SmoothPursuitExercise(center, radius: Math.Min(center.X, center.Y) * 0.6, periodMs: 4000, onTargetRadiusPixels: 80),
            new Calibration202020Exercise(center, onTargetRadiusPixels: 40),
            new SaccadeExercise(BuildSaccadeTargets(center), dwellPerTargetMs: 1500, onTargetRadiusPixels: 100),
            new DirectionalSweepExercise(new PhysicalPoint(100, 100), new PhysicalPoint(center.X * 2 - 100, center.Y * 2 - 100), sweepDurationMs: 3000, onTargetRadiusPixels: 100),
        };
    }

    private static PhysicalPoint[] BuildSaccadeTargets(PhysicalPoint center)
    {
        return new[]
        {
            new PhysicalPoint(center.X * 0.2, center.Y * 0.3),
            new PhysicalPoint(center.X * 1.8, center.Y * 0.3),
            new PhysicalPoint(center.X * 0.2, center.Y * 1.7),
            new PhysicalPoint(center.X * 1.8, center.Y * 1.7),
        };
    }
}
