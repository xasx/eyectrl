namespace EyeCtrl.App;

using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Contracts;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Services;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Startup;
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
using EyeCtrl.Platform.Services;
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
    private SingleInstanceGuard? _singleInstance;
    private HeartbeatPublisher? _heartbeatPublisher;
    private AppRestartCoordinator? _restartCoordinator;
    private string _appDataDir = null!;

    /// <summary>
    /// Validated <c>EYECTRL_INSTANCE_ID</c>, or <c>null</c> for the default instance. Every scoped
    /// name in this process is derived from it, and <c>!= null</c> is the single "am I scoped?" test.
    /// </summary>
    private string? _instanceId;

    /// <summary>
    /// The default instance's profile directory, resolved alongside <see cref="_appDataDir"/> from
    /// the same single <c>%APPDATA%</c> read. Phase 2 asserts the two differ; holding it avoids
    /// re-deriving the live path at the one moment the code is checking it did not use it.
    /// </summary>
    private string _liveProfileDir = null!;

    /// <summary>
    /// When true, the app uses MouseGazeProvider and skips the splash delay.
    /// Activated by launching with --test-mode CLI argument.
    /// </summary>
    public static bool IsTestMode { get; private set; }

    /// <summary>
    /// When true, the app starts in a paused state regardless of the persisted
    /// <c>StartPaused</c> setting. Activated by launching with the --paused CLI
    /// argument — used by the supervisor's paused-retry handoff so a retried
    /// instance is inert (no SendInput) without mutating the user's setting.
    /// </summary>
    public static bool IsPausedLaunch { get; private set; }

    /// <summary>
    /// The resolved gaze provider type for this run (Auto resolves to the concrete type at startup).
    /// Used to gate eye-tracker-only features (e.g. toolbar auto-hide). Set in OnStartup.
    /// </summary>
    public static GazeProviderType ActiveProviderType { get; private set; }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        IsTestMode = e.Args.Contains("--test-mode", StringComparer.OrdinalIgnoreCase);
        IsPausedLaunch = e.Args.Contains("--paused", StringComparer.OrdinalIgnoreCase);
        var isRestartHandoff = e.Args.Contains(RestartHandoff.Argument, StringComparer.OrdinalIgnoreCase);

        // ── Phase 1: may this instance run at all? ──
        // Before the mutex, because the mutex's NAME depends on the id. Before the handoff signal,
        // so a replacement that is going to be refused never tells its parent it is ready.
        var decision = InstanceStartupPolicy.Evaluate(
            Environment.GetEnvironmentVariable(InstanceScope.EnvVar), IsTestMode, isRestartHandoff);

        if (decision.Outcome == InstanceStartupOutcome.Refuse && !decision.CanLog)
        {
            // A malformed id yields no legal instances\<id> directory, so there is no scoped log
            // path. Initialising Serilog anyway would open the LIVE %APPDATA%\EyeCtrl\logs — writing
            // to the profile this instance was supposed to stay out of, on the one path where the id
            // cannot be trusted. stderr instead, and out.
            Console.Error.WriteLine(decision.Reason);
            Shutdown(InstanceStartupPolicy.RefusalExitCode);
            return;
        }

        InitializeInstanceAndLogging(decision.InstanceId);

        if (decision.Outcome == InstanceStartupOutcome.Refuse)
        {
            Log.Fatal("Refusing to start: {Reason}", decision.Reason);
            Log.CloseAndFlush();
            Shutdown(InstanceStartupPolicy.RefusalExitCode);
            return;
        }

        // A restart replacement announces itself BEFORE queueing for the mutex: the
        // outgoing instance is waiting for exactly this before it exits, and it still
        // holds the mutex while it waits. Reversing the order deadlocks. Unreachable while
        // scoped — phase 1 refuses --restart-handoff — so it is never reached by an instance
        // that might still fail validation.
        if (isRestartHandoff)
        {
            RestartHandoff.SignalReady();
        }

        // Single-instance guard, now under its scoped name. A second instance exits fast. The
        // supervisor's respawn is legitimate, but a stray double-click/race must not launch two
        // instances fighting over the same gaze device, settings file, and dwell pipe.
        // Local\ (not Global\): multi-user/RDP is a non-goal; avoids cross-session collisions.
        //
        // A restart replacement is the one case that must WAIT rather than fail fast: the
        // instance it replaces holds the mutex until the very end of its own OnExit.
        _singleInstance = new SingleInstanceGuard(
            InstanceScope.Scope(SingleInstanceGuard.DefaultMutexName, decision.InstanceId));
        if (!_singleInstance.TryAcquire(isRestartHandoff ? RestartHandoff.MutexWait : TimeSpan.Zero))
        {
            // Another instance is already running (likely the supervisor's legitimate one),
            // or — for a handoff — the outgoing instance never let go. Either way, exit:
            // one working instance beats two fighting over the gaze device.
            //
            // Now loggable, because logging is initialised above — closing the wart the old
            // comment here described ("deliberately unlogged; Serilog's static Log is still a
            // SilentLogger"). Resolving the instance first is what made that possible.
            Log.Information("Another instance already holds {MutexName}; exiting.",
                _singleInstance.EffectiveName);
            Log.CloseAndFlush();
            Shutdown();
            return;
        }

        SplashWindow? splash = null;
        try
        {
            var (settings, settingsFilePath) = LoadSettings();

            // ── Theme ──
            // Before the DI container, because everything downstream — overlays, dwell
            // controls, the settings window — reads Tokens the moment it is constructed.
            // Applies at startup only: a theme change recreates windows rather than
            // re-resolving live (spec §10.6), so the picker is labelled "restart to apply".
            //
            // Theme must be active before ANY window is constructed: OverlayWindow's constructor
            // reads Tokens.FontStandard, and a window built earlier would silently keep the
            // default theme's values. The splash used to be built first — see spec §16.
            ThemeStartup.Apply(settings, Resources);

            // ── Splash screen ──
            if (!IsTestMode)
            {
                splash = new SplashWindow();
                splash.Show();
            }

            InitializeDpi(splash);

            // ── Build DI container ──
            _serviceProvider = AppBootstrapper.BuildServiceProvider(
                settings, settingsFilePath, Dispatcher, IsTestMode, _appDataDir, _instanceId);

            // ── Resolve services ──
            var settingsService = _serviceProvider.GetRequiredService<SettingsService>();
            var gazeEngine = _serviceProvider.GetRequiredService<GazeEngine>();
            var messenger = _serviceProvider.GetRequiredService<IMessenger>();
            var inputSimulator = _serviceProvider.GetRequiredService<IInputSimulator>();
            var gazeProvider = _serviceProvider.GetRequiredService<IGazeProvider>();

            // Reconcile auto-launch registration with the persisted setting.
            // In supervised mode the supervisor owns launch-at-logon (Scheduled Task), so the
            // app never self-registers the HKCU Run key — doing so would cause a double-launch
            // at next logon. `supervised` is captured once at launch: live-toggling supervision
            // mode isn't supported (it requires install/uninstall of the Scheduled Task, the
            // supervisor's job). The PropertyChanged handler only re-reacts to StartOnBoot changes.
            var startupRegistrar = _serviceProvider.GetRequiredService<IStartupRegistrar>();
            var supervised = settings.Supervision.Enabled;
            startupRegistrar.SetStartOnBoot(StartOnBootPolicy.ShouldSelfRegister(supervised, settings.General.StartOnBoot));

            // Live-toggle: if the user changes StartOnBoot in settings, apply immediately.
            settings.General.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(settings.General.StartOnBoot))
                    startupRegistrar.SetStartOnBoot(StartOnBootPolicy.ShouldSelfRegister(supervised, settings.General.StartOnBoot));
            };

            var activeProviderType = GazeProviderFactory.GetProviderType(gazeProvider);
            ActiveProviderType = activeProviderType;
            var version = System.Reflection.Assembly.GetExecutingAssembly()
                .GetName().Version?.ToString() ?? "unknown";
            Log.Information("Startup: EyeCtrl v{Version}, provider={Provider}, settings={SettingsPath}",
                version, activeProviderType, settingsFilePath);
            var im = gazeEngine.InteractionManager;

            // ── Resolve ViewModels ──
            var (cursorVm, activationVm, manualSelectionVm, actionMenuVm, toolbarVm, keyboardVm,
                 manualSelectionIndicatorVm, backdropVm, calibrationVm, targetHighlightVm, edgeButtonBarVm, trackStatusVm, taskSwitcherVm,
                 quickActionsGridVm) = ResolveViewModels(settings);

            // Resolve adapter (triggers Core event subscriptions)
            _ = _serviceProvider.GetRequiredService<GazeEngineAdapter>();

            // Resolve ManualSelectionDwellService (triggers message subscriptions)
            _ = _serviceProvider.GetRequiredService<ManualSelectionDwellService>();

            // ── Initialize overlay windows via OverlayManager ──
            var overlayManager = _serviceProvider.GetRequiredService<IOverlayManager>();
            var viewModels = new OverlayViewModels(cursorVm, activationVm, manualSelectionVm, actionMenuVm, toolbarVm, keyboardVm, manualSelectionIndicatorVm, calibrationVm, targetHighlightVm, edgeButtonBarVm, trackStatusVm, taskSwitcherVm,
                quickActionsGridVm);
            var (cursorCircle, toolbar) = InitializeOverlays(overlayManager, viewModels, settings, activeProviderType);

            // Load calibration. Screen size must be in PHYSICAL pixels to match what
            // CompleteSession persists (logical DIPs here would discard calibration on any non-100% DPI).
            var calibrationPath = Path.Combine(_appDataDir, "calibration.json");
            var screen = new PhysicalPoint(
                SystemParameters.PrimaryScreenWidth * DpiHelper.ScaleX,
                SystemParameters.PrimaryScreenHeight * DpiHelper.ScaleY);
            // Match on the monitor's EDID identity, not its \\.\DISPLAYn index: Windows renumbers those
            // on dock changes/driver updates/reboots, which used to discard the calibration silently.
            var monitorName = System.Windows.Forms.Screen.PrimaryScreen?.DeviceName ?? "";
            var monitorHardwareId = EyeCtrl.Platform.Interop.MonitorIdentity.GetHardwareId(monitorName);
            _serviceProvider.GetRequiredService<CalibrationService>()
                .Load(calibrationPath, screen, monitorName, monitorHardwareId);

            // ── Event Coordination ──
            // All PropertyChanged and message handlers are now managed by EventCoordinator
            _eventCoordinator = new EventCoordinator();
            var noteManager = _serviceProvider.GetRequiredService<INoteManager>();
            var dragTrailVm = _serviceProvider.GetRequiredService<DragTrailViewModel>();
            var coordinationContext = new EventCoordinationContext(
                cursorVm, activationVm, manualSelectionVm, manualSelectionIndicatorVm, actionMenuVm, toolbarVm, keyboardVm, backdropVm, dragTrailVm,
                overlayManager, gazeEngine, im, messenger, Dispatcher, settingsService, settings, targetHighlightVm, inputSimulator,
                _serviceProvider.GetRequiredService<CalibrationService>(),
                noteManager,
                trackStatusVm,
                taskSwitcherVm,
                _serviceProvider.GetRequiredService<IWindowManager>(),
                quickActionsGridVm,
                _serviceProvider.GetRequiredService<IQuickActionsStore>(),
                _serviceProvider.GetRequiredService<IWindowsAppLauncher>(),
                _serviceProvider.GetRequiredService<IInstalledAppProvider>(),
                _appDataDir);
            _eventCoordinator.Initialize(coordinationContext);

            // Warm the installed-app cache off the UI thread (the provider walks Shell AppsFolder
            // on its own STA worker; this just triggers the one-time enumeration early).
            System.Threading.Tasks.Task.Run(
                () => _serviceProvider.GetRequiredService<IInstalledAppProvider>().GetInstalledApps());

            var modelRegistry = _serviceProvider.GetRequiredService<ModelRegistryService>();
            var modelDownload = _serviceProvider.GetRequiredService<ModelDownloadService>();
            var modelManagement = _serviceProvider.GetRequiredService<ModelManagementService>();
            _eventCoordinator.SetModelServices(modelRegistry, modelDownload, modelManagement);

            // Load keyboard using EventCoordinator
            _eventCoordinator.LoadKeyboard(overlayManager.Keyboard, settings, inputSimulator, settingsService);

            // ── System tray ──
            bool autoHideActive = ActiveProviderType != Core.Enums.GazeProviderType.Mouse && settings.Toolbar.IsDocked;
            _trayIconManager = new TrayIconManager(
                im, Dispatcher, messenger,
                shutdownCallback: () => Shutdown(),
                showToolbarCallback: autoHideActive
                    ? () => _eventCoordinator?.ForceShowToolbar()
                    : null);

            // ── Heartbeat liveness (supervisor watchdog) ──
            // A DispatcherTimer stamps a HeartbeatPayload on the UI thread every ~3s. A bounded
            // async writer sends it over the `eyectrl_watch` named pipe to the supervisor. The
            // Dispatcher NEVER blocks on IPC — Stamp() only refreshes an in-memory slot.
            // lastFrameUtc is fed by every raw gaze sample (valid or invalid), so a wedged poll
            // loop that stops delivering frames is detectable.
            //
            // The TIMER now runs unconditionally; the WRITER is what standalone mode omits
            // (HeartbeatPublisher.EnablePublishing). Stamping with no writer costs one field
            // assignment every 3s — Stamp skips the semaphore Release when not publishing,
            // see below — and it means that if the user starts a supervisor mid-session,
            // heartbeats begin flowing the moment publishing is enabled, with no window in
            // which the supervisor has adopted an app that is not yet stamping.
            _heartbeatPublisher = new HeartbeatPublisher(supervised: settings.Supervision.Enabled);
            gazeEngine.GazeSampled += _ => _heartbeatPublisher.OnGazeSample();
            var heartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            heartbeatTimer.Tick += (_, _) =>
                _heartbeatPublisher.Stamp(gazeProvider.IsConnected, im.CurrentState == InteractionState.Paused);
            heartbeatTimer.Start();

            // ── Restart coordination ──
            // After the publisher, because the supervised strategy probes the same pipe.
            // The coordinator refuses to shut down unless a successor has been proven, so
            // a failed restart leaves a running app rather than a dark screen.
            _restartCoordinator = new AppRestartCoordinator(
                messenger,
                settings,
                tryReachSupervisor: () => _heartbeatPublisher?.TryReachSupervisor() ?? false,
                selfRestart: SelfRestartStrategy.CreateDefault(isScoped: _instanceId != null).Begin,
                shutdown: () => Shutdown(),
                reportInProgress: () =>
                {
                    Log.Information("Restart accepted; waiting for a successor.");
                    messenger.Send(new RestartInProgressMessage());
                },
                reportUnavailable: reason =>
                {
                    Log.Warning("Restart refused: {Reason}", reason);
                    messenger.Send(new RestartUnavailableMessage(reason));
                },
                runInBackground: work => System.Threading.Tasks.Task.Run(work),
                postToUi: work => Dispatcher.Invoke(work),
                supervisorInstalled: () => SupervisorLauncher.IsInstalled,
                launchSupervisor: SupervisorLauncher.TryLaunch,
                enablePublishing: () => _heartbeatPublisher?.EnablePublishing(),
                persistSettings: () =>
                {
                    settingsService.Save(settings);
                    // Supervision just turned on: the app must stop self-registering the
                    // HKCU Run key, or the next logon launches it BOTH from the Run key and
                    // from the supervisor's own launch path (a transient double-launch the
                    // mutex resolves, but a race at logon). Same reconciliation the startup
                    // block performs, with the captured `supervised` flag now known true.
                    startupRegistrar.SetStartOnBoot(
                        StartOnBootPolicy.ShouldSelfRegister(supervised: true, settings.General.StartOnBoot));
                },
                isScoped: _instanceId != null);

            // Hand the coordinator to the settings window's owner so the restart button
            // can ask whether to offer the supervisor (Task 13 Step 6).
            _eventCoordinator.SetRestartCoordinator(_restartCoordinator);

            // ── Phase 2: did every resource actually take its scoped name? ──
            // Read back off the CONSTRUCTED objects. This is what catches a receiver that accepts an
            // injectable name and is never passed one. Runs before any overlay is shown, before the
            // gaze engine starts and before the dwell pipe server binds — i.e. before anything can
            // act on input. (A window OBJECT already exists by now: ExternalDwellService's
            // constructor builds a DwellFeedbackOverlay. Nothing is shown and no input is processed.)
            //
            // The heartbeat publisher and restart coordinator are constructed ABOVE ShowOverlays
            // purely so this check can see them. There is no position that is both below them and
            // above the show/start otherwise.
            // Its own try/catch, NOT the method's. A throw in here — a changed DI registration
            // making the WindowsStartupRegistrar cast fail, an unwritable profile — would otherwise
            // land in the generic catch at the bottom of OnStartup and exit 0 via Shutdown(),
            // which is indistinguishable from a clean exit. Exit 78 is what tells a harness that
            // isolation could not be established, and it has to survive the failure being a crash
            // rather than a verdict.
            if (_instanceId != null)
            {
              try
              {
                var dwell = _serviceProvider.GetRequiredService<ExternalDwellService>();
                var registrar = (WindowsStartupRegistrar)startupRegistrar;

                var reported = new List<InstanceResource>
                {
                    // "Scoped" here means "not the live profile", which is the property that
                    // actually matters and stays true when EYECTRL_APPDATA_DIR redirects elsewhere.
                    //
                    // Compared as NORMALISED paths, case-insensitively. An ordinal compare of the
                    // raw strings fails OPEN: AppPaths returns an override verbatim, so pointing
                    // EYECTRL_APPDATA_DIR at a case- or separator-variant of the live profile
                    // ("c:\users\…\roaming\eyectrl") differs as a string while denoting the same
                    // directory — and phase 2 would certify a scoped instance that is writing the
                    // live settings.json, calibration.json and logs.
                    new("ProfileDirectory", !SamePath(_appDataDir, _liveProfileDir), _appDataDir),
                    new("AppSingletonMutex",
                        _singleInstance!.EffectiveName != SingleInstanceGuard.DefaultMutexName,
                        _singleInstance.EffectiveName),
                    new("SettingsWriterMutex",
                        settingsService.EffectiveWriterMutexName != SettingsService.DefaultWriterMutexName,
                        settingsService.EffectiveWriterMutexName),
                    new("DwellPipe",
                        dwell.EffectivePipeName != ExternalDwellService.DefaultPipeName,
                        dwell.EffectivePipeName),
                    new("StartupRunValue",
                        registrar.EffectiveValueName != WindowsStartupRegistrar.DefaultValueName,
                        registrar.EffectiveValueName),
                };

                var expected = new[]
                {
                    "ProfileDirectory", "AppSingletonMutex", "SettingsWriterMutex", "DwellPipe", "StartupRunValue",
                };
                var defaults = new Dictionary<string, string>
                {
                    ["AppSingletonMutex"] = SingleInstanceGuard.DefaultMutexName,
                    ["SettingsWriterMutex"] = SettingsService.DefaultWriterMutexName,
                    ["DwellPipe"] = ExternalDwellService.DefaultPipeName,
                    ["StartupRunValue"] = WindowsStartupRegistrar.DefaultValueName,
                };

                var verdict = InstanceScopeVerifier.Verify(reported, expected, defaults);

                // Explicit patterns, not `?.X == true`: on a null publisher that expression is
                // false, which would report a subsystem as refused when it was merely never built.
                var refusedFailures = new List<string>();
                if (_heartbeatPublisher is null)
                    refusedFailures.Add("HeartbeatPublisher was never constructed, so it cannot be verified.");
                else if (_heartbeatPublisher.IsPublishing)
                    refusedFailures.Add("HeartbeatPublisher is publishing on a scoped instance.");

                if (_restartCoordinator is null)
                    refusedFailures.Add("AppRestartCoordinator was never constructed, so it cannot be verified.");
                else if (!_restartCoordinator.IsScopeGated)
                    refusedFailures.Add("AppRestartCoordinator is not scope-gated.");

                if (!verdict.IsSatisfied || refusedFailures.Count > 0)
                {
                    foreach (var failure in verdict.Failures.Concat(refusedFailures))
                        Log.Fatal("Instance scoping failed: {Failure}", failure);
                    Log.CloseAndFlush();
                    Shutdown(InstanceStartupPolicy.RefusalExitCode);
                    return;
                }

                // Reached only once both checks above passed, so these two names are a statement
                // about what was verified rather than a literal claim.
                var refused = new List<string> { "Supervision", "SelfRestart" };

                InstanceManifest.Write(_appDataDir, new InstanceManifest(
                    _instanceId, Environment.ProcessId, DateTime.UtcNow, IsTestMode,
                    reported, refused));

                Log.Information(
                    "Scoped instance {InstanceId} verified; manifest written to {ProfileDirectory}.",
                    _instanceId, _appDataDir);
              }
              catch (Exception ex)
              {
                  // Same exit code as a verdict of "not scoped". A harness cannot tell the
                  // difference between "we proved isolation failed" and "we could not tell", and it
                  // must not drive real input in either case.
                  Log.Fatal(ex, "Instance scoping could not be verified; refusing to start.");
                  Log.CloseAndFlush();
                  Shutdown(InstanceStartupPolicy.RefusalExitCode);
                  return;
              }
            }

            // ── Close splash and show persistent overlays (last, after all wiring) ──
            ShowOverlays(splash, cursorCircle, toolbar, settings);

            if (settings.General.StartPaused || IsPausedLaunch)
                im.TogglePause();

            gazeEngine.Start();

            // External dwell pipe server (cooperating-app control activation).
            _serviceProvider.GetRequiredService<ExternalDwellService>().Start();

            // Eye-care break reminder: subscribe to raw gaze samples for scheduler + fatigue monitor.
            // Resolving IEyeCareService constructs the singleton and its EyeCareHooks,
            // which registers for GazeCursorMovedMessage to feed gaze into overlays.
            // Subscription lives for the process lifetime (eyeCare + gazeEngine are app-lifetime
            // singletons), so no explicit unsubscribe is needed.
            //
            // ORDERING: this subscription must stay AHEAD of the GazeQualityBridge one at the end of
            // this method. Handlers run in registration order, and the bridge asks eye care whether
            // its fatigue score is fresh *for the sample being handled* — an answer that is only yes
            // once this handler has stamped it. Registered the other way round, every reading is
            // stale and adaptation silently sees a fatigue score of 0 forever.
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

            // Struggle-detection adaptation. AdaptationActivation starts and stops the whole runtime —
            // coordinator tick timer, the cue / drift-notice / log subscribers (resolving the cue service is
            // what constructs its overlay HWND), and the system-wide UIA focus hook — on the
            // AdaptationSettings.Enabled edge. Since the setting is off by default, none of that exists until
            // the user opts in, and it is torn back down when they opt out.
            _serviceProvider.GetRequiredService<AdaptationActivation>().Start(gazeEngine);

            // The manual escape (§8.6) is NOT gated on Enabled: it is one messenger registration with no
            // runtime cost, and the reset path should be reachable rather than conditional.
            _serviceProvider.GetRequiredService<AdaptationResetHandler>().Start();

            // Gaze-quality telemetry. The bridge substitutes 0 for a stale fatigue score, so a
            // faulting eye-care handler degrades to the documented "disabled" value rather than
            // freezing its last reading into adaptation.
            //
            // ORDERING: must stay AFTER the eye-care subscription above, which is what makes the
            // score fresh for this sample. Moving this line earlier does not fail loudly — it just
            // reports 0 fatigue for the rest of the process. See the note at that subscription.
            var gazeQuality = _serviceProvider.GetRequiredService<GazeQualityBridge>();
            gazeEngine.GazeSampled += gazeQuality.OnGazeSample;
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

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // C3: a Windows logoff/shutdown is an INTENTIONAL exit, not a crash. Signal the supervisor's
        // authenticated clean-shutdown (shuttingDown:true) BEFORE the session ends so it does not
        // treat the logoff as a crash and fight it with a relaunch. Do NOT cancel the session.
        // (Spec lines 128, 235, 426: OnSessionEnding also sends shuttingDown:true.)
        Log.Information("Session ending (reason={Reason}); signalling clean shutdown to supervisor.", e.ReasonSessionEnding);
        _heartbeatPublisher?.SignalShuttingDown();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Shutdown");

        // Stamp the final shuttingDown:true payload BEFORE tearing down services — the supervisor
        // needs this authenticated clean-shutdown signal within its 5s deadline to distinguish a
        // graceful exit (no restart) from a crash. Cancels the writer loop and drains it (bounded).
        // Carries the restart intent to the supervisor. False for every ordinary exit —
        // and also for a self-restart, where a replacement is already coming up and a
        // supervisor-launched second instance would just lose the race for the mutex.
        _heartbeatPublisher?.SignalShuttingDown(
            restartRequested: _restartCoordinator?.SupervisorRelaunchRequested ?? false);

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

        _heartbeatPublisher?.Dispose();

        // Release the single-instance mutex LAST. A restart child is queued on it
        // (Task 6's waiting acquire) and starts initialising the instant it is released, so
        // everything this instance still owns — the settings file, the Tobii device, the
        // external-dwell pipe — must already be released. Releasing it at the TOP of OnExit
        // was harmless only while a second instance exited immediately instead of waiting.
        _singleInstance?.Dispose();

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
             EdgeButtonBarViewModel EdgeButtonBarVm,
             TrackStatusViewModel TrackStatusVm,
             TaskSwitcherViewModel TaskSwitcherVm,
             QuickActionsGridViewModel QuickActionsGridVm) ResolveViewModels(EyeCtrlSettings settings)
    {
        var vms = AppBootstrapper.ResolveViewModels(_serviceProvider, settings);
        var backdropVm = _serviceProvider.GetRequiredService<GlassBackdropViewModel>();
        return (vms.CursorCircle, vms.ActivationButton, vms.ManualSelectionOverlay, vms.ActionMenu,
                vms.Toolbar, vms.Keyboard, vms.ManualSelectionIndicator, backdropVm,
                vms.Calibration, vms.TargetHighlight, vms.EdgeButtonBar, vms.TrackStatus, vms.TaskSwitcher,
                vms.QuickActionsGrid);
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
    /// Resolves the instance, derives the profile directory (stored in <see cref="_appDataDir"/>),
    /// initializes Serilog, and registers the global (AppDomain + dispatcher + task-scheduler)
    /// exception handlers.
    /// <para>
    /// Split out of settings loading so phase 1 can log its refusals, and so a refused or rejected
    /// instance no longer loads settings — which matters because <c>SettingsService.Load</c> can
    /// write a migrated file back, and doing that from an instance that is about to exit 78 would
    /// touch the very profile it was refused for.
    /// </para>
    /// </summary>
    /// <param name="instanceId">Validated instance id, or <c>null</c> for the default instance.</param>
    private void InitializeInstanceAndLogging(string? instanceId)
    {
        _instanceId = instanceId;

        // The ONE place this process reads %APPDATA%. AppPaths takes it as a parameter by design —
        // that is what keeps the resolver pure and testable — so the composition root is where it
        // has to be read. Both the live profile and this instance's profile come from this call, so
        // phase 2 can compare them without deriving anything a second time.
        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _liveProfileDir = AppPaths.Resolve(null, roamingAppData, null);
        _appDataDir = AppPaths.Resolve(
            Environment.GetEnvironmentVariable(AppPaths.OverrideEnvVar), roamingAppData, instanceId);

        var logsDir = Path.Combine(_appDataDir, "logs");
        LoggingInitializer.Initialize(logsDir);

        // Say so when the profile has been redirected without an instance id. That combination
        // relocates files ONLY: the singleton mutex, the dwell pipe and the HKCU Run value all keep
        // their production names, so this instance still collides with the live one on every
        // process-external resource. Silence here is what would let someone believe they were
        // isolated because their settings.json moved.
        if (_instanceId == null && !SamePath(_appDataDir, _liveProfileDir))
        {
            Log.Warning(
                "{OverrideVar} redirected the profile to {ProfileDirectory} but {InstanceVar} is not "
                + "set. This moves files only — the single-instance mutex, dwell pipe and autostart "
                + "value keep their production names and still collide with the default instance.",
                AppPaths.OverrideEnvVar, _appDataDir, InstanceScope.EnvVar);
        }

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception (terminating={IsTerminating})",
                args.IsTerminating);
            Log.CloseAndFlush();
        };

        // Deliberately do NOT set args.Handled = true. We log + let the process
        // crash so the supervisor's respawn owns recovery, rather than the app
        // limping on in a corrupt state.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Unhandled dispatcher exception");
        };

        // Safety net for fire-and-forget async work (notably the heartbeat writer
        // added by the supervisor integration): an unobserved task fault must not
        // crash the process. Log it, then mark observed to suppress the
        // UnobservedTaskException-to-process-crash escalation.
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception (observed set to true to prevent crash)");
            args.SetObserved();
        };
    }

    /// <summary>
    /// Whether two paths denote the same directory. Normalises separators and relative segments via
    /// <see cref="Path.GetFullPath(string)"/> and compares case-insensitively, because Windows paths
    /// are case-insensitive and <see cref="AppPaths.Resolve"/> returns an explicit override
    /// verbatim. An ordinal compare of the raw strings fails OPEN, which on the one check designed
    /// to fail closed is the wrong direction.
    /// <para>
    /// Falls back to a case-insensitive compare of the raw strings if a path cannot be normalised.
    /// </para>
    /// </summary>
    internal static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                      or PathTooLongException or System.Security.SecurityException)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Loads settings from the resolved profile, migrating newly-introduced defaults, and forces
    /// supervision off on a scoped instance.
    /// </summary>
    private (EyeCtrlSettings Settings, string SettingsFilePath) LoadSettings()
    {
        var settingsFilePath = Path.Combine(_appDataDir, "settings.json");
        var tempSettingsService = new SettingsService(
            settingsFilePath, SettingsWriter.App, logger: null,
            writerMutexName: InstanceScope.Scope(SettingsService.DefaultWriterMutexName, _instanceId));
        var settings = tempSettingsService.Load();

        // Migrate settings for newly-introduced features
        settings.ActionMenu.EnsureDefaults();

        // Supervision is FORCED off on a scoped instance rather than refused, because the refusal
        // could not be ordered: the flag lives only in settings, which load here — after phase 1.
        // Forcing deletes the ordering question. This is an in-memory flip; the scoped profile is
        // ours to write, and the live one is never opened by a scoped instance at all.
        if (_instanceId != null && settings.Supervision.Enabled)
        {
            Log.Warning(
                "Scoped instance {InstanceId}: forcing Supervision.Enabled off. A scoped instance "
                + "runs alone and refuses supervision.", _instanceId);
            settings.Supervision.Enabled = false;
        }

        return (settings, settingsFilePath);
    }
}
