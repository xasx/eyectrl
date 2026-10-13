namespace EyeCtrl.App.Services;

using System.Windows.Threading;
using EyeCtrl.App.Localization;
using EyeCtrl.App.Overlays;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Diagnostics;
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
using EyeCtrl.Platform.Interop;
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
    /// The per-user runtime data folder the shipping app uses: <c>%APPDATA%\EyeCtrl</c>. Named rather
    /// than defaulted, so that choosing it is an act at the call site — see the
    /// <c>appDataDir</c> parameter for why a default here was actively unsafe.
    /// </summary>
    public static string DefaultAppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EyeCtrl");

    /// <summary>
    /// Configures and builds the service provider with all application dependencies.
    /// </summary>
    /// <param name="settings">The loaded application settings</param>
    /// <param name="settingsFilePath">Path to the settings file</param>
    /// <param name="dispatcher">The UI thread dispatcher</param>
    /// <param name="isTestMode">Whether the app is running in test mode</param>
    /// <param name="appDataDir">
    /// Where per-user runtime data lives. The app passes <see cref="DefaultAppDataDir"/>; tests pass a
    /// temporary directory.
    /// <para>
    /// REQUIRED, with no default, and that is the whole point. It began as an optional override and was
    /// immediately dangerous: this container now loads <c>calibration.json</c> when
    /// <c>CalibrationService</c> is first resolved, and three pre-existing registration tests resolve it
    /// transitively (via <c>ManualSelectionDwellService</c> / <c>ExternalDwellService</c> →
    /// <c>GazeEngine</c>) without knowing they do. With a default they silently read the real
    /// <c>%APPDATA%\EyeCtrl</c> — and <c>Load</c>'s monitor-name refresh can WRITE it. A test run could
    /// therefore rewrite the user's live calibration, which is the file their only input method depends
    /// on. A parameter that must be supplied cannot be forgotten by a test that does not realise it is
    /// touching this path.
    /// </para>
    /// </param>
    /// <returns>Configured service provider</returns>
    public static ServiceProvider BuildServiceProvider(
        EyeCtrlSettings settings,
        string settingsFilePath,
        Dispatcher dispatcher,
        bool isTestMode,
        string appDataDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDir);
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
            SettingsWriter.App,
            sp.GetRequiredService<ILogger<SettingsService>>()));

        // Dictionary management (prediction controls #22): the settings page's view into the
        // learned words. The live prediction factory arrives late via SetFactory (KeyboardCoordinator
        // constructs it per keyboard init, not DI).
        services.AddSingleton(_ => new UserDictionaryService(settings, appDataDir));

        // Platform services
        services.AddSingleton<IMouseCoordinateMapper, MouseCoordinateMapper>();
        services.AddSingleton<IInputSimulator, Win32InputSimulator>();
        // Installs the WH_KEYBOARD_LL escape hatch for the process lifetime (issue #141);
        // disposed (and unhooked) with the container on shutdown.
        services.AddSingleton<IGlobalEscapeHatch, GlobalEscapeHatch>();

        // Startup registration
        services.AddSingleton<IStartupRegistrar, WindowsStartupRegistrar>();

        // Window management (Task Switcher)
        services.AddSingleton<IWindowManager, Win32WindowManager>();
        services.AddSingleton<IAppIconProvider, Win32AppIconProvider>();

        // Quick Actions
        services.AddSingleton<IQuickActionsStore>(sp =>
        {
            var path = Path.Combine(appDataDir, "actions.json");
            var store = new QuickActionsStore(path);

            // The one-time preset match (D2) runs here, at startup, before any surface reads the
            // file — a keyed grid must not wait for the Settings window to be opened once. It
            // fills only missing ids, keeps file order, writes only when something changed, and
            // a corrupt file loads as empty so nothing is written over it. A failed write must
            // not take startup down: the tiles then simply stay unmatched.
            try
            {
                var actions = store.LoadAll();
                if (QuickActionPresets.AssignMissingPresetIds(actions)) store.Save(actions);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Quick-action preset match failed at startup; tiles stay unmatched.");
            }

            return store;
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
            double screenWidth = System.Windows.SystemParameters.PrimaryScreenWidth * DpiHelper.ScaleX;
            double screenHeight = System.Windows.SystemParameters.PrimaryScreenHeight * DpiHelper.ScaleY;
            var provider = factory.Create(settings.General.GazeProviderType, screenWidth, screenHeight);

            // Raw-gaze recording is opt-in and off by default, so the decorator is not even constructed
            // unless the user turned it on for this session: with it off the provider chain is exactly
            // what it has always been. Toggling the setting mid-session does not start a recording —
            // the wrap is decided once here — which is deliberate: a recording that begins partway
            // through has no header describing the session it is half of.
            if (!settings.Diagnostics.RecordRawGaze)
                return provider;

            var recorderLog = loggerFactory.CreateLogger<GazeRecorder>();
            var calibration = sp.GetRequiredService<CalibrationService>();

            // The transform's own coefficients, not just its identity: the analyzer replays the raw
            // samples and must reapply the exact mapping the live pipeline used, and calibration.json
            // may have been recalibrated away by the time anyone looks at the recording. Null for a
            // transform type this does not know how to serialize — the analyzer refuses such a recording
            // rather than replaying an uncorrected stream as if it were corrected.
            //
            // The service is loaded by the time this reads it: it loads in its own factory, and the
            // GetRequiredService above is what triggers that. Header state is therefore the session's
            // real state, which it was not while the load happened later in App startup.
            AffineTransformParameters? transformParams =
                AffineTransformParameters.TryDescribe(calibration.CurrentTransform);

            var header = new GazeRecordingHeader(
                ProviderType: GazeProviderFactory.GetProviderType(provider).ToString(),
                ScreenWidthPhysical: screenWidth,
                ScreenHeightPhysical: screenHeight,
                CalibrationActive: calibration.CurrentTransform is not null,
                CalibrationVersion: calibration.CurrentData?.CalibratedAt.ToString("O") ?? string.Empty,
                SettingsVersion: 0,
                CalibrationTransform: transformParams);

            if (calibration.CurrentTransform is not null && transformParams is null)
            {
                recorderLog.LogWarning(
                    "A calibration is active but its transform type ({Type}) cannot be written to the recording header; " +
                    "the recording will not be analyzable",
                    calibration.CurrentTransform.GetType().Name);
            }

            var recorder = new GazeRecorder(
                Path.Combine(appDataDir, "gaze-recording.csv"),
                header,
                onFault: ex => recorderLog.LogWarning(ex,
                    "Raw-gaze recording stopped after a writer fault; gaze itself is unaffected"),
                // The drop count is the recording's own validity check, so it must reach the operator
                // while they are still at the machine that produced it. Warning, not Information: a
                // non-zero count means the session has to be collected again.
                onClosed: t =>
                {
                    if (t.DroppedSamples > 0)
                    {
                        recorderLog.LogWarning(
                            "Raw-gaze recording DROPPED {Dropped} of {Recorded} samples — the recording has holes in it " +
                            "and cannot be measured; re-collect the session",
                            t.DroppedSamples, t.RecordedSamples);
                    }
                    else
                    {
                        recorderLog.LogInformation(
                            "Raw-gaze recording closed: {Recorded} samples, none dropped", t.RecordedSamples);
                    }
                });

            recorderLog.LogInformation(
                "Raw-gaze recording is ON for this session (opt-in). Writing to {Path}",
                Path.Combine(appDataDir, "gaze-recording.csv"));

            return new RecordingGazeProvider(provider, recorder, settings.Diagnostics,
                onFault: ex => recorderLog.LogWarning(ex,
                    "Raw-gaze recording self-disabled; gaze itself is unaffected"));
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

        // Calibration.
        //
        // LOADED IN THE FACTORY, not by the caller afterwards. It used to be constructed here and
        // loaded from App.xaml.cs some eighty lines later, which made "is a calibration active?" a
        // question whose answer depended on WHERE in startup you asked it — and the raw-gaze recorder,
        // resolved earlier, asked too early. Every recording made on a calibrated machine was stamped
        // calibrationActive:false with a null transform, which since replay began applying that
        // transform means the recording cannot be replayed as the calibrated session it was.
        //
        // Loading inside the factory removes the ordering question rather than answering it: the first
        // resolution loads, so no consumer can observe an unloaded service, whatever order startup is
        // rearranged into later. `Load` raises no events, so nothing can depend on it running late.
        //
        // It is NOT side-effect-free, and the earlier version of this comment claiming it was a "pure
        // read" was wrong: on a hardware-id match with a stale device name it rewrites the file
        // (CalibrationService.RefreshPersistedDeviceName). That is exactly why appDataDir is a required
        // parameter — a caller who does not supply one would have this write land on the user's live
        // calibration.
        services.AddSingleton(sp =>
        {
            var service = new CalibrationService(sp.GetService<ILogger<CalibrationService>>());

            // PHYSICAL pixels, matching what CompleteSession persists; logical DIPs here would discard
            // the calibration on any non-100% DPI display.
            var screen = new PhysicalPoint(
                System.Windows.SystemParameters.PrimaryScreenWidth * DpiHelper.ScaleX,
                System.Windows.SystemParameters.PrimaryScreenHeight * DpiHelper.ScaleY);

            // Match on the monitor's EDID identity, not its \\.\DISPLAYn index: Windows renumbers those
            // on dock changes/driver updates/reboots, which used to discard the calibration silently.
            var monitorName = System.Windows.Forms.Screen.PrimaryScreen?.DeviceName ?? "";
            service.Load(
                Path.Combine(appDataDir, "calibration.json"),
                screen,
                monitorName,
                MonitorIdentity.GetHardwareId(monitorName));

            return service;
        });

        // ControlFinder (AutoSelection mode). Takes the AutoSelection sub-settings by reference so
        // SnapSearchRadiusPixels edits apply without rebuilding this singleton.
        services.AddSingleton<IControlFinder>(sp => new ControlFinder(
            sp.GetRequiredService<ILogger<ControlFinder>>(),
            sp.GetRequiredService<EyeCtrlSettings>().AutoSelection));

        // Struggle-detection effective-settings provider (Phase 1b): composes bounded momentary
        // deltas onto baseline settings. Holds no deltas while adaptation is disabled or idle, and
        // returns baseline while AdaptationSettings.Enabled == false; Phase 2 adds the learned fills beside the momentary ones.
        services.AddSingleton(sp => new EffectiveSettingsProvider(
            sp.GetRequiredService<EyeCtrlSettings>().Adaptation));

        // Dwell routing (Phase 1c slice 2): the registry discovers live dwell surfaces; the router
        // applies the effective DwellTime on provider Changed / registration change. Eagerly resolved
        // at startup (App.xaml.cs) — it subscribes and applies in its ctor, and DI singletons are lazy.
        services.AddSingleton<IDwellDetectorRegistry, DwellDetectorRegistry>();
        services.AddSingleton<DwellTimeRouter>();
        services.AddSingleton<GeometryRouter>();

        // Dwell-completion audio cue (spec 2026-09-23): ctor-subscribes DwellActivationMessage on
        // the messenger, so — like DwellTimeRouter — it must be eagerly resolved at startup
        // (ResolveEagerServices); DI singletons are lazy and nothing else depends on it.
        services.AddSingleton(sp => new DwellCueService(
            sp.GetRequiredService<IMessenger>(),
            sp.GetRequiredService<EyeCtrlSettings>().Dwell,
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "dwell-cue.wav")));

        // Core engine. The telemetry sink rides the engine since ticket 3: the activation surface's clock
        // lives in the fixation rule now, so its Activated/Abandoned outcomes are emitted from the same
        // valid-time ledger. The sink is registered below this factory, which is fine — factories are
        // evaluated lazily at first resolution.
        services.AddSingleton(sp => new GazeEngine(
            sp.GetRequiredService<IGazeProvider>(),
            sp.GetRequiredService<IInputSimulator>(),
            sp.GetRequiredService<EyeCtrlSettings>(),
            sp.GetRequiredService<CalibrationService>(),
            sp.GetRequiredService<EffectiveSettingsProvider>(),
            sp.GetRequiredService<ILogger<GazeEngine>>(),
            sp.GetRequiredService<IStruggleTelemetrySink>()));

        // Struggle-detection telemetry sink (Phase 1a): surfaces report DwellOutcomes here.
        // BufferingTelemetrySink only buffers (zero behaviour change); DispatcherTelemetrySink
        // serializes onto the UI dispatcher and swallows inner exceptions. The concrete sink is
        // registered once and mapped to both IStruggleTelemetrySink and IGazeQualitySink so one
        // instance serializes both dwell-outcome and gaze-quality paths onto the UI thread. The
        // GazeQualityAggregator feeds raw samples into the IGazeQualitySink path (App.xaml.cs).
        services.AddSingleton<BufferingTelemetrySink>();
        services.AddSingleton<DispatcherTelemetrySink>(sp => new DispatcherTelemetrySink(
            sp.GetRequiredService<BufferingTelemetrySink>(),
            dispatcher,
            sp.GetRequiredService<EyeCtrlSettings>().Adaptation,
            sp.GetRequiredService<ILogger<DispatcherTelemetrySink>>(),
            innerQuality: sp.GetRequiredService<BufferingTelemetrySink>()));
        services.AddSingleton<IStruggleTelemetrySink>(sp => sp.GetRequiredService<DispatcherTelemetrySink>());
        services.AddSingleton<IGazeQualitySink>(sp => sp.GetRequiredService<DispatcherTelemetrySink>());
        services.AddSingleton(sp => new GazeQualityAggregator(
            sp.GetRequiredService<IGazeQualitySink>(),
            sp.GetRequiredService<EyeCtrlSettings>().Adaptation));

        // The jitter delegate resolves GazeEngine lazily. Factory registrations are not evaluated at
        // registration time, so it does not matter that GazeEngine is registered earlier — and
        // GazeEngineAdapter already resolves it the same way.
        services.AddSingleton(sp => new GazeQualityBridge(
            sp.GetRequiredService<GazeQualityAggregator>(),
            sp.GetRequiredService<IEyeCareService>(),
            () => sp.GetRequiredService<GazeEngine>().LastFixationJitter));

        // ViewModels (singleton -- each registers message handlers in constructor)
        services.AddSingleton<CursorCircleViewModel>();
        services.AddSingleton<ActivationButtonViewModel>();
        services.AddSingleton<ManualSelectionOverlayViewModel>();
        services.AddSingleton<ActionMenuViewModel>();
        // Full-arity factory (Task 6, R2-1 — load-bearing): every optional parameter after
        // `messenger` compiles-and-runs when dropped, while silently switching something off —
        // `sink` kills toolbar struggle telemetry, `dwellRegistry` kills adaptation's dwell/
        // geometry routing to the toolbar, and `isFeedbackSuppressed` leaves the Reset preview
        // unable to go silent in sensitive contexts (MS DI fills unresolvable optionals with
        // null, so even the plain AddSingleton<ToolbarViewModel>() "works").
        // ToolbarPreviewWiringTests resolves this registration and asserts all three.
        services.AddSingleton(sp => new ToolbarViewModel(
            sp.GetRequiredService<EyeCtrlSettings>(),
            WeakReferenceMessenger.Default,
            sink: sp.GetRequiredService<IStruggleTelemetrySink>(),
            dwellRegistry: sp.GetRequiredService<IDwellDetectorRegistry>(),
            isFeedbackSuppressed: () => sp.GetRequiredService<IFeedbackGate>().IsFeedbackSuppressed,
            logger: sp.GetRequiredService<ILoggerFactory>().CreateLogger<ToolbarViewModel>()));
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
            Path.Combine(appDataDir, "calibration.json"),
            // Recorded so the next load matches on EDID rather than the \\.\DISPLAYn index, which
            // Windows renumbers on dock changes/driver updates/reboots. Deferred, not captured: the
            // view model is an app-lifetime singleton, so evaluating this here would pin whichever
            // monitor was primary at startup onto every later recalibration.
            () => MonitorIdentity.GetHardwareId(
                System.Windows.Forms.Screen.PrimaryScreen?.DeviceName ?? ""),
            // Only so the deletion of calibration.json on an explicit Clear leaves a trace — that
            // delete is otherwise indistinguishable from a calibration that never existed.
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<CalibrationViewModel>(),
            // Scripted ground truth: a real recorder since ticket 6 (constructed unconditionally —
            // see its own registration below). The view model still gates normal calibration runs on
            // the live RecordRawGaze setting, so this only means the probe can always write its
            // self-contained sidecar.
            sp.GetService<ScriptedGroundTruthRecorder>(),
            GazeProviderFactory.GetProviderType(sp.GetRequiredService<IGazeProvider>()).ToString(),
            // The live RecordRawGaze gate, so the sidecar stops when the recording it describes does.
            settings.Diagnostics));
        services.AddSingleton<TargetHighlightOverlayViewModel>();
        services.AddSingleton<DragTrailViewModel>();
        services.AddSingleton<EdgeButtonBarViewModel>();
        // The LEFT-edge action-menu modifier bar's view model — a SECOND instance of the same
        // type, so it is keyed: the unkeyed registration above stays the right-edge bar, and
        // unkeyed GetRequiredService<EdgeButtonBarViewModel>() ignores this one. Resolution must
        // go through GetRequiredKeyedService (GetRequiredService<T> has no key overload).
        services.AddKeyedSingleton<EdgeButtonBarViewModel>("actionMenuModBar");
        services.AddSingleton<TrackStatusViewModel>();
        services.AddSingleton<TaskSwitcherViewModel>();
        services.AddSingleton<ModesMenuViewModel>();
        services.AddSingleton<RepeatCursorViewModel>();
        services.AddSingleton<QuickActionsGridViewModel>();

        // Scripted ground truth (Stage 2 ticket 3; self-contained again for ticket 6's bias probe).
        //
        // Constructed UNCONDITIONALLY since ticket 6. It used to resolve to null unless RecordRawGaze
        // was on, on the reasoning that a sidecar only describes a recording that exists. The
        // measure-only probe breaks that premise on purpose: its windows carry their own raw samples,
        // so it must not hinge on a global diagnostics flag — a measurement session whose data
        // silently vanished because a hidden toggle was off is exactly the trap this user cannot
        // escape. The recorder is inert until BeginSession, and the view model still gates the
        // NORMAL calibration path on the live RecordRawGaze setting, so nothing is written for a
        // session the user did not ask to measure.
        services.AddSingleton<ScriptedGroundTruthRecorder>(sp =>
        {
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger<ScriptedGroundTruthRecorder>();
            return new ScriptedGroundTruthRecorder(
                Path.Combine(appDataDir, "gaze-recording.groundtruth.json"),
                ex => log.LogWarning(ex, "Could not write the scripted ground-truth sidecar; the calibration is unaffected"));
        });

        // The two Stage-2 UI instruments. Constructed only when the operator turned them on for a
        // measurement session, so the shipped default leaves both paths exactly as they were; the
        // registrations resolve to null otherwise, which each consumer reads as "not instrumented".
        services.AddSingleton<DispatchLatencyProbe>(_ =>
            settings.Diagnostics.MeasureUiInstruments ? new DispatchLatencyProbe() : null!);
        services.AddSingleton<RingQuantizationProbe>(_ =>
            settings.Diagnostics.MeasureUiInstruments ? new RingQuantizationProbe() : null!);

        // GazeEngineAdapter (bridges Core events <-> MVVM messages)
        services.AddSingleton(sp => new GazeEngineAdapter(
            sp.GetRequiredService<GazeEngine>(),
            sp.GetRequiredService<EyeCtrlSettings>(),
            sp.GetRequiredService<IMessenger>(),
            dispatcher,
            sp.GetRequiredService<ILogger<GazeEngineAdapter>>(),
            sp.GetRequiredService<IControlFinder>(),
            sp.GetService<DispatchLatencyProbe>()));

        // ManualSelectionDwellService (handles ManualSelection dwell detection and indicator updates)
        services.AddSingleton(sp => new ManualSelectionDwellService(
            sp.GetRequiredService<IMessenger>(),
            sp.GetRequiredService<ILogger<ManualSelectionDwellService>>(),
            sp.GetRequiredService<EyeCtrlSettings>(),
            dispatcher,
            sp.GetRequiredService<GazeEngine>().InteractionManager,
            sp.GetRequiredService<IStruggleTelemetrySink>(),
            sp.GetRequiredService<IDwellDetectorRegistry>()));

        // ExternalDwellService: lets cooperating apps register dwellable controls over a named pipe.
        services.AddSingleton(sp => new ExternalDwellService(
            sp.GetRequiredService<EyeCtrlSettings>().ExternalDwell,
            sp.GetRequiredService<GazeEngine>(),
            dispatcher,
            sp.GetRequiredService<ILogger<ExternalDwellService>>(),
            sp.GetRequiredService<IMessenger>(),
            sink: sp.GetRequiredService<IStruggleTelemetrySink>(),
            dwellRegistry: sp.GetRequiredService<IDwellDetectorRegistry>()));

        // NoteManager (pinned notes lifecycle)
        services.AddSingleton<INoteManager>(sp => new NoteManager(
            sp.GetRequiredService<IMessenger>(),
            sp.GetRequiredService<IStruggleTelemetrySink>(),
            sp.GetRequiredService<IDwellDetectorRegistry>()));

        // OverlayManager (manages all overlay window lifecycle)
        services.AddSingleton<IOverlayManager>(sp => new OverlayManager(
            dispatcher,
            sp.GetRequiredService<ILogger<OverlayManager>>(),
            sp.GetRequiredService<IAppIconProvider>(),
            sp.GetRequiredService<IStruggleTelemetrySink>(),
            sp.GetRequiredService<IDwellDetectorRegistry>()));

        // Eye-care
        services.AddSingleton(sp => new EyeCareBannerWindow(
            sp.GetRequiredService<EyeCtrlSettings>().Dwell.ActivationButtonDwellMs,
            sp.GetRequiredService<IStruggleTelemetrySink>(),
            sp.GetRequiredService<IDwellDetectorRegistry>()));
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

        // Struggle-detection adaptation runtime (Phase 1b). The telemetry sink (1a-②) and the
        // effective-settings provider (1b-①) are already registered above; here we add the monitor,
        // controller, and coordinator. The coordinator ticks the monitor→controller pipeline on a
        // dispatcher timer, enable-gated on AdaptationSettings.Enabled (off by default) — inert until opted in.
        //
        // backspaceTargetId: null, deliberately. DO NOT "restore" it to "backspace".
        //
        // The corroboration it enabled is backwards for this user. A wrong key fires from gaze BIAS
        // (measured ~2 key widths; within-fixation scatter is 0.064 key widths, so scatter cannot cross
        // a key gap) → the user presses Backspace to undo it → ScoreAccidental counts that CORRECTION as
        // evidence the keyboard activates too easily → and accidental-activation is the one drive that
        // TIGHTENS the dwell knob (AdaptationController.Tick). So the machine answers a user who is
        // already struggling by making the keyboard slower — a ratchet toward 675 ms that re-arms on
        // every correction. A user who is struggling is the one who presses Backspace most, so it
        // tightens fastest for exactly the person it must not.
        //
        // The reason is the category error, not the magnitude: the monitor cannot distinguish
        // "activated too easily" from "activated the wrong thing", and those want opposite responses.
        // Backspace is the user's only correction gesture; scoring it as a failure inverts the meaning
        // of the only feedback they can give. The monitor cannot tell the two apart yet, so off is
        // strictly better than backwards for this drive. The geometric re-approach half of accidental
        // detection needs no target id and is unchanged: at shipped geometry (70 DIP keys at this
        // user's 2.0 display scale = 148 px centre pitch) its 40 px cone cannot reach an adjacent key,
        // so it cannot corroborate a keyboard correction — but it is still live, and at a small
        // key-size multiplier on a 1.0-scale display (39 px centre pitch) it could. What is disarmed
        // here is the Backspace path, not keyboard tightening as a structural possibility.
        //
        // The keyboard-side companion is the app-escaping dwell floor (KeyboardSpecialActions /
        // KeyboardViewModel.KeyDwellMsFor), which holds enter/tab/Escape/Delete/Insert/F-keys/shortcuts
        // at or above the 600 ms policy floor while adaptation shortens everything else.
        //
        // Pinned by EyeCtrl.App.Tests.Services.StruggleMonitorRegistrationTests (the app-resolved
        // monitor must carry a null target) and by the Core loop test that shows the identical
        // correction stream lengthening dwell only when this id is supplied.
        services.AddSingleton(sp => new StruggleMonitor(
            sp.GetRequiredService<BufferingTelemetrySink>(),
            sp.GetRequiredService<EyeCtrlSettings>().EyeCare,
            backspaceTargetId: null));
        services.AddSingleton(sp => new AdaptationController(
            sp.GetRequiredService<EffectiveSettingsProvider>(),
            sp.GetRequiredService<EyeCtrlSettings>().Adaptation,
            sp.GetRequiredService<ILogger<AdaptationController>>()));
        // Phase-2 learned layer (§7.4): EWMA over the controller's momentary fills, pushed into the
        // provider; ticked by the coordinator on assess ticks. Load()/Persist() are called by App
        // (startup/exit) — DI construction itself performs no I/O.
        services.AddSingleton(sp => new AdaptationLearner(
            sp.GetRequiredService<EffectiveSettingsProvider>(),
            sp.GetRequiredService<EyeCtrlSettings>().Adaptation,
            sp.GetRequiredService<ILogger<AdaptationLearner>>(),
            Path.Combine(appDataDir, "adaptation-state.json")));
        services.AddSingleton(sp => new AdaptationCoordinator(
            sp.GetRequiredService<StruggleMonitor>(),
            sp.GetRequiredService<AdaptationController>(),
            sp.GetRequiredService<AdaptationLearner>(),
            sp.GetRequiredService<EyeCtrlSettings>().Adaptation,
            sp.GetRequiredService<IGazeProvider>(),
            sp.GetRequiredService<GazeEngine>().InteractionManager));

        // Starts/stops the whole adaptation runtime on the Enabled edge, so the opt-in feature costs nothing
        // (no tick timer, no overlay HWNDs, no system-wide UIA focus hook) while it is off. Resolves its
        // participants lazily from the container, hence the IServiceProvider dependency.
        services.AddSingleton(sp => new AdaptationActivation(
            sp.GetRequiredService<EyeCtrlSettings>().Adaptation,
            sp));

        // 1b-⑤b feedback & manual-escape surface. The sensitive-context gate (1b-⑥) is the real suppression
        // seam (§8.10). CueOverlayWindow is excluded from magnifier capture and doubles as the ICuePresenter.
        // The cue service + reset handler are started in App.OnStartup (after the coordinator starts).
        // 1b-⑥ sensitive-context suppression (§8.10). The UIA detector caches whether a password field is
        // focused; the gate suppresses the cue / drift notice / verbose log while it is (and the setting is
        // on), leaving adaptation itself running. Replaces ⑤b's no-op AllowAllFeedbackGate registration.
        // ILogger<UiaSensitiveContextDetector> is auto-injected (as for ControlFinder). Started in
        // App.OnStartup; disposed by the container in OnExit (removing the UIA focus hook).
        services.AddSingleton<ISensitiveContextDetector, UiaSensitiveContextDetector>();
        services.AddSingleton<IFeedbackGate>(sp => new SensitiveContextFeedbackGate(
            sp.GetRequiredService<EyeCtrlSettings>().Adaptation,
            sp.GetRequiredService<ISensitiveContextDetector>()));
        services.AddSingleton<CueOverlayWindow>();
        services.AddSingleton<ICuePresenter>(sp => sp.GetRequiredService<CueOverlayWindow>());
        services.AddSingleton(sp => new AdaptationCueService(
            sp.GetRequiredService<EyeCtrlSettings>().Adaptation,
            sp.GetRequiredService<IFeedbackGate>(),
            sp.GetRequiredService<ICuePresenter>()));
        services.AddSingleton(sp => new AdaptationResetHandler(
            sp.GetRequiredService<IMessenger>(),
            sp.GetRequiredService<AdaptationCoordinator>(),
            sp.GetRequiredService<AdaptationLearner>()));

        // 1b-⑤c drift report + adaptation log. Both subscribe to the coordinator's AssessmentUpdated event
        // and consult the same IFeedbackGate (1b-⑤b). The drift banner is excluded from magnifier capture
        // and non-interactive; both are started in App.OnStartup after the cue service.
        services.AddSingleton(sp => new AdaptationLog(
            sp.GetRequiredService<EyeCtrlSettings>().Adaptation,
            sp.GetRequiredService<IFeedbackGate>(),
            sp.GetRequiredService<EffectiveSettingsProvider>(),
            Path.Combine(appDataDir, "adaptation-log.jsonl")));
        services.AddSingleton<DriftNoticeWindow>();
        services.AddSingleton<IDriftNoticePresenter>(sp => sp.GetRequiredService<DriftNoticeWindow>());
        services.AddSingleton(sp => new DriftNoticeService(
            sp.GetRequiredService<IFeedbackGate>(),
            sp.GetRequiredService<IDriftNoticePresenter>()));
        // Phase-3 caregiver alert (§10 resolution): the app-owned banner + chime. Same pattern as the
        // drift notice — the window is a DI singleton (constructed lazily on the first Enabled edge,
        // never while disabled) and doubles as the presenter; the service is the pure decision layer
        // started/stopped by AdaptationActivation. The log callback is the JSONL hook (D8).
        services.AddSingleton<CaregiverAlertWindow>();
        services.AddSingleton<ICaregiverAlertPresenter>(sp => sp.GetRequiredService<CaregiverAlertWindow>());
        services.AddSingleton(sp => new CaregiverAlertService(
            sp.GetRequiredService<EyeCtrlSettings>().Adaptation,
            sp.GetRequiredService<IFeedbackGate>(),
            sp.GetRequiredService<ICaregiverAlertPresenter>(),
            sp.GetRequiredService<AdaptationLog>().LogCaregiverAlert));

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Resolves the ctor-subscribing/lazy-trapped singletons that nothing else depends on.
    /// Extracted from App.xaml.cs's discard-resolves so tests can run the SAME startup path
    /// (plan B4) — and so the startup line is one call site to pin.
    /// </summary>
    public static void ResolveEagerServices(IServiceProvider sp)
    {
        // Resolve ManualSelectionDwellService (triggers message subscriptions)
        _ = sp.GetRequiredService<ManualSelectionDwellService>();

        // Resolve the dwell-time router (Phase 1c dwell routing): it subscribes to the effective-
        // settings provider and the dwell-detector registry in its ctor and applies the current
        // values immediately, so it must exist before any surface registers.
        _ = sp.GetRequiredService<DwellTimeRouter>();

        _ = sp.GetRequiredService<GeometryRouter>();

        // DwellCueService subscribes DwellActivationMessage in its ctor (see its class doc).
        _ = sp.GetRequiredService<DwellCueService>();
    }

    public static OverlayViewModels ResolveViewModels(ServiceProvider sp, EyeCtrlSettings settings)
    {
        var manualSelectionIndicatorVm = sp.GetRequiredService<ManualSelectionGazeIndicatorViewModel>();
        manualSelectionIndicatorVm.Configure(settings);

        // T08 (N2): the live grid shows preset labels from the overlays surface's keys (D3). The
        // Settings picker keeps its own settings-keyed function, so one key never serves two
        // surfaces. Set here — the single composition point both the App and the bootstrap tests
        // run — so no later reload can read the English default.
        var quickActionsGridVm = sp.GetRequiredService<QuickActionsGridViewModel>();
        quickActionsGridVm.PresetLabelFor = OverlayTextSources.PresetLabelFor;

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
            ActionMenuModBar: sp.GetRequiredKeyedService<EdgeButtonBarViewModel>("actionMenuModBar"),
            TrackStatus: sp.GetRequiredService<TrackStatusViewModel>(),
            TaskSwitcher: sp.GetRequiredService<TaskSwitcherViewModel>(),
            QuickActionsGrid: quickActionsGridVm,
            ModesMenu: sp.GetRequiredService<ModesMenuViewModel>(),
            RepeatCursor: sp.GetRequiredService<RepeatCursorViewModel>());
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
