namespace EyeCtrl.Supervisor;

using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.Supervisor.Fallback;
using EyeCtrl.Supervisor.Ipc;
using EyeCtrl.Supervisor.Supervision;
using EyeCtrl.Supervisor.UI;
using Serilog;

internal static class Program
{
    /// <summary>Single-instance guard name. Two supervisor processes would both open the heartbeat
    /// pipe and both try to launch/adopt the app — exactly the double-launch race this prevents.</summary>
    private const string SupervisorMutexName = @"Local\EyeCtrl.Supervisor.Singleton";

    [STAThread]
    private static int Main(string[] args)
    {
        // C4: only one supervisor per session. A stray double-launch (or the Scheduled Task firing
        // while one is already running) exits fast here. AbandonedMutexException (a prior holder
        // was force-killed) is treated as acquired — same pattern as EyeCtrl.App's SingleInstanceGuard.
        using var guard = new SingleInstanceGuard(SupervisorMutexName);
        if (!guard.TryAcquire())
        {
            System.Console.Error.WriteLine("Another EyeCtrl supervisor is already running. Exiting.");
            return 0;
        }

        var app = new SupervisorApp();
        app.Initialize();
        return app.Run();
    }
}

/// <summary>
/// The supervisor's WPF <see cref="Application"/>: a thin integration shell that wires the
/// state machine (<see cref="SupervisionStateMachine"/>), process controller
/// (<see cref="ProcessController"/>), heartbeat pipe server (<see cref="HeartbeatPipeServer"/>),
/// freshness evaluator (<see cref="HeartbeatFreshnessEvaluator"/>), fallback-AT launcher
/// (<see cref="FallbackAtLauncher"/>), tray icon and recovery dialog into a running watchdog.
/// </summary>
/// <remarks>
/// <para><b>This class is integration wiring, not unit-tested as a whole.</b> Every pure decision
/// it delegates to (freshness, reset, clean-exit gating, identity binding) is already covered by
/// the tests in Tasks 12b/13/16. The supervision loop here is a thin adapter: it feeds inputs
/// (pipe events + a clock tick) into <see cref="SupervisionStateMachine.Handle"/> and dispatches
/// the emitted <see cref="SupervisionStateMachine.Commands"/> to launch / kill / fallback actions.</para>
/// <para><b>Threading model.</b> All state-machine contact is serialized on the UI
/// (<see cref="Dispatcher"/>) thread. The <see cref="HeartbeatPipeServer.HeartbeatReceived"/>
/// event fires on background pipe threads, so the handler marshals back to the dispatcher before
/// touching the state machine or any UI. The <see cref="DispatcherTimer"/> tick is already on the
/// UI thread. A <see cref="Process.Exited"/>-style callback on the bound process is likewise
/// marshaled to the UI thread.</para>
/// </remarks>
internal sealed class SupervisorApp : Application
{
    /// <summary>
    /// Clean-exit grace: once the app signals <c>ShuttingDown</c> the supervisor waits this long
    /// for the process to actually exit before treating it as a hung shutdown (DeadlineElapsed).
    /// See spec "Clean exit" + the Task 20 pseudocode ("if sm.State == CleanExitPending && now -
    /// cleanExitRequestedAt > 5s -> DeadlineElapsed").
    /// </summary>
    private const int CleanExitDeadlineSeconds = 5;

    /// <summary>
    /// Clock-tick interval for the periodic supervision checks (heartbeat staleness, survived-window
    /// reset, clean-exit deadline). ~1s per the Task 20 plan.
    /// </summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    // TODO(installer): the supervisor needs to know where EyeCtrl.App.exe lives to launch it.
    // There is no installer yet (separate workstream — spec "Out-of-Scope: installer authoring").
    // For now: prefer the EYECTRL_APP_EXE env var, else assume the app exe sits next to the
    // supervisor exe in the same install directory. The installer will supply the real path.
    private const string AppExeEnvVar = "EYECTRL_APP_EXE";

