namespace EyeCtrl.Supervisor.Supervision;

using System;
using System.Collections.Generic;

public sealed class SupervisionStateMachine
{
    private readonly CrashWindow _crash;
    private readonly BackoffPolicy _backoff = new();
    private readonly List<SupervisorCommand> _commands = new();
    private DateTime? _supervisingSince;
    private DateTime? _launchingSince;

    public SupervisorState State { get; private set; } = SupervisorState.IdleWatch; // spec entry state
    public IReadOnlyList<SupervisorCommand> Commands => _commands;
    public DateTime? LastHeartbeatUtc { get; private set; }
    public int ConsecutiveFailures { get; private set; }
    public TimeSpan NextBackoffDelay => _backoff.DelayFor(ConsecutiveFailures);
    /// <summary>When the current Supervising stretch began (drives the SurvivedWindow clock). Null when not Supervising.</summary>
    public DateTime? SupervisingSince => _supervisingSince;
    /// <summary>When the current Launching stretch began (drives the startup-grace clock). Null when not Launching.</summary>
    public DateTime? LaunchingSince => _launchingSince;

    /// <param name="maxRetries">Max restart attempts within the crash window (escalate on the next).</param>
    /// <param name="windowSeconds">The sliding crash-window length (seconds) over which strikes are counted.</param>
    /// <remarks>
    /// hangThresholdSeconds, startupGraceSeconds, and the windowReset interval are NOT machine state — the
    /// orchestrator (Program.cs) enforces them directly via its clock tick (PeriodicCheckStale /
    /// PeriodicCheckStartupGrace / PeriodicCheckSurvivedWindow) reading SettingsService. They were removed
    /// from this constructor to avoid implying the machine enforces them.
    /// </remarks>
    public SupervisionStateMachine(int maxRetries, int windowSeconds)
    {
        _crash = new CrashWindow(maxRetries, windowSeconds);
    }

    /// <summary>
    /// Applies a live change to the crash-window policy (e.g. after the user edits MaxRetries /
    /// CrashWindowSeconds in the supervisor setup window). Does not reset accumulated strikes — only
    /// the escalation limits going forward. Called on the same (UI) thread as <see cref="Handle"/>.
    /// </summary>
    public void UpdatePolicy(int maxRetries, int windowSeconds) => _crash.UpdateLimits(maxRetries, windowSeconds);

    public void Handle(SupervisionInput input)
    {
        _commands.Clear();
        switch (input)
        {
            case LaunchRequest when State == SupervisorState.IdleWatch:
                State = SupervisorState.Launching;
                _launchingSince = DateTime.UtcNow;
                _commands.Add(SupervisorCommand.LaunchApp);
                break;

            case AdoptRequest when State == SupervisorState.IdleWatch:
                State = SupervisorState.Supervising;
                _supervisingSince = input.At;
                break;

            // NOTE: isPaused in the heartbeat is intentionally NOT consulted by this state machine.
            // Pause gates the app's SendInput (cursor movement), not its gaze stream — a paused app
            // still delivers frames and heartbeats. Liveness is driven by HeartbeatFreshnessEvaluator
            // (on lastFrameUtc), not by isPaused. See spec "Paused-retry handoff" + "Gaze-stream-death".

            // First heartbeat after a launch (Launching) OR after a recovery relaunch
            // (Recovering — GoRecovering already commanded LaunchApp; the relaunched
            // process's first heartbeat means it is alive -> Supervising).
            case HeartbeatReceived when State is SupervisorState.Launching or SupervisorState.Recovering:
                LastHeartbeatUtc = input.At;
                State = SupervisorState.Supervising;
                _supervisingSince = input.At;
                break;

            case HeartbeatReceived when State is SupervisorState.Supervising or SupervisorState.CleanExitPending:
                LastHeartbeatUtc = input.At;
                break;

            case SurvivedWindow when State == SupervisorState.Supervising:
                // Spec reset rule: surviving a full crash window resets BOTH counters.
                _crash.Reset();
                ConsecutiveFailures = 0;
                _supervisingSince = input.At;
                break;

            case HeartbeatStale when State == SupervisorState.Supervising:
            case GazeStreamDead when State == SupervisorState.Supervising:
                GoRecovering(input.At, forceKill: true);
                break;

            case CleanExitRequested ce when State == SupervisorState.Supervising:
                // Spec: the shuttingDown signal "must come from the verified supervised
                // process, not an untrusted sender." An unverified request is IGNORED —
                // it must not suppress crash recovery.
                if (ce.ClientVerified)
                {
                    State = SupervisorState.CleanExitPending;
                    _commands.Add(SupervisorCommand.NotifyCleanExit);
                }
                break;

            case ProcessExited when State == SupervisorState.CleanExitPending:
                State = SupervisorState.IdleWatch; // authenticated clean exit within deadline
                _supervisingSince = null;
                break;

            case ProcessExited when State is SupervisorState.Supervising or SupervisorState.Recovering or SupervisorState.Launching:
                GoRecovering(input.At, forceKill: false); // unannounced exit / startup crash
                break;

            case StartupGraceElapsed when State == SupervisorState.Launching:
                // Spec failure mode #2: app launched but never sent a heartbeat within the 60s
                // startup grace (e.g. UI thread deadlocked during DI/Tobii init, process still
                // alive). Force-kill + recover. Counts as one strike.
                GoRecovering(input.At, forceKill: true);
                break;

            case ProcessExited when State == SupervisorState.FallenBack:
                // "Retry EyeCtrl (once)" re-showed the dialog but the retried app crashed again.
                // Spec line 350: "If it crashes again, the dialog reappears." Re-escalate so
                // OnFallenBack fires again (the retry path leaves state in FallenBack).
                ConsecutiveFailures++;
                _crash.RecordStrike(input.At);
                _commands.Add(SupervisorCommand.LaunchFallbackAt);
                break;

            case FallenBackRetry when State == SupervisorState.FallenBack:
                // User clicked "Retry EyeCtrl once": the retried app was launched with --paused, now
                // return to IdleWatch so its first verified heartbeat ADOPTS it into Supervising
                // (FallenBack has no HeartbeatReceived case). Resets the surviving-window + launch timers.
                State = SupervisorState.IdleWatch;
                _supervisingSince = null;
                _launchingSince = null;
                break;

            case DeadlineElapsed when State == SupervisorState.CleanExitPending:
                GoRecovering(input.At, forceKill: true); // claimed shutdown but didn't exit
                break;
        }
    }

    private void GoRecovering(DateTime at, bool forceKill)
    {
        ConsecutiveFailures++;
        _crash.RecordStrike(at);
        if (_crash.ShouldEscalate(at))
        {
            State = SupervisorState.FallenBack;
            _commands.Add(SupervisorCommand.LaunchFallbackAt);
        }
        else
        {
            State = SupervisorState.Recovering;
            if (forceKill) _commands.Add(SupervisorCommand.ForceKill);
            _commands.Add(SupervisorCommand.LaunchApp);
        }
        _supervisingSince = null;
        _launchingSince = null;
    }
}
