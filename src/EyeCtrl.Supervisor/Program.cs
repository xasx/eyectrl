namespace EyeCtrl.Supervisor;

using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using EyeCtrl.Core.Diagnostics;
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
/// the tests in Tasks 12b/13/16, and the clock-tick thresholds and their order by
/// <see cref="SupervisionClock"/>'s table tests. The supervision loop here is a thin adapter: it feeds inputs
/// (pipe events + a clock tick) into <see cref="SupervisionStateMachine.Handle"/> and dispatches
/// the emitted <see cref="SupervisionStateMachine.Commands"/> to launch / kill / fallback actions.</para>
/// <para><b>Threading model.</b> All state-machine contact is serialized on the UI
/// (<see cref="Dispatcher"/>) thread. The <see cref="HeartbeatPipeServer.HeartbeatReceived"/>
/// event fires on background pipe threads, so the handler marshals back to the dispatcher before
/// touching the state machine or any UI. The <see cref="DispatcherTimer"/> tick is already on the
/// UI thread. A <see cref="Process.Exited"/>-style callback on the bound process is likewise
/// marshaled to the UI thread.</para>
/// </remarks>
internal sealed class SupervisorApp : Application, ISupervisionClockHost
{
    /// <summary>
    /// Clock-tick interval for the periodic supervision checks (heartbeat staleness, survived-window
    /// reset, clean-exit deadline). ~1s per the Task 20 plan.
    /// </summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    // TODO(installer): the supervisor needs to know where EyeCtrl.App.exe lives to launch it.
    // There is no installer yet (separate workstream — spec "Out-of-Scope: installer authoring").
    // For now: prefer the EYECTRL_APP_EXE env var, else assume the app exe sits next to the
    // supervisor exe in the same install directory. The installer will supply the real path.
    // Dependency-injected supervision state (all constructed in Initialize()).
    private SupervisionStateMachine _sm = null!;
    private ProcessController _pc = null!;
    private HeartbeatFreshnessEvaluator _freshness = null!;
    private SupervisorTrayIcon _tray = null!;
    private SettingsService _settingsService = null!;
    private EyeCtrlSettings _settings = null!;
    private string _appExePath = null!;

    // Ticket 06-B: last disk-read supervision.enabled, kept by the tracker so a damaged file falls
    // back to the last DISK value — never to the in-memory settings object the setup window edits.
    private SupervisionEnabledTracker _enabledTracker = null!;

    // Pipe server is constructed EAGERLY at startup in adoption mode (exe-path-only binding) — see
    // EnsurePipeStartedAdoption / Initialize. This lets a manually-launched app (supervised +
    // StartOnBoot-off) connect and be adopted before any supervisor launch. EnsurePipeStarted is
    // now a no-op (kept for call-site compatibility). See its remarks for the strict-vs-adoption
    // tradeoff (we use adoption-mode binding for both manual-launch and supervisor-launched apps).
    private HeartbeatPipeServer? _pipe;
    private DateTime? _cleanExitRequestedAt;

    // C2: a deferred recovery relaunch — stamped when LaunchApp command fires, executed by
    // the clock tick (SupervisionClock.RelaunchDue) once NextBackoffDelay elapses. Null when no relaunch pending.
    private DateTime? _pendingLaunchAt;

    // True once a fallback AT has been launched this session — guards against re-launching it (or
    // re-opening the Eye Control Settings page) on a FALLEN_BACK re-escalation. Reset never happens:
    // FALLEN_BACK is terminal for the session; next logon is a clean slate.
    private bool _fallbackAtLaunched;

    // Clock-tick: drives HeartbeatStale / SurvivedWindow / DeadlineElapsed / StartupGrace detection.
    private DispatcherTimer? _tickTimer;

    // Guards against reentrancy if a pipe event fires while a tick is draining on the dispatcher.
    private readonly object _loopLock = new();

    // Dispatcher-fault budget for the containment handlers registered in Initialize. Touched
    // only on the UI thread, so no synchronization.
    private readonly RollingFaultBudget _dispatcherFaultBudget = new(10, TimeSpan.FromSeconds(60));

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