    // Dependency-injected supervision state (all constructed in Initialize()).
    private SupervisionStateMachine _sm = null!;
    private ProcessController _pc = null!;
    private HeartbeatFreshnessEvaluator _freshness = null!;
    private SupervisorTrayIcon _tray = null!;
    private SettingsService _settingsService = null!;
    private EyeCtrlSettings _settings = null!;
    private string _appExePath = null!;

    // Pipe server is constructed EAGERLY at startup in adoption mode (exe-path-only binding) — see
    // EnsurePipeStartedAdoption / Initialize. This lets a manually-launched app (supervised +
    // StartOnBoot-off) connect and be adopted before any supervisor launch. EnsurePipeStarted is
    // now a no-op (kept for call-site compatibility). See its remarks for the strict-vs-adoption
    // tradeoff (we use adoption-mode binding for both manual-launch and supervisor-launched apps).
    private HeartbeatPipeServer? _pipe;
    private DateTime? _cleanExitRequestedAt;

    // C2: a deferred recovery relaunch — stamped when LaunchApp command fires, executed by
    // PeriodicCheckPendingRelaunch once NextBackoffDelay elapses. Null when no relaunch pending.
    private DateTime? _pendingLaunchAt;

    // Clock-tick: drives HeartbeatStale / SurvivedWindow / DeadlineElapsed / StartupGrace detection.
    private DispatcherTimer? _tickTimer;

    // Guards against reentrancy if a pipe event fires while a tick is draining on the dispatcher.
    private readonly object _loopLock = new();

    public void Initialize()
    {
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "EyeCtrl");
        var logsDir = Path.Combine(appDataDir, "logs");
        var settingsPath = Path.Combine(appDataDir, "settings.json");

        // 1. Logging — supervisor-.log (distinct from the app's eyectrl-.log).
        SupervisorLoggingInitializer.Initialize(logsDir);
        Log.Information("Supervisor starting up.");

        // 2. Settings — supervisor owns ONLY the Supervision section (SettingsWriter.Supervisor).
        _settingsService = new SettingsService(settingsPath, SettingsWriter.Supervisor);
        _settings = _settingsService.Load();

        // Resolve the app exe path (env override > sibling-of-supervisor-exe default).
        _appExePath = ResolveAppExePath();
        Log.Information("App exe path resolved to {AppExePath}.", _appExePath);

        // 3. Construct the supervision components.
        var sup = _settings.Supervision;
        _sm = new SupervisionStateMachine(
            maxRetries: sup.MaxRetries,
            windowSeconds: sup.CrashWindowSeconds,
            hangThresholdSeconds: sup.HangThresholdSeconds,
            startupGraceSeconds: sup.StartupGraceSeconds,
            windowResetSeconds: sup.CrashWindowSeconds);
        _pc = new ProcessController();
        _freshness = new HeartbeatFreshnessEvaluator(sup.HangThresholdSeconds);

        // 3a. Tray icon — settings/launch/exit callbacks wired to the loop below.
        _tray = new SupervisorTrayIcon(
            openSettings: OpenSettings,
            launchAppNow: () => HandleInput(new LaunchRequest(DateTime.UtcNow)),
            exit: Shutdown);

