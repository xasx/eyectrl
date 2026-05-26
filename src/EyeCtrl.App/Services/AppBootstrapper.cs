namespace EyeCtrl.App.Services;

using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.Platform.Gaze;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using EyeCtrl.Platform.Input;
using EyeCtrl.ViewModels;
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
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var modelsDir = Path.Combine(appData, "EyeCtrl", "models");
            var settings = sp.GetRequiredService<EyeCtrlSettings>();

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
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "EyeCtrl", "models"),
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<ILogger<ModelDownloadService>>()));

        // Core engine
        services.AddSingleton<GazeEngine>();

        // ViewModels (singleton -- each registers message handlers in constructor)
        services.AddSingleton<CursorCircleViewModel>();
        services.AddSingleton<ActivationButtonViewModel>();
        services.AddSingleton<ManualSelectionOverlayViewModel>();
        services.AddSingleton<ActionMenuViewModel>();
        services.AddSingleton<ToolbarViewModel>();
        services.AddSingleton<KeyboardViewModel>();
        services.AddSingleton<ManualSelectionGazeIndicatorViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<GlassBackdropViewModel>();

        // GazeEngineAdapter (bridges Core events <-> MVVM messages)
        services.AddSingleton(sp => new GazeEngineAdapter(
            sp.GetRequiredService<GazeEngine>(),
            sp.GetRequiredService<EyeCtrlSettings>(),
            sp.GetRequiredService<IMessenger>(),
            dispatcher,
            sp.GetRequiredService<ILogger<GazeEngineAdapter>>()));

        // ManualSelectionDwellService (handles ManualSelection dwell detection and indicator updates)
        services.AddSingleton(sp => new ManualSelectionDwellService(
            sp.GetRequiredService<IMessenger>(),
            sp.GetRequiredService<ILogger<ManualSelectionDwellService>>(),
            sp.GetRequiredService<EyeCtrlSettings>(),
            dispatcher,
            sp.GetRequiredService<GazeEngine>().InteractionManager));

        // OverlayManager (manages all overlay window lifecycle)
        services.AddSingleton<IOverlayManager>(sp => new OverlayManager(
            dispatcher,
            sp.GetRequiredService<ILogger<OverlayManager>>()));

        return services.BuildServiceProvider();
    }
}
