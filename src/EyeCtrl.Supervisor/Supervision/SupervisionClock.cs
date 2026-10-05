namespace EyeCtrl.Supervisor.Supervision;

using System;
using EyeCtrl.Core.Settings;
using Serilog;

/// <summary>
/// What the clock tick needs from the supervisor: the live state machine and policy, the two
/// timestamps the supervisor keeps outside the machine, and the two actions a tick can take.
/// Implemented by <c>SupervisorApp</c>; tests implement it over a real
/// <see cref="SupervisionStateMachine"/>.
/// </summary>
internal interface ISupervisionClockHost
{
    SupervisionStateMachine Machine { get; }
    SupervisionSettings Settings { get; }
    /// <summary>When the machine entered CleanExitPending; null otherwise.</summary>
    DateTime? CleanExitRequestedAt { get; }
    /// <summary>When a deferred (backed-off) relaunch is due; null when none is pending.</summary>
    DateTime? PendingLaunchAt { get; }
    /// <summary>Feeds an input to the machine and drains its commands (<c>HandleInput</c>).</summary>
    void Handle(SupervisionInput input);
    /// <summary>Consumes the due pending relaunch. <see cref="SupervisionClock.Tick"/> calls this
    /// immediately before <see cref="LaunchPendingNow"/> as part of the relaunch contract.</summary>
    void ClearPendingLaunch();
    /// <summary>Launches the app now. The pending relaunch has already been cleared by the tick.</summary>
    void LaunchPendingNow();
}

/// <summary>
/// The supervisor's wall-clock decisions — startup grace, heartbeat staleness, survived crash
/// window, clean-exit deadline, deferred relaunch — as pure threshold functions plus the tick that
/// applies them. Extracted from <c>SupervisorApp</c>, which is not unit-tested as a whole: a wrong
/// state guard or a <c>&gt;</c> vs <c>&gt;=</c> here could only be caught live, by a gaze-only user
/// whose app has just hung (architecture-check-2026-10, ticket 03). Behaviour is unchanged from the
/// inline checks it replaces: same order, same comparisons, same state guards.
/// </summary>
internal static class SupervisionClock
{
    /// <summary>
    /// Clean-exit grace: once the app signals <c>ShuttingDown</c> the supervisor waits this long
    /// for the process to actually exit before treating it as a hung shutdown (DeadlineElapsed).
    /// See spec "Clean exit" + the Task 20 pseudocode ("if sm.State == CleanExitPending && now -
    /// cleanExitRequestedAt > 5s -> DeadlineElapsed").
    /// </summary>
    public const int CleanExitDeadlineSeconds = 5;

    /// <summary>
    /// One clock tick. The checks run in this order, each against the machine's CURRENT state —
    /// an input fed by an earlier check can change what a later one sees (a stale heartbeat moves
    /// Supervising to Recovering, so the survived-window check must not fire in the same tick).
    /// They are therefore applied one at a time, never evaluated from a single snapshot. The
    /// deferred relaunch is checked last.
    /// </summary>
    public static void Tick(DateTime now, ISupervisionClockHost host)
    {
        var settings = host.Settings;

        // C1: startup grace — app launched but no heartbeat within StartupGraceSeconds -> recover
        // (spec failure mode #2).
        if (StartupGraceElapsed(now, host.Machine.State, host.Machine.LaunchingSince, settings.StartupGraceSeconds))
        {
            Log.Warning("Startup grace ({Grace}s) elapsed in LAUNCHING with no heartbeat; recovering.",
                settings.StartupGraceSeconds);
            host.Handle(new StartupGraceElapsed(now));
        }

        if (HeartbeatStale(now, host.Machine.State, host.Machine.LastHeartbeatUtc, settings.HangThresholdSeconds))
            host.Handle(new HeartbeatStale(now));

        // SurvivedWindow reset is driven entirely by the state machine (it resets the crash window
        // + counter on a SurvivedWindow input, AND re-arms SupervisingSince to input.At so this
        // check doesn't re-fire every tick). Emitted after the app has been continuously
        // Supervising for longer than the crash window, reading the machine's own SupervisingSince.
        if (SurvivedCrashWindow(now, host.Machine.State, host.Machine.SupervisingSince, settings.CrashWindowSeconds))
            host.Handle(new SurvivedWindow(now));

        if (CleanExitDeadlineElapsed(now, host.Machine.State, host.CleanExitRequestedAt))
            host.Handle(new DeadlineElapsed(now));

        // C2: a recovery relaunch is NOT executed immediately when the machine commands it; it is
        // deferred until the backoff delay elapses, so a tight crash loop doesn't hammer the
        // app/process table and the spec's worked-example timing holds. Consuming the pending
        // relaunch (clear-then-launch) is the tick's contract, not the host's: a host that
        // launched without clearing would relaunch on every tick.
        if (RelaunchDue(now, host.PendingLaunchAt))
        {
            host.ClearPendingLaunch();
            host.LaunchPendingNow();
        }
    }

    /// <summary>Launching for longer than <paramref name="startupGraceSeconds"/> with no heartbeat.</summary>
    public static bool StartupGraceElapsed(DateTime now, SupervisorState state, DateTime? launchingSince, int startupGraceSeconds) =>
        state == SupervisorState.Launching && launchingSince.HasValue &&
        (now - launchingSince.Value).TotalSeconds > startupGraceSeconds;

    /// <summary>Supervising, and the last heartbeat is older than <paramref name="hangThresholdSeconds"/>.</summary>
    public static bool HeartbeatStale(DateTime now, SupervisorState state, DateTime? lastHeartbeatUtc, int hangThresholdSeconds) =>
        lastHeartbeatUtc.HasValue &&
        (now - lastHeartbeatUtc.Value).TotalSeconds > hangThresholdSeconds &&
        state == SupervisorState.Supervising;

    /// <summary>Supervising continuously for longer than the crash window.</summary>
    public static bool SurvivedCrashWindow(DateTime now, SupervisorState state, DateTime? supervisingSince, int crashWindowSeconds) =>
        state == SupervisorState.Supervising && supervisingSince.HasValue &&
        (now - supervisingSince.Value).TotalSeconds > crashWindowSeconds;

    /// <summary>A clean exit was announced more than <see cref="CleanExitDeadlineSeconds"/> ago and the
    /// process is still there.</summary>
    public static bool CleanExitDeadlineElapsed(DateTime now, SupervisorState state, DateTime? cleanExitRequestedAt) =>
        state == SupervisorState.CleanExitPending &&
        cleanExitRequestedAt.HasValue &&
        (now - cleanExitRequestedAt.Value).TotalSeconds > CleanExitDeadlineSeconds;

    /// <summary>A deferred relaunch is due — at or after its scheduled time.</summary>
    public static bool RelaunchDue(DateTime now, DateTime? pendingLaunchAt) =>
        pendingLaunchAt.HasValue && now >= pendingLaunchAt.Value;
}
