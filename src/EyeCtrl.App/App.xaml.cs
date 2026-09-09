namespace EyeCtrl.App;

using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Diagnostics;
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
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
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
    private DisplayGeometryWatcher? _displayGeometryWatcher;
    private string _appDataDir = null!;

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

        // A restart replacement announces itself BEFORE queueing for the mutex: the
        // outgoing instance is waiting for exactly this before it exits, and it still
        // holds the mutex while it waits. Reversing the order deadlocks.
        if (isRestartHandoff)
        {
            RestartHandoff.SignalReady();
        }

        // Single-instance guard: a second instance exits fast. The supervisor's respawn
        // is legitimate, but a stray double-click/race must not launch two instances
        // fighting over the same gaze device, settings file, and dwell pipe.
        // Local\ (not Global\): multi-user/RDP is a non-goal; avoids cross-session collisions.
        //
        // A restart replacement is the one case that must WAIT rather than fail fast: the
        // instance it replaces holds the mutex until the very end of its own OnExit.
        _singleInstance = new SingleInstanceGuard();
        if (!_singleInstance.TryAcquire(isRestartHandoff ? RestartHandoff.MutexWait : TimeSpan.Zero))
        {
            // Another instance is already running (likely the supervisor's legitimate one),
            // or — for a handoff — the outgoing instance never let go. Either way, exit:
            // one working instance beats two fighting over the gaze device.
            //
            // Deliberately unlogged: InitializeSettingsAndLogging() has not run yet, so
            // Serilog's static Log is still a SilentLogger and a Log.Information here would
            // compile and then discard the line. See the note below for what that costs.
            Shutdown();
            return;
        }

        SplashWindow? splash = null;
        try
        {
            var (settings, settingsFilePath) = InitializeSettingsAndLogging();

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
                settings, settingsFilePath, Dispatcher, IsTestMode, AppBootstrapper.DefaultAppDataDir);

            // ── Resolve services ──
            var settingsService = _serviceProvider.GetRequiredService<SettingsService>();
            var gazeEngine = _serviceProvider.GetRequiredService<GazeEngine>();
            var messenger = _serviceProvider.GetRequiredService<IMessenger>();
            var inputSimulator = _serviceProvider.GetRequiredService<IInputSimulator>();
            var gazeProvider = _serviceProvider.GetRequiredService<IGazeProvider>();

            // Reconcile auto-launch registration with the persisted setting.
            // In supervised mode the supervisor is MEANT to own launch-at-logon via a Scheduled
            // Task, and self-registering as well would double-launch at next logon. But the task
            // does not exist yet (no installer), so handing over unconditionally deleted the Run
            // key and left nothing at all starting the app — see SupervisorLaunchRegistration.
            // Until something demonstrably owns launch, the app keeps its own registration and
            // the single-instance mutex arbitrates any transient double-launch.
            // `supervised` is captured once at launch: live-toggling supervision mode isn't
            // supported. The PropertyChanged handler only re-reacts to StartOnBoot changes.
            var startupRegistrar = _serviceProvider.GetRequiredService<IStartupRegistrar>();
            var supervised = settings.Supervision.Enabled;
            var supervisorOwnsLaunch = SupervisorLaunchRegistration.IsRegistered;
            startupRegistrar.SetStartOnBoot(StartOnBootPolicy.ShouldSelfRegister(
                supervised, settings.General.StartOnBoot, supervisorOwnsLaunch));

            // Live-toggle: if the user changes StartOnBoot in settings, apply immediately.
            settings.General.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(settings.General.StartOnBoot))
                    startupRegistrar.SetStartOnBoot(StartOnBootPolicy.ShouldSelfRegister(
                        supervised, settings.General.StartOnBoot, supervisorOwnsLaunch));
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
                 quickActionsGridVm, modesMenuVm) = ResolveViewModels(settings);

            // Resolve adapter (triggers Core event subscriptions)
            _ = _serviceProvider.GetRequiredService<GazeEngineAdapter>();

            // Resolve ManualSelectionDwellService (triggers message subscriptions)
            _ = _serviceProvider.GetRequiredService<ManualSelectionDwellService>();

            // Resolve the dwell-time router (Phase 1c dwell routing): it subscribes to the effective-
            // settings provider and the dwell-detector registry in its ctor and applies the current
            // values immediately, so it must exist before any surface registers.
            _ = _serviceProvider.GetRequiredService<DwellTimeRouter>();

            _ = _serviceProvider.GetRequiredService<GeometryRouter>();

            // ── Initialize overlay windows via OverlayManager ──
            var overlayManager = _serviceProvider.GetRequiredService<IOverlayManager>();
            var viewModels = new OverlayViewModels(cursorVm, activationVm, manualSelectionVm, actionMenuVm, toolbarVm, keyboardVm, manualSelectionIndicatorVm, calibrationVm, targetHighlightVm, edgeButtonBarVm, trackStatusVm, taskSwitcherVm,
                quickActionsGridVm, modesMenuVm);
            var (cursorCircle, toolbar) = InitializeOverlays(overlayManager, viewModels, settings, activeProviderType);

            // Stage-2 UI instrument 2, attached only for a measurement session. Without the attach the
            // ring's position path is exactly what it was; with it, the window reads back where it
            // actually landed and reports — it still positions itself the same way.
            // The capability query is the seam: ICursorCircleOverlay deliberately does not carry the
            // instrument, so an overlay that cannot be instrumented is simply not instrumented.
            if (_serviceProvider.GetService<RingQuantizationProbe>() is { } ringProbe
                && cursorCircle is IRingQuantizationInstrument instrumentable)
            {
                instrumentable.AttachQuantizationProbe(ringProbe, settings.Diagnostics);
                Log.Information("UI instruments are ON for this session (opt-in): dispatch latency + ring quantization");
            }

            // Calibration is loaded by CalibrationService's own DI factory, not here. It used to be
            // loaded at this line — some eighty lines after the gaze provider was resolved — and the
            // raw-gaze recorder builds its header from the calibration state when the provider is
            // constructed. So every recording made on a calibrated machine was stamped
            // calibrationActive:false with a null transform, and since replay applies that transform
            // such a recording cannot be replayed as the calibrated session it actually was.
            //
            // Moving the load into the factory removes the ordering question instead of re-answering
            // it in a new place: whoever resolves the service first triggers the load, so no consumer
            // can see it unloaded however this method is later rearranged.

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
                modesMenuVm);
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

            // ── Close splash and show persistent overlays (last, after all wiring) ──
            ShowOverlays(splash, cursorCircle, toolbar, settings);

            if (settings.General.StartPaused || IsPausedLaunch)
                im.TogglePause();

            gazeEngine.Start();

            // ── Display / resolution / DPI change ──
            // Started AFTER ShowOverlays, so the toolbar exists and can be re-measured when the
            // first notification arrives, and after gazeEngine.Start() because the provider is what
            // the new bounds are handed to.
            //
            // Nothing used to listen for this at all: the DPI scale, the provider's screen bounds
            // and the toolbar's screen size + summon rect were captured once here at startup and
            // never refreshed. A dock, undock, resolution change or RDP reconnect then left gaze
            // addressing the wrong pixel space with dwell targets displaced from where they were
            // drawn — and the supervisor could not see it, because a stale-geometry app still
            // delivers frames, so the heartbeat stayed fresh.
            _displayGeometryWatcher = new DisplayGeometryWatcher(
                gazeProvider,
                messenger,
                read: ReadDisplayGeometry,
                postToUi: work => Dispatcher.BeginInvoke(work),
                initial: ReadDisplayGeometry(),
                subscribe: h => SystemEvents.DisplaySettingsChanged += h,
                unsubscribe: h => SystemEvents.DisplaySettingsChanged -= h);
            _displayGeometryWatcher.Start();

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
                selfRestart: SelfRestartStrategy.CreateDefault().Begin,
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
                supervisorInstalled: () => SupervisorLauncher.IsExecutablePresent,
                launchSupervisor: SupervisorLauncher.TryLaunch,
                enablePublishing: () => _heartbeatPublisher?.EnablePublishing(),
                persistSettings: () =>
                {
                    settingsService.Save(settings);
                    // Supervision just turned on. Handing the HKCU Run key over avoids a
                    // double-launch at next logon — but ONLY if something else is registered to
                    // launch the app. Nothing is, until the installer creates the Scheduled
                    // Task, so this reconciliation now asks rather than assumes. Surrendering
                    // the Run key to a mechanism that does not exist is a lockout, not a race.
                    // Same reconciliation the startup block performs, with `supervised` true.
                    startupRegistrar.SetStartOnBoot(StartOnBootPolicy.ShouldSelfRegister(
                        supervised: true,
                        settings.General.StartOnBoot,
                        SupervisorLaunchRegistration.IsRegistered));
                });

            // Hand the coordinator to the settings window's owner so the restart button
            // can ask whether to offer the supervisor (Task 13 Step 6).
            _eventCoordinator.SetRestartCoordinator(_restartCoordinator);

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
            // coordinator tick timer, the cue / drift-notice / log / caregiver-alert subscribers (resolving the cue service is
            // what constructs its overlay HWND), and the system-wide UIA focus hook — on the
            // AdaptationSettings.Enabled edge. Since the setting is off by default, none of that exists until
            // the user opts in, and it is torn back down when they opt out.
            // §7.4 persisted learned offsets — only read when the feature is on (§9.1 disabled = current
            // cost); a mid-session first enable then starts learned at zero, conservatively.
            if (settings.Adaptation.Enabled)
                _serviceProvider.GetRequiredService<AdaptationLearner>().Load();
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

        // Before the coordinators: SystemEvents holds a static reference to the handler, so an
        // un-unsubscribed watcher outlives the app object and would fire into disposed services.
        _displayGeometryWatcher?.Dispose();

        _trayIconManager?.Dispose();
        _eventCoordinator?.Dispose();

        if (_serviceProvider != null)
        {
            var gazeEngine = _serviceProvider.GetRequiredService<GazeEngine>();
            var settings = _serviceProvider.GetRequiredService<EyeCtrlSettings>();
            var settingsService = _serviceProvider.GetRequiredService<SettingsService>();

            gazeEngine.Stop();
            settingsService.Save(settings);
            _serviceProvider.GetRequiredService<AdaptationLearner>().Persist(); // best-effort (§11); never throws
            // (User dictionary is saved by EventCoordinator.Dispose above, via PredictionStackFactory.)

            // Instrument 2's final verdict, before the overlays go away. Instrument 1 reports from
            // GazeEngineAdapter.Dispose just below.
            try
            {
                if (_serviceProvider.GetRequiredService<IOverlayManager>().CursorCircle
                    is IRingQuantizationInstrument instrumented)
                {
                    instrumented.ReportQuantization();
                }
            }
            catch (Exception ex) { Log.Warning(ex, "Could not emit the ring-quantization summary"); }

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
             QuickActionsGridViewModel QuickActionsGridVm,
             ModesMenuViewModel ModesMenuVm) ResolveViewModels(EyeCtrlSettings settings)
    {
        var vms = AppBootstrapper.ResolveViewModels(_serviceProvider, settings);
        var backdropVm = _serviceProvider.GetRequiredService<GlassBackdropViewModel>();
        return (vms.CursorCircle, vms.ActivationButton, vms.ManualSelectionOverlay, vms.ActionMenu,
                vms.Toolbar, vms.Keyboard, vms.ManualSelectionIndicator, backdropVm,
                vms.Calibration, vms.TargetHighlight, vms.EdgeButtonBar, vms.TrackStatus, vms.TaskSwitcher,
                vms.QuickActionsGrid, vms.ModesMenu);
    }

    private (ICursorCircleOverlay CursorCircle, IToolbarOverlay Toolbar) InitializeOverlays(
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
        ICursorCircleOverlay cursorCircle,
        IToolbarOverlay toolbar,
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
    /// Reads the display's current addressable space: primary screen bounds in physical pixels and
    /// the logical-to-physical scale.
    /// </summary>
    /// <remarks>
    /// Uses the Win32 system DPI rather than a window's composition target, because this is called
    /// after a display change when no splash exists and the toolbar's own source may be mid-move.
    /// <c>SystemParameters</c> reports logical (DIP) units and WPF invalidates its cache on the
    /// display-change message, so multiplying by the freshly-read scale yields physical pixels —
    /// the same derivation <c>AppBootstrapper</c> uses to size the provider at startup.
    /// </remarks>
    private static DisplayGeometry ReadDisplayGeometry()
    {
        var scale = GetDpiForSystem() / (double)DpiStandardScale;
        return new DisplayGeometry(
            SystemParameters.PrimaryScreenWidth * scale,
            SystemParameters.PrimaryScreenHeight * scale,
            scale,
            scale);
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
        var tempSettingsService = new SettingsService(settingsFilePath, SettingsWriter.App);
        var settings = tempSettingsService.Load();

        // Migrate settings for newly-introduced features. Once-only: the action list is
        // user-editable in Settings since 2026-08-17, so re-adding defaults every startup
        // would undo a removal the user made there.
        settings.ActionMenu.EnsureDefaultsOnce();
        // Same once-only pattern, same reason: the primary four are user-editable in Settings, so
        // a per-startup migration would re-promote an action the user demoted on every launch.
        settings.ActionMenu.MigratePrimaryOnce();
        // Same once-only pattern: a persisted old-default toolbar size (80) would override the
        // new 120 (mis-dwell fix) forever without this bump.
        settings.Toolbar.MigrateSizeOnce();

        var logsDir = Path.Combine(_appDataDir, "logs");
        LoggingInitializer.Initialize(logsDir);

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

        return (settings, settingsFilePath);
    }
}