        // 1b. Fault containment. The supervisor is the WATCHDOG: nothing watches it, and dying
        // silently means the app runs unwatched from that moment — a later app crash is
        // unrecovered. The supervisor previously registered no handlers at all: any dispatcher
        // fault (a throwing tick, a pipe event, a tray/menu handler) killed it outright.
        // Dispatcher faults now log-and-continue under the same budget the app uses — the
        // state machine re-derives recovery on the next tick, and a hard-looping supervisor
        // (budget exceeded) still dies rather than wedging invisibly. Terminating faults are
        // flushed for post-mortem; unobserved task faults are observed.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Unhandled supervisor dispatcher exception");
            // The watchdog itself has no watcher: supervisorPresent is always false, so the
            // standalone budget applies exactly as before the ticket 07 signature change.
            if (DispatcherCrashPolicy.ShouldKeepRunning(
                    supervisionEnabled: false, supervisorPresent: false, _dispatcherFaultBudget))
                args.Handled = true; // the watchdog stays up
            else
                Log.Fatal("Supervisor dispatcher fault budget exhausted; giving up (hard loop).");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Fatal(args.ExceptionObject as Exception,
                "Unhandled supervisor exception (terminating={IsTerminating})", args.IsTerminating);
            Log.CloseAndFlush();
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved supervisor task exception (observed set to prevent crash)");
            args.SetObserved();
        };

        // 2. Settings — supervisor owns ONLY the Supervision section (SettingsWriter.Supervisor).
        _settingsService = new SettingsService(settingsPath, SettingsWriter.Supervisor);
        _settings = _settingsService.Load();

        // Resolve the app exe path (env override > sibling-of-supervisor-exe default).
        _appExePath = AppExePathResolver.Resolve();
        Log.Information("App exe path resolved to {AppExePath}.", _appExePath);

        // 3. Construct the supervision components. Ticket 04 re-check 2 R2-1: the enabled predicate
        // (tracker-backed: disk, never the in-memory snapshot) and the startup decision live in the
        // composition factory, pinned by SupervisorCompositionTests — this class is integration
        // wiring that cannot be composed in a test.
        var sup = _settings.Supervision;
        var composition = SupervisorComposition.Create(_settings, settingsPath);
        _enabledTracker = composition.EnabledTracker;
        _sm = composition.Machine;
        // hangThresholdSeconds / startupGraceSeconds are read directly from settings in the clock-tick
        // checks (SupervisionClock.HeartbeatStale / StartupGraceElapsed, via SupervisionClock.Tick).
        _pc = new ProcessController();
        // The Exited subscription lives on ProcessController so a stand-down can detach it
        // (ticket 06-B); this handler marshals to the UI thread and applies the F1 suppression.
        _pc.BoundProcessExited += OnBoundProcessExited;
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
        // logon AND no EyeCtrl is already running, the supervisor owns the launch (LaunchRequest ->
        // Launching -> LaunchApp). If the app is already running (the row or Restart → "with
        // supervisor" starts the supervisor while EyeCtrl is up), stay in IdleWatch and adopt it on
        // its first heartbeat: launching a duplicate would die on the app's single-instance mutex
        // and every such exit would book a strike (04-review F1 — 4 strikes reach TD Control over a
        // healthy app). If supervision is off, the process stays running but idle (spec: "always
        // runs at logon to watch the app even when StartOnBoot is off").
        if (composition.ShouldEmitStartupLaunch)
        {
            Log.Information("Supervision enabled + StartOnBoot + no EyeCtrl running: emitting initial LaunchRequest.");
            HandleInput(new LaunchRequest(DateTime.UtcNow));
        }
        else
        {
            Log.Information(
                "Startup launch skipped (enabled={Enabled}, StartOnBoot={StartOnBoot}, appAlreadyRunning={AppAlreadyRunning}); staying in IdleWatch.",
                sup.Enabled, _settings.General.StartOnBoot, composition.AppAlreadyRunning);
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
            DrainCommands(prevState);

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
                // Ticket 06-B/F2: the enabled gate lives in the machine (AdoptRequest is accepted
                // only in IdleWatch while enabled), so a disabled supervisor never adopts. The app
                // may still be publishing, so heartbeats keep arriving; a later re-enable adopts on
                // the next one. No logging per heartbeat — only the transition.
                HandleInput(new AdoptRequest(now));
                if (_sm.State != SupervisorState.Supervising) return;   // refused: supervision is off

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
            }
            HandleInput(new HeartbeatReceived(now));

            // 3. Clean exit: the app's final ShuttingDown payload. ClientVerified == (id != null),
            //    which is true here (we dropped unbound above); the flag is threaded through for
            //    the state machine's authenticated-shutdown check.
            //    RestartRequested rides along: a user-requested restart is a clean exit that is
            //    followed by a launch. It is only honoured for a verified client, by the same
            //    rule and in the same place as the shutdown signal itself.
            if (hb.ShuttingDown)
            {
                HandleInput(new CleanExitRequested(
                    now,
                    ClientVerified: id != null,
                    RestartRequested: hb.RestartRequested));
            }
        }));
    }

    /// <summary>
    /// Periodic clock tick: startup grace, stale heartbeat, survived-window reset, clean-exit
    /// deadline, then the deferred relaunch — the decisions and their order live in the pure,
    /// table-tested <see cref="SupervisionClock"/>. The checks each call HandleInput (which takes
    /// _loopLock), so the tick itself does not need to lock, and it already runs on the UI thread
    /// alongside all other HandleInput callers.
    /// </summary>
    private void OnClockTick() => SupervisionClock.Tick(DateTime.UtcNow, this);

    SupervisionStateMachine ISupervisionClockHost.Machine => _sm;
    SupervisionSettings ISupervisionClockHost.Settings => _settings.Supervision;
    DateTime? ISupervisionClockHost.CleanExitRequestedAt => _cleanExitRequestedAt;
    DateTime? ISupervisionClockHost.PendingLaunchAt => _pendingLaunchAt;
    bool ISupervisionClockHost.SupervisionEnabled => _enabledTracker.Current;
    void ISupervisionClockHost.Handle(SupervisionInput input) => HandleInput(input);

    void ISupervisionClockHost.ClearPendingLaunch() => _pendingLaunchAt = null;

    bool ISupervisionClockHost.TryLaunchPendingNow()
    {
        // This runs on the dispatcher via the clock tick, not inside HandleInput's lock. An
        // unhandled exception here (e.g. Process.Start throwing because the app exe is missing —
        // the installer-less default) would surface as a DispatcherUnhandledException and tear
        // down the supervisor itself. Guard it: false makes Tick feed LaunchFailed, which books a
        // strike and retries with backoff instead of leaving the machine waiting for a startup
        // grace that can never fire (ticket 06-C/F2). The wire-time race guard runs after Tick
        // feeds the outcome (WireLaunchedProcess, ticket 06 review F4).
        try
        {
            Log.Information("LaunchApp: starting {AppExePath}.", _appExePath);
            _pc.Launch(_appExePath, paused: false);
            EnsurePipeStarted(); // adoption-mode pipe starts eagerly at startup; this is an idempotent no-op.
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "LaunchApp failed (app exe missing or Process.Start threw); booking a strike and retrying with backoff.");
            return false;
        }
    }

    void ISupervisionClockHost.WireLaunchedProcess() => WireProcessExited();

    // ---- Command drain -----------------------------------------------------------------

    /// <summary>Dispatches every command the last Handle() emitted to its side-effecting action.</summary>
    /// <param name="prevState">
    /// The state the machine was in before this input. Paired with the machine's current
    /// state it identifies the emitting transition: only CleanExitPending → Launching is a
    /// user-requested restart and skips backoff — see <see cref="RelaunchDelayPolicy"/>.
    /// </param>
    private void DrainCommands(SupervisorState prevState)
    {
        foreach (var cmd in _sm.Commands)
        {
            switch (cmd)
            {
                case SupervisorCommand.LaunchApp:
                    // C2: backoff — defer the relaunch until NextBackoffDelay elapses (checked in
                    // SupervisionClock.Tick). The initial logon launch has ConsecutiveFailures==0
                    // -> delay 0, so it still happens on the next tick (~immediate). Recovery relaunches
                    // after N failures are spread per the 0/5/15/60s curve (spec worked example).
                    // The one-tick wait is load-bearing, not scheduling slack: a tick that issued
                    // ForceKill must not launch in the same tick, because Launch clears KillInitiated
                    // before the killed process's queued Exited callback runs, and that callback would
                    // then book a second strike for one incident (the F1 double count). The timer runs
                    // at Background priority and the callback is marshalled at Normal, so Exited is
                    // handled first, while KillInitiated is still set.
                    // _sm.State is already the POST-transition state here: HandleInput calls
                    // DrainCommands after _sm.Handle(input). The pair is what distinguishes a
                    // restart (CleanExitPending -> Launching, no strike) from the hung-shutdown
                    // force-kill (CleanExitPending -> Recovering/FallenBack, which books one and
                    // must keep its brake).
                    var delay = RelaunchDelayPolicy.For(prevState, _sm.State, _sm.NextBackoffDelay);
                    _pendingLaunchAt = DateTime.UtcNow + delay;
                    Log.Information("LaunchApp scheduled in {Delay:F1}s (consecutiveFailures={Failures}).",
                        delay.TotalSeconds, _sm.ConsecutiveFailures);
                    break;
                case SupervisorCommand.ForceKill:
                    Log.Information("ForceKill command: killing supervised process.");
                    _pc.ForceKill();
                    break;
                case SupervisorCommand.StandDown:
                    // Ticket 06-B: supervision is off on disk. Release the process WITHOUT killing
                    // it, drop any deferred relaunch, and stay idle. The machine has already gone to
                    // IdleWatch; no strike was booked.
                    Log.Warning("Supervision disabled on disk; standing down without killing or relaunching.");
                    _pendingLaunchAt = null;
                    _pc.Unbind();
                    break;
                case SupervisorCommand.LaunchFallbackAt:
                    // Handled by OnFallenBack (driven off the state transition, not the command,
                    // so the dialog is shown exactly once per entry into FallenBack).
                    break;
                case SupervisorCommand.StopListening:
                    // Ticket 07 review F2: a FallenBack Supervisor has given up. Closing the pipe
                    // makes the app's deliveries fail, so its presence gate stays standalone and
                    // its row says "not answering" instead of promising a respawn that will not
                    // come. StartListening reopens it on the dialog's Retry.
                    Log.Information("StopListening: closing the heartbeat pipe (FallenBack).");
                    _pipe?.Dispose();
                    _pipe = null;
                    break;
                case SupervisorCommand.StartListening:
                    Log.Information("StartListening: reopening the heartbeat pipe (retry).");
                    EnsurePipeStartedAdoption();
                    break;
                case SupervisorCommand.NotifyCleanExit:
                    Log.Information("NotifyCleanExit: app signalled authenticated clean shutdown.");
                    break;
                case SupervisorCommand.None:
                    break;
            }
        }
    }

    /// <summary>
    /// No-op now that the pipe starts eagerly in adoption mode (see <see cref="EnsurePipeStartedAdoption"/>
    /// / Initialize). Kept as an idempotent call site for <see cref="ISupervisionClockHost.TryLaunchPendingNow"/>
    /// and the FallenBack retry path so they don't need to care which startup mode is active.
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

    /// <summary>
    /// Runs the wire-time race guard for the currently bound process. The Exited subscription
    /// itself lives on <see cref="ProcessController.BoundProcessExited"/> (wired once in
    /// Initialize, detached on unbind), so a stand-down silences a later exit of the released
    /// process (ticket 06-B).
    /// </summary>
    private void WireProcessExited()
    {
        // Race guard: if the process already exited between Adopt()/Launch() and the subscription,
        // the Exited event may already have fired (or never will). Check now and feed
        // ProcessExited immediately.
        var proc = _pc.BoundProcess;
        if (proc == null) return;
        if (proc.HasExited)
        {
            Log.Information("Bound process already exited at wire time (pid={Pid}); feeding ProcessExited.", proc.Id);
            HandleInput(new ProcessExited(DateTime.UtcNow));
        }
    }

    /// <summary><see cref="ProcessController"/>'s exit callback: marshals to the UI thread and
    /// applies the F1 suppression (a supervisor-initiated kill is not an independent crash).</summary>
    private void OnBoundProcessExited(IProcessHandle proc)
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
    }

    // ---- FALLEN_BACK handling ----------------------------------------------------------

    /// <summary>
    /// Runs the fallback-AT launcher (so the user keeps eye control) and shows the non-modal
    /// <see cref="FallenBackDialog"/>. Wired to the state machine's transition into FallenBack.
    /// </summary>
    private void OnFallenBack()
    {
        Log.Warning("State -> FallenBack: launching fallback AT and showing recovery dialog.");

        // Goal #5 "never strand the user": launch a fallback AT — but only once a launch has
        // actually SUCCEEDED. A re-escalation (a "Retry once" app that crashed again) must not
        // spawn a second AT instance or re-open the Settings page while the one from the first
        // escalation is still running. But a session where nothing started is not that case:
        // the latch stays false, so the next escalation tries again rather than treating a
        // failure as a completed handover.
        // Order honors the user's configured SupervisionSettings.FallbackAtOrder (not a hard-coded list).
        if (!_fallbackAtLaunched)
        {
            var candidates = FallbackAtLauncher.BuildCandidates(_settings.Supervision.FallbackAtOrder);
            var launched = FallbackAtLauncher.LaunchFirstInstalled(candidates);
            _fallbackAtLaunched = launched != null;

            if (launched != null)
            {
                // Truthful by construction now: LaunchFirstInstalled returns a candidate only
                // when its launch reported a started process. It previously returned the first
                // candidate whose install PROBE passed, so this line asserted a launch that a
                // silent no-op had never performed — and post-incident diagnosis then began
                // from a false statement, which is worse than no log at all.
                Log.Information("Fallback AT launched: {Launched}", launched.Id);
            }
            else
            {
                // Deliberately not "(none installed)": a null return now means either nothing
                // was installed OR every installed candidate failed to start, and this branch
                // cannot tell them apart. LaunchFirstInstalled logs a per-candidate warning for
                // the second case, so the distinction is recoverable from the log without
                // asserting a cause here.
                //
                // Error, not Information: reaching this line means the app has crashed past
                // MaxRetries AND no assistive technology is running, so a user whose only input
                // channel was EyeCtrl now has none. That is the failure of the one guarantee
                // this whole path exists to provide.
                Log.Error(
                    "No fallback AT could be started — the user may have no input channel. " +
                    "Either none of the configured candidates ({Order}) is installed, or every " +
                    "installed one failed to launch; see the per-candidate warnings above. " +
                    "The next escalation will retry.",
                    string.Join(", ", _settings.Supervision.FallbackAtOrder));
            }
        }
        else
        {
            Log.Information("Fallback AT already launched this session; re-showing recovery dialog only.");
        }

        // Show the recovery dialog (non-modal + topmost — see FallenBackDialog remarks).
        var dialog = new FallenBackDialog();
        dialog.RetryRequested += () =>
        {
            // Paused-retry handoff: relaunch with --paused so the retried instance is inert (no
            // SendInput) until the user is ready, without mutating the persisted setting. Transition
            // the machine to IdleWatch so the retried app's first heartbeat can ADOPT it back into
            // Supervising (otherwise it stays FallenBack, which has no HeartbeatReceived case, so
            // the retried app would be unsupervised — no crash recovery, no clean-exit detection).
            Log.Information("FallenBack dialog Retry: relaunching app with --paused and returning to IdleWatch.");
            try
            {
                _pc.Launch(_appExePath, paused: true);
                WireProcessExited();
                EnsurePipeStarted();
            }
            catch (Exception ex)
            {
                // The same Process.Start failure TryLaunchPendingNow guards against (app exe missing) applies
                // here — at the user's most vulnerable moment. The dialog STAYS OPEN so the user can
                // retry after the cause is fixed, instead of the exception killing the supervisor.
                Log.Error(ex, "FallenBack retry: launch failed; dialog stays open, machine stays in FallenBack.");
                return;
            }
            dialog.Close();
            // Re-arm IdleWatch so the next verified heartbeat adopts the retried instance. Feeding an
            // explicit input (rather than poking State) keeps the transition through the machine's API.
            HandleInput(new FallenBackRetry(DateTime.UtcNow));
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
                // Apply the live-editable policy immediately so an edit takes effect without a restart:
                //  - MaxRetries / CrashWindowSeconds are baked into the state machine's CrashWindow at
                //    construction, so push them in via UpdatePolicy (previously ignored until restart);
                //  - HangThresholdSeconds is baked into the freshness evaluator, so rebuild it (the
                //    stale-heartbeat check already reads it live each tick).
                // Enabled also takes effect live (ticket 06-B): the machine re-reads it at every
                // recovery decision, adoption re-checks it, and a due Recovering relaunch re-checks it.
                // StartOnBoot lives in GeneralSettings (App-owned) and is NOT persisted by the supervisor.
                var s = _settings.Supervision;
                _sm.UpdatePolicy(s.MaxRetries, s.CrashWindowSeconds);
                _freshness = new HeartbeatFreshnessEvaluator(s.HangThresholdSeconds);
                Log.Information(
                    "Supervisor settings saved; applied MaxRetries={Max}, CrashWindow={Win}s, HangThreshold={Hang}s.",
                    s.MaxRetries, s.CrashWindowSeconds, s.HangThresholdSeconds);
            },
            launchAppNow: () => HandleInput(new LaunchRequest(DateTime.UtcNow)),
            statusText: "Supervisor state: " + SupervisorStateText.Describe(_sm.State));
        window.Show();
    }

    private void UpdateTrayStatus()
    {
        var text = SupervisorStateText.TrayPrefix + SupervisorStateText.Describe(_sm.State);
        _tray.SetStatusText(text);
    }

    // ---- App-exe path resolution (see AppExePathResolver) ------------------------------

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Supervisor exiting.");
        _tickTimer?.Stop();
        _pipe?.Dispose();
        _tray?.Dispose();
        base.OnExit(e);
    }
}