        // 3b. Periodic clock tick — runs on the UI thread; drives stale/window/deadline checks.
        _tickTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TickInterval };
        _tickTimer.Tick += (_, _) => OnClockTick();
        _tickTimer.Start();

        // 3c. Start the heartbeat pipe EAGERLY in adoption mode (exe-path-only binding). This lets a
        // manually-launched app (supervised + StartOnBoot-off, where the supervisor stays in
        // IdleWatch waiting to adopt) connect and be adopted. When the supervisor later launches the
        // app itself, the same pipe covers that case too — adoption-mode binding (exe-path +
        // same-session + per-user pipe ACL) is sufficient authentication, so EnsurePipeStarted is a
        // no-op (see its remarks for the strict-mode tradeoff). Previously the pipe started only
        // after the first LaunchApp, which meant a manual launch had nothing to connect to.
        EnsurePipeStartedAdoption();

        // 4. Initial behavior at startup. If supervision is enabled AND the user wants the app at
        // logon, the supervisor owns the launch (LaunchRequest -> Launching -> LaunchApp). If
        // supervision is on but StartOnBoot is off, stay in IdleWatch and wait to adopt a
        // manually-launched app via its first heartbeat. If supervision is disabled, the process
        // stays running but idle (spec: "always runs at logon to watch the app even when
        // StartOnBoot is off").
        if (sup.Enabled && _settings.General.StartOnBoot)
        {
            Log.Information("Supervision enabled + StartOnBoot: emitting initial LaunchRequest.");
            HandleInput(new LaunchRequest(DateTime.UtcNow));
        }
        else
        {
            Log.Information("Supervision enabled={Enabled}, StartOnBoot={StartOnBoot}; staying in IdleWatch.",
                sup.Enabled, _settings.General.StartOnBoot);
        }
    }

    /// <summary>The WPF message pump — blocks until <see cref="Application.Shutdown"/> is called.</summary>
    public new int Run() => base.Run();

    // ---- Input handling (all serialized on the UI thread) -------------------------------

    /// <summary>
    /// Single entry point for every supervision input: feeds <paramref name="input"/> into the
    /// state machine and immediately drains the emitted commands. Callers are responsible for
    /// marshaling onto the UI thread first (pipe events arrive on background threads).
    /// </summary>
    private void HandleInput(SupervisionInput input)
    {
        // All access to the state machine + ProcessController + dialog is funneled through here.
        // The lock guards the rare case of a background-delivered pipe event racing a dispatcher
        // tick (both end up on the UI thread, but the lock makes the invariant explicit).
        lock (_loopLock)
        {
            var prevState = _sm.State;
            _sm.Handle(input);
            DrainCommands();

            // Clean-exit bookkeeping: stamp the deadline when the machine enters CleanExitPending.
            if (_sm.State == SupervisorState.CleanExitPending && prevState != SupervisorState.CleanExitPending)
            {
                _cleanExitRequestedAt = DateTime.UtcNow;
            }
            else if (_sm.State != SupervisorState.CleanExitPending)
            {
                _cleanExitRequestedAt = null;
            }

            // SurvivedWindow clock is now driven off the machine's own SupervisingSince (it re-arms
            // on SurvivedWindow input), so no local tracking is needed here.

            // FALLEN_BACK handling: when the machine escalates OR re-escalates (a "Retry once" app
            // crashed again — the FallenBack ProcessExited case re-emits LaunchFallbackAt), run the
            // fallback launcher + dialog. OnFallenBack is idempotent for a re-show.
            if (_sm.State == SupervisorState.FallenBack && _sm.Commands.Contains(SupervisorCommand.LaunchFallbackAt))
            {
                OnFallenBack();
            }

            UpdateTrayStatus();
        }
    }

    /// <summary>
    /// Heartbeat-pipe event handler. Fires on a background pipe thread; marshals to the UI thread
    /// before touching the state machine. Drops unbound heartbeats (binder returned null identity)
    /// per the authenticated-shutdown rule.
    /// </summary>
    private void OnHeartbeatReceived(HeartbeatPayload hb, ClientIdentity? id)
    {
        // Marshal to the UI thread — the state machine is not thread-safe and the dialog/tray are
        // UI objects. BeginInvoke avoids blocking the pipe reader; the work is non-blocking.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (id == null)
            {
                // Unverified client: ignore entirely (must not suppress crash recovery, must not
                // drive the state machine). See spec "Clean exit must come from the verified process."
                return;
            }

            var now = DateTime.UtcNow;

            // 1. Freshness: gaze stream dead (connected but no frames for hangThreshold) -> recover.
            //    A disconnected tracker is a soft signal handled by the app's own reconnect loop
            //    and never trips this (HeartbeatFreshnessEvaluator returns false when !isConnected).
            //    F4: if dead, GoRecovering has fired — STOP. Do not fall through to process this same
            //    payload as a HeartbeatReceived, which would flip Recovering->Supervising (the machine
            //    accepts a first heartbeat in Recovering) and immediately "revive" the app we just
            //    decided to kill. The next genuine heartbeat from the relaunched app is a fresh payload.
            if (_freshness.IsGazeStreamDead(now, hb.LastFrameUtc, hb.IsConnected))
            {
                HandleInput(new GazeStreamDead(now));
                return;
            }

            // 2. Heartbeat. For a manual launch (app started without the supervisor launching it),
            //    the very first verified heartbeat adopts it into Supervising.
            if (_sm.State == SupervisorState.IdleWatch)
            {
                // Adopt the manually-launched app: bind its PID into ProcessController so exit-detection
                // and ForceKill work for it (not just for supervisor-launched apps). id is non-null here
                // (unbound heartbeats were dropped at the top of the handler); id.Pid is uint, cast to
                // int for Process.GetProcessById. WireProcessExited reads _pc.BoundProcess which Adopt
                // just populated.
                if (_pc.Adopt((int)id.Pid))
                {
                    WireProcessExited();
                    Log.Information("Adopted manually-launched app (pid={Pid}, exe={Exe}).", id.Pid, id.ExePath);
                }
                HandleInput(new AdoptRequest(now));
            }
            HandleInput(new HeartbeatReceived(now));

            // 3. Clean exit: the app's final ShuttingDown payload. ClientVerified == (id != null),
            //    which is true here (we dropped unbound above); the flag is threaded through for
            //    the state machine's authenticated-shutdown check.
            if (hb.ShuttingDown)
            {
                HandleInput(new CleanExitRequested(now, ClientVerified: id != null));
            }
        }));
    }

    /// <summary>Periodic clock-tick: startup-grace, stale-heartbeat, survived-window reset, clean-exit deadline.</summary>
    private void OnClockTick()
    {
        // The checks below each call HandleInput (which takes _loopLock), so the tick itself
        // does not need to lock — there is no shared mutable access here beyond reading _sm state,
        // and the tick already runs on the UI thread alongside all other HandleInput callers.
        var now = DateTime.UtcNow;
        PeriodicCheckStartupGrace(now);
        PeriodicCheckStale(now);
        PeriodicCheckSurvivedWindow(now);
        PeriodicCheckCleanExitDeadline(now);
        PeriodicCheckPendingRelaunch(now);
    }

    /// <summary>C1: startup grace — app launched but no heartbeat within StartupGraceSeconds -> recover (spec failure mode #2).</summary>
    private void PeriodicCheckStartupGrace(DateTime now)
    {
        if (_sm.State == SupervisorState.Launching && _sm.LaunchingSince.HasValue &&
            (now - _sm.LaunchingSince.Value).TotalSeconds > _settings.Supervision.StartupGraceSeconds)
        {
            Log.Warning("Startup grace ({Grace}s) elapsed in LAUNCHING with no heartbeat; recovering.", _settings.Supervision.StartupGraceSeconds);
            HandleInput(new StartupGraceElapsed(now));
        }
    }

    /// <summary>C2: apply backoff before a recovery relaunch — defer LaunchApp until NextBackoffDelay elapses.</summary>
    private void PeriodicCheckPendingRelaunch(DateTime now)
    {
        // A recovery relaunch (GoRecovering -> LaunchApp command) is NOT executed immediately in
        // DrainCommands; instead it's deferred here until the backoff delay elapses, so a tight
        // crash loop doesn't hammer the app/process table and the spec's worked-example timing holds.
        // The initial logon launch (LaunchRequest from IdleWatch) has ConsecutiveFailures==0 -> delay 0.
        if (_pendingLaunchAt.HasValue && now >= _pendingLaunchAt.Value)
        {
            _pendingLaunchAt = null;
            DoLaunchApp();
        }
    }

    private void PeriodicCheckStale(DateTime now)
    {
        if (_sm.LastHeartbeatUtc.HasValue &&
            (now - _sm.LastHeartbeatUtc.Value).TotalSeconds > _settings.Supervision.HangThresholdSeconds &&
            _sm.State == SupervisorState.Supervising)
        {
            HandleInput(new HeartbeatStale(now));
        }
    }

    private void PeriodicCheckSurvivedWindow(DateTime now)
    {
        // SurvivedWindow reset is driven entirely by the state machine (it resets the crash
        // window + counter on a SurvivedWindow input, AND re-arms SupervisingSince to input.At so
        // this check doesn't re-fire every tick). We emit the input after the app has been
        // continuously Supervising for longer than the crash window, reading the machine's own
        // SupervisingSince (single source of truth — no duplicated local field).
        if (_sm.State == SupervisorState.Supervising && _sm.SupervisingSince.HasValue &&
            (now - _sm.SupervisingSince.Value).TotalSeconds > _settings.Supervision.CrashWindowSeconds)
        {
            HandleInput(new SurvivedWindow(now));
        }
    }

    private void PeriodicCheckCleanExitDeadline(DateTime now)
    {
        if (_sm.State == SupervisorState.CleanExitPending &&
            _cleanExitRequestedAt.HasValue &&
            (now - _cleanExitRequestedAt.Value).TotalSeconds > CleanExitDeadlineSeconds)
        {
            HandleInput(new DeadlineElapsed(now));
        }
    }

    // ---- Command drain -----------------------------------------------------------------

    /// <summary>Dispatches every command the last Handle() emitted to its side-effecting action.</summary>
    private void DrainCommands()
    {
        foreach (var cmd in _sm.Commands)
        {
            switch (cmd)
            {
                case SupervisorCommand.LaunchApp:
                    // C2: backoff — defer the relaunch until NextBackoffDelay elapses (checked in
                    // PeriodicCheckPendingRelaunch). The initial logon launch has ConsecutiveFailures==0
                    // -> delay 0, so it still happens on the next tick (~immediate). Recovery relaunches
                    // after N failures are spread per the 0/5/15/60s curve (spec worked example).
                    var delay = _sm.NextBackoffDelay;
                    _pendingLaunchAt = DateTime.UtcNow + delay;
                    Log.Information("LaunchApp scheduled in {Delay:F1}s (consecutiveFailures={Failures}).",
                        delay.TotalSeconds, _sm.ConsecutiveFailures);
                    break;
                case SupervisorCommand.ForceKill:
                    Log.Information("ForceKill command: killing supervised process.");
                    _pc.ForceKill();
                    break;
                case SupervisorCommand.LaunchFallbackAt:
                    // Handled by OnFallenBack (driven off the state transition, not the command,
                    // so the dialog is shown exactly once per entry into FallenBack).
                    break;
                case SupervisorCommand.NotifyCleanExit:
                    Log.Information("NotifyCleanExit: app signalled authenticated clean shutdown.");
                    break;
                case SupervisorCommand.None:
                    break;
            }
        }
    }

    /// <summary>Performs the actual app launch (not paused by default) and wires exit detection. Called by PeriodicCheckPendingRelaunch after the backoff delay.</summary>
    private void DoLaunchApp()
    {
        Log.Information("LaunchApp: starting {AppExePath}.", _appExePath);
        _pc.Launch(_appExePath, paused: false);

        // _supervisingSince / _launchingSince are tracked via the state transitions in HandleInput
        // (Launching is stamped when LaunchRequest flips state; Supervising when the first heartbeat arrives).

        // Wire the process-exit callback so an unannounced exit feeds ProcessExited into the loop.
        WireProcessExited();

        // Pipe starts eagerly in adoption mode at supervisor startup (see EnsurePipeStartedAdoption).
        EnsurePipeStarted();
    }

    /// <summary>
    /// No-op now that the pipe starts eagerly in adoption mode (see <see cref="EnsurePipeStartedAdoption"/>
    /// / Initialize). Kept as an idempotent call site for <see cref="LaunchApp"/> and the FallenBack
    /// retry path so they don't need to care which startup mode is active.
    /// </summary>
    /// <remarks>
    /// <para><b>Strict-vs-adoption tradeoff.</b> Previously the pipe was constructed here, after the
    /// first launch, with a strict <see cref="ClientIdentityBinder"/> bound to the launched process's
    /// creation time (PID-reuse protection). Now the pipe starts eagerly at supervisor startup in
    /// adoption mode (exe-path-only binding) so a manually-launched app can connect. Adoption-mode
    /// binding still enforces exe-path + same-session + the per-user pipe ACL, which is strong
    /// authentication; the only thing lost versus strict mode is the creation-time PID-reuse check.
    /// Reconstructing the pipe with a strict binder on launch would drop any in-flight connection,
    /// so the eager adoption-mode pipe deliberately covers BOTH the supervisor-launched and
    /// manual-launch cases. If strict creation-time checking is later required for
    /// supervisor-launched apps, add an UpgradeBinderToStrict that swaps the binder (accepting the
    /// connection-drop cost).</para>
    /// </remarks>
    private void EnsurePipeStarted()
    {
        // Pipe is already running from Initialize (adoption mode). Nothing to do.
    }

    /// <summary>
    /// Constructs and starts the <see cref="HeartbeatPipeServer"/> in ADOPTION mode (exe-path-only
    /// binding via <see cref="ClientIdentityBinder.ForAdoption"/>), so a manually-launched app
    /// (supervised + StartOnBoot-off) can connect and be adopted before any supervisor launch.
    /// Idempotent.
    /// </summary>
    private void EnsurePipeStartedAdoption()
    {
        if (_pipe != null) return;
        var binder = ClientIdentityBinder.ForAdoption(_appExePath);
        _pipe = new HeartbeatPipeServer(binder);
        _pipe.HeartbeatReceived += OnHeartbeatReceived;
        _pipe.Start();
        Log.Information("Heartbeat pipe started in adoption mode (exe-path-only binding for {AppExePath}).", _appExePath);
    }

    /// <summary>Wires the bound process's Exited event so a crash/exit feeds ProcessExited.</summary>
    private void WireProcessExited()
    {
        var proc = _pc.BoundProcess;
        if (proc == null) return;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            // Exited fires on a background thread — marshal to UI before touching the state machine.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // F1: a supervisor-initiated ForceKill already booked a strike via GoRecovering (hang /
                // gaze-death / startup-grace recovery). The Exited event from OUR kill is the expected
                // consequence, NOT an independent crash — feeding ProcessExited here would double-count
                // the incident and halve the crash budget. Suppress it. (KillInitiated clears on relaunch.)
                if (_pc.KillInitiated)
                {
                    Log.Information("Bound process exited after supervisor-initiated kill (pid={Pid}); not counting as a separate crash.", proc.Id);
                    return;
                }
                Log.Information("Bound process exited (pid={Pid}).", proc.Id);
                HandleInput(new ProcessExited(DateTime.UtcNow));
            }));
        };
    }

    // ---- FALLEN_BACK handling ----------------------------------------------------------

    /// <summary>
    /// Runs the fallback-AT launcher (so the user keeps eye control) and shows the non-modal
    /// <see cref="FallenBackDialog"/>. Wired to the state machine's transition into FallenBack.
    /// </summary>
    private void OnFallenBack()
    {
        Log.Warning("State -> FallenBack: launching fallback AT and showing recovery dialog.");

        // Goal #5 "never strand the user": launch a fallback AT immediately.
        var candidates = FallbackAtLauncher.BuildDefaultCandidates();
        var launched = FallbackAtLauncher.LaunchFirstInstalled(candidates);
        Log.Information("Fallback AT launched: {Launched}", launched?.Id ?? "(none installed)");

        // Show the recovery dialog (non-modal + topmost — see FallenBackDialog remarks).
        var dialog = new FallenBackDialog();
        dialog.RetryRequested += () =>
        {
            // Paused-retry handoff: relaunch with --paused so the retried instance is inert
            // (no SendInput) until the user is ready, without mutating the persisted setting.
            // The machine stays in FallenBack (this is a one-shot manual reprieve, not a state
            // transition); the dialog is closed and the user interacts with the relaunched app.
            Log.Information("FallenBack dialog Retry: relaunching app with --paused.");
            dialog.Close();
            _pc.Launch(_appExePath, paused: true);
            WireProcessExited();
            EnsurePipeStarted();
        };
        dialog.Dismissed += () =>
        {
            Log.Information("FallenBack dialog Dismissed: staying idle with fallback AT.");
        };
        dialog.Show();
    }

    // ---- Tray + setup window -----------------------------------------------------------

    private void OpenSettings()
    {
        var window = new SupervisorSetupWindow(
            settings: _settings,
            settingsService: _settingsService,
            saveChanges: _ =>
            {
                // The setup window persists the Supervision section itself (SettingsWriter.Supervisor).
                // React here to changes that affect the running loop — none require a live restart
                // today (MaxRetries/HangThreshold are read each tick; Enabled toggles take effect on
                // the next transition). StartOnBoot lives in GeneralSettings (App-owned) and is NOT
                // persisted by the supervisor.
                Log.Information("Supervisor settings saved; refreshing loop in-memory state.");
            },
            launchAppNow: () => HandleInput(new LaunchRequest(DateTime.UtcNow)),
            statusText: $"Supervisor state: {_sm.State}");
        window.Show();
    }

    private void UpdateTrayStatus()
    {
        var text = _sm.State switch
        {
            SupervisorState.IdleWatch => "EyeCtrl Supervisor: idle",
            SupervisorState.Launching => "EyeCtrl Supervisor: launching",
            SupervisorState.Supervising => "EyeCtrl Supervisor: watching",
            SupervisorState.Recovering => "EyeCtrl Supervisor: recovering",
            SupervisorState.CleanExitPending => "EyeCtrl Supervisor: clean exit pending",
            SupervisorState.FallenBack => "EyeCtrl Supervisor: FALLEN BACK (fallback AT running)",
            _ => "EyeCtrl Supervisor"
        };
        _tray.SetStatusText(text);
    }

    // ---- App-exe path resolution -------------------------------------------------------

    /// <summary>
    /// Resolves the EyeCtrl.App.exe path. Env var <see cref="AppExeEnvVar"/> wins; otherwise assume
    /// the app exe sits next to the supervisor exe in the same install directory. TODO(installer):
    /// the installer will supply the canonical path.
    /// </summary>
    private static string ResolveAppExePath()
    {
        var env = Environment.GetEnvironmentVariable(AppExeEnvVar);
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return Path.GetFullPath(env);
        }

        // Default: sibling of the supervisor exe — EyeCtrl.App.exe next to EyeCtrl.Supervisor.exe.
        var supervisorDir = AppContext.BaseDirectory;
        return Path.GetFullPath(Path.Combine(supervisorDir, "EyeCtrl.App.exe"));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Supervisor exiting.");
        _tickTimer?.Stop();
        _pipe?.Dispose();
        _tray?.Dispose();
        base.OnExit(e);
    }
}
