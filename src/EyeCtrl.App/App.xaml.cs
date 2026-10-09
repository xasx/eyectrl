namespace EyeCtrl.App;

using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Localization;
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
    private SupervisorRequestService? _supervisorRequests;
    private AppRestartCoordinator? _restartCoordinator;
    private DisplayGeometryWatcher? _displayGeometryWatcher;
    private string _appDataDir = null!;
    private DispatcherTimer? _heartbeatTimer;

    // Dispatcher-fault budget for the standalone crash policy (see InitializeApp's
    // DispatcherUnhandledException registration). Touched only on the UI thread.
    private readonly RollingFaultBudget _dispatcherFaultBudget = new(10, TimeSpan.FromSeconds(60));

    // Ticket 07: the composition-built presence predicate (delivered-heartbeat freshness). The
    // crash handler calls only this; forcing presence true/false is a composition mutation covered
    // by tests, not untestable glue here. Initialised safe: false means the standalone budget,
    // which is the right regime before the composition has run.
    private Func<bool> _isSupervisorPresent = () => false;

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
            var (settings, settingsFilePath) = InitializeSettingsAndLogging(e.Args);

            // ── String table ──
            // After logging exists (the load path logs, never fails) and before the theme and the
            // splash, because everything downstream reads StringTable.Current the moment it is
            // constructed. English is compiled into Core; this activates the persisted selection,
            // with System resolved against the Windows UI culture the process already runs under.
            StringTable.Activate(StringTable.Load(
                settings.General.UiLanguage,
                CultureInfo.CurrentUICulture,
                AppDomain.CurrentDomain.BaseDirectory,
                message => Log.Warning("String table: {Message}", message)));

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
            // The reconciler reads supervision.enabled and the registration probe LIVE, so a row
            // toggle later in the session is honoured by a subsequent StartOnBoot change; the
            // policy computation is shared with the supervision composition (see the factory).
            var startupRegistrar = _serviceProvider.GetRequiredService<IStartupRegistrar>();
            var reconcileRunKey = SupervisionComposition.CreateProductionRunKeyReconciler(
                settings, startupRegistrar);
            reconcileRunKey();

            // Live-toggle: if StartOnBoot changes (Settings row or Supervisor request), apply immediately.
            SupervisionComposition.ReconcileRunKeyOnStartOnBootChange(settings, reconcileRunKey);

            var activeProviderType = GazeProviderFactory.GetProviderType(gazeProvider);
            ActiveProviderType = activeProviderType;
            var version = System.Reflection.Assembly.GetExecutingAssembly()
                .GetName().Version?.ToString() ?? "unknown";
            Log.Information("Startup: EyeCtrl v{Version}, provider={Provider}, settings={SettingsPath}",
                version, activeProviderType, settingsFilePath);
            var im = gazeEngine.InteractionManager;

            // ── Resolve ViewModels ──
            var (cursorVm, activationVm, manualSelectionVm, actionMenuVm, toolbarVm, keyboardVm,
                 manualSelectionIndicatorVm, backdropVm, calibrationVm, targetHighlightVm, edgeButtonBarVm, actionMenuModBarVm, trackStatusVm, taskSwitcherVm,
                 quickActionsGridVm, modesMenuVm, repeatCursorVm) = ResolveViewModels(settings);

            // Resolve adapter (triggers Core event subscriptions)
            _ = _serviceProvider.GetRequiredService<GazeEngineAdapter>();

            // Resolve the ctor-subscribing singletons nothing else depends on
            // (ManualSelectionDwellService, DwellTimeRouter, GeometryRouter, DwellCueService):
            // DI singletons are lazy, so they must be eagerly resolved here to subscribe.
            AppBootstrapper.ResolveEagerServices(_serviceProvider);

            // ── Initialize overlay windows via OverlayManager ──
            var overlayManager = _serviceProvider.GetRequiredService<IOverlayManager>();
            var viewModels = new OverlayViewModels(cursorVm, activationVm, manualSelectionVm, actionMenuVm, toolbarVm, keyboardVm, manualSelectionIndicatorVm, calibrationVm, targetHighlightVm, edgeButtonBarVm, actionMenuModBarVm, trackStatusVm, taskSwitcherVm,
                quickActionsGridVm, modesMenuVm, repeatCursorVm);
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
                modesMenuVm,
                repeatCursorVm,
                _serviceProvider.GetRequiredService<IGlobalEscapeHatch>(),
                _serviceProvider.GetRequiredService<UserDictionaryService>());
            _eventCoordinator.Initialize(coordinationContext);

            // Surface a degraded hatch at startup (review PR #142): a failed or policy-blocked
            // hook is otherwise silent, and ESC is the caregiver's gaze-independent exit.
            if (!_serviceProvider.GetRequiredService<IGlobalEscapeHatch>().IsActive)
                Log.Warning("Global escape hatch is NOT active — ESC will not end drag/repeat; the gaze exits remain.");

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

            // ORDER LOAD-BEARING (spec O4): this pause push must stay AFTER ShowOverlays — the
            // startup band show bypasses ToolbarVisibilityAdapter, so on --paused/startPaused
            // launches the strip's show+raise lands here, on top of the already-shown band.
            // Moving this above ShowOverlays hides the strip under the band until the next
            // band transition. The same call also emits the startup provenance line (which
            // source — --paused, general.startPaused, neither — caused the state).
            PauseLaunchProvenance.Apply(IsPausedLaunch, settings.General.StartPaused, im,
                line => Log.Information("{PauseState}", line));

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
            _heartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _heartbeatTimer.Tick += (_, _) =>
                _heartbeatPublisher.Stamp(gazeProvider.IsConnected, im.CurrentState == InteractionState.Paused);
            _heartbeatTimer.Start();

            // ── Restart coordination ──
            // After the publisher, because the supervised strategy probes the same pipe.
            // The coordinator refuses to shut down unless a successor has been proven, so
            // a failed restart leaves a running app rather than a dark screen.
            //
            // The whole supervision graph is composed by ONE testable factory
            // (SupervisionComposition): the shared bring-up and the Settings-row toggle, both
            // persisting through the sanctioned App exception (a Supervisor-writer
            // SettingsService, never the App writer — an App-writer Save overlays the on-disk
            // Supervision section and would silently discard the flip), both reconciling the
            // Run key through the same live predicate built above. CreateForProduction binds the
            // static probes (the launcher and the launch-registration question) itself, so this
            // call site cannot pass a wrong answer for them.
            var (bringUp, supervisionToggle, _, isSupervisorPresent) = SupervisionComposition.CreateForProduction(
                settings,
                settingsFilePath,
                startupRegistrar,
                publisher: () => _heartbeatPublisher,
                runInBackground: work => System.Threading.Tasks.Task.Run(work),
                postToUi: work => Dispatcher.Invoke(work));
            // Ticket 07 review F1: the crash handler's presence predicate is the composition's,
            // built from the same publisher as publishing, the probe and the row's connected read.
            _isSupervisorPresent = isSupervisorPresent;

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
                bringUp: bringUp);

            // Hand the coordinator to the settings window's owner so the restart button
            // can ask whether to offer the supervisor (Task 13 Step 6), and the supervision
            // toggle so the General tab can drive it.
            _eventCoordinator.SetRestartCoordinator(_restartCoordinator);
            _eventCoordinator.SetSupervisionToggle(supervisionToggle);

            // Ticket 07: after a reboot nothing else starts the Supervisor (no installer task
            // exists), yet enabled=true arms the kill-for-respawn crash policy. Run the write-free
            // ensure now — non-blocking, and through the toggle's latch so the row shows progress
            // and a concurrent retry dwell cannot start a second launch. No persist, no Run-key.
            supervisionToggle.RequestEnsure();

            // External dwell pipe server (cooperating-app control activation).
            _serviceProvider.GetRequiredService<ExternalDwellService>().Start();

            // Supervisor → App request channel: the Supervisor asks, the App saves (single writer).
            _supervisorRequests = new SupervisorRequestService(
                new SupervisorRequestHandler(
                    settings, settingsService.Save,
                    _serviceProvider.GetRequiredService<ILogger<SupervisorRequestHandler>>(),
                    // A live SetUiLanguage restarts through the same coordinator as the App's own
                    // language row, after its acknowledgement is out (hook (b)).
                    isRestartInProgress: () => _restartCoordinator?.IsRestartInProgress ?? false,
                    requestRestart: () => _restartCoordinator?.HandleRestart(startSupervisor: false)).Handle,
                runOnUi: work => Dispatcher.InvokeAsync(work).Task,
                _serviceProvider.GetRequiredService<ILogger<SupervisorRequestService>>());
            _supervisorRequests.Start();

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
            // Shutdown BEFORE showing any dialog: OnExit releases the single-instance mutex, and a
            // blocking MessageBox shown first held the mutex for as long as nobody clicked OK —
            // for a gaze-only user that turned one failed boot into "no relaunch can succeed
            // until a caregiver arrives". The dispatcher drains while the box's native message
            // pump runs, so OnExit completes under the dialog. OnExit also flushes the log.
            Shutdown();
            // The box itself auto-dismisses: nobody present may be able to click OK, and an
            // unattended retry loop (Run key, caregiver script) must be able to make progress.
            // Title and message are resolved once (T06): the title is the auto-dismiss's exact
            // lookup key, and a second resolution could hand FindWindowW a different string.
            var startupError = StartupErrorText.Resolve(ex.Message);
            DismissMessageBoxAfter(startupError.Title, TimeSpan.FromSeconds(20));
            MessageBox.Show(
                startupError.Message,
                startupError.Title,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// A SendInput drag button-down is OS-global state that outlives the process: dying with the
    /// button held leaves the successor in FreeLook, where the user's gaze becomes a drag they
    /// cannot stop. Best-effort and exception-free — callers are fatal-exception paths with
    /// nothing left to protect.
    /// </summary>
    private void ReleaseHeldDragButtonForFatalExit()
    {
        try { _eventCoordinator?.ReleaseHeldDragButtonForFatalExit(); }
        catch { /* the process is dying anyway */ }
    }

    /// <summary>Finds a top-level window by exact title and posts WM_CLOSE to it after
    /// <paramref name="delay"/> — the auto-dismiss for the startup-error box. Best-effort: a box
    /// that stays up no longer holds anything (shutdown already ran).</summary>
    private static async void DismissMessageBoxAfter(string title, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay).ConfigureAwait(false);
            var hwnd = FindWindowW(null, title);
            if (hwnd != IntPtr.Zero)
                PostMessageW(hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // Ignore: worst case the box stays until clicked.
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr FindWindowW(string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

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

        // Every step is best-effort: an exception in any step used to skip every step after it —
        // including the single-instance mutex release and the log flush — and then rethrow into
        // the dying dispatcher. The ORDER below is unchanged (see the original ordering notes).
        void Step(string what, Action act)
        {
            try { act(); }
            catch (Exception ex) { Log.Error(ex, "OnExit step failed: {Step}", what); }
        }

        // Stamp the final shuttingDown:true payload BEFORE tearing down services — the supervisor
        // needs this authenticated clean-shutdown signal within its 5s deadline to distinguish a
        // graceful exit (no restart) from a crash. Cancels the writer loop and drains it (bounded).
        // Carries the restart intent to the supervisor. False for every ordinary exit —
        // and also for a self-restart, where a replacement is already coming up and a
        // supervisor-launched second instance would just lose the race for the mutex.
        Step("signal shutting down", () => _heartbeatPublisher?.SignalShuttingDown(
            restartRequested: _restartCoordinator?.SupervisorRelaunchRequested ?? false));

        // Stop ticking before the publisher goes away: a tick racing Dispose would call Stamp on
        // a disposed publisher from the dispatcher.
        Step("stop heartbeat timer", () => _heartbeatTimer?.Stop());

        // Stop taking Supervisor requests before the exit save: a request mid-flight now sees the
        // pipe close, and the Supervisor parks it and re-sends to the next App.
        Step("dispose supervisor request service", () => _supervisorRequests?.Dispose());

        // Before the coordinators: SystemEvents holds a static reference to the handler, so an
        // un-unsubscribed watcher outlives the app object and would fire into disposed services.
        Step("dispose display geometry watcher", () => _displayGeometryWatcher?.Dispose());

        Step("dispose tray icon", () => _trayIconManager?.Dispose());
        Step("dispose event coordinator", () => _eventCoordinator?.Dispose());

        if (_serviceProvider != null)
        {
            var gazeEngine = _serviceProvider.GetRequiredService<GazeEngine>();
            var settings = _serviceProvider.GetRequiredService<EyeCtrlSettings>();
            var settingsService = _serviceProvider.GetRequiredService<SettingsService>();

            Step("stop gaze engine", () => gazeEngine.Stop());
            // Best-effort by design: Save throws on validation failure, and a failed final save
            // must not cost the caller its remaining teardown (previously this throw skipped
            // provider disposal, the mutex release and the log flush).
            Step("save settings", () => settingsService.Save(settings));
            Step("persist adaptation state", () => _serviceProvider.GetRequiredService<AdaptationLearner>().Persist()); // best-effort (§11); never throws
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
            Step("dispose gaze adapter", () => adapter.Dispose());
            Step("dispose gaze engine", () => gazeEngine.Dispose());
            Step("dispose external dwell service", () => _serviceProvider.GetRequiredService<ExternalDwellService>().Dispose());
            Step("dispose service provider", () => _serviceProvider.Dispose()); // Disposes all IDisposable singletons including ManualSelectionDwellService
        }

        Step("dispose heartbeat publisher", () => _heartbeatPublisher?.Dispose());

        // Release the single-instance mutex LAST. A restart child is queued on it
        // (Task 6's waiting acquire) and starts initialising the instant it is released, so
        // everything this instance still owns — the settings file, the Tobii device, the
        // external-dwell pipe — must already be released. Releasing it at the TOP of OnExit
        // was harmless only while a second instance exited immediately instead of waiting.
        Step("release single-instance mutex", () => _singleInstance?.Dispose());

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
             EdgeButtonBarViewModel ActionMenuModBarVm,
             TrackStatusViewModel TrackStatusVm,
             TaskSwitcherViewModel TaskSwitcherVm,
             QuickActionsGridViewModel QuickActionsGridVm,
             ModesMenuViewModel ModesMenuVm,
             RepeatCursorViewModel RepeatCursorVm) ResolveViewModels(EyeCtrlSettings settings)
    {
        var vms = AppBootstrapper.ResolveViewModels(_serviceProvider, settings);
        var backdropVm = _serviceProvider.GetRequiredService<GlassBackdropViewModel>();
        return (vms.CursorCircle, vms.ActivationButton, vms.ManualSelectionOverlay, vms.ActionMenu,
                vms.Toolbar, vms.Keyboard, vms.ManualSelectionIndicator, backdropVm,
                vms.Calibration, vms.TargetHighlight, vms.EdgeButtonBar, vms.ActionMenuModBar, vms.TrackStatus, vms.TaskSwitcher,
                vms.QuickActionsGrid, vms.ModesMenu, vms.RepeatCursor);
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
    /// while migrating newly-introduced defaults, applies the <c>--ui-language</c> flag,
    /// initializes Serilog, and registers the global (AppDomain + dispatcher) exception handlers.
    /// </summary>
    /// <param name="args">The process arguments; <c>--ui-language</c> is read from them.</param>
    private (EyeCtrlSettings Settings, string SettingsFilePath) InitializeSettingsAndLogging(string[] args)
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
        settings.ActionMenu.EnsureKeyboardActionOnce();
        // Same once-only pattern: a persisted old-default toolbar size (80) would override the
        // new 120 (mis-dwell fix) forever without this bump.
        settings.Toolbar.MigrateSizeOnce();
        // Same once-only pattern: any persisted radius (the user's file holds 90, from a paused session)
        // would override the ticket-3 default of 75 forever without this bump. Runs after the settings
        // file has been validated, so the 10–200 guard is untouched and legal legacy values load first.
        settings.Dwell.MigrateFixationRadiusOnce();

        var logsDir = Path.Combine(_appDataDir, "logs");
        LoggingInitializer.Initialize(logsDir);

        // ── --ui-language ──
        // A persisting setter: the choice is saved through this method's App writer, then the
        // string table below loads from the mutated settings, so the launch the flag arrives on
        // already shows the requested language. Deliberately HERE, inside the method OnStartup
        // calls only after the single-instance guard: a duplicate that loses the mutex exits
        // before this point and therefore never writes (T04). Logging must already exist, or an
        // ignored value's warning would be discarded. Never throws; a failed save is a log line.
        if (UiLanguageArgument.Apply(
                args, settings, () => tempSettingsService.Save(settings)) is { } uiLanguageOutcome)
        {
            Log.Information("--ui-language: {Outcome}", uiLanguageOutcome);
        }

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            ReleaseHeldDragButtonForFatalExit();
            Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception (terminating={IsTerminating})",
                args.IsTerminating);
            Log.CloseAndFlush();
        };

        // Crash policy, two regimes:
        // - Supervision enabled AND a Supervisor is actually present (recently DELIVERED
        //   heartbeats, ticket 07): a UI-thread fault deliberately kills the process so the
        //   supervisor's respawn owns recovery, rather than the app limping on in a corrupt state.
        // - Standalone (supervision off, or enabled with nobody answering after a reboot/Supervisor
        //   exit): nothing respawns this process. A crash strands a gaze-only user until a caregiver
        //   arrives, so the policy is log-and-continue under a budget (RollingFaultBudget: 10
        //   faults / 60s) — a hard-looping UI, and dying (relaunchable) beats an app that wedges
        //   input invisibly. The presence predicate is built by the composition (F1) and assigned
        //   there; until then the field's safe default (false) keeps the standalone budget. This
        //   handler only calls it.
        // In BOTH death cases the held drag button is released first — a SendInput button-down
        // outlives the process.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Unhandled dispatcher exception");
            if (!DispatcherCrashPolicy.ShouldKeepRunning(
                    settings.Supervision.Enabled, _isSupervisorPresent(), _dispatcherFaultBudget))
            {
                ReleaseHeldDragButtonForFatalExit();
                return; // deliberately unhandled: the process dies
            }
            args.Handled = true;
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
