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
    /// <summary>Fresh on-disk <c>supervision.enabled</c> (last known value when unreadable). Read
    /// only when a due relaunch is about to execute (ticket 06-B).</summary>
    bool SupervisionEnabled { get; }
    /// <summary>Feeds an input to the machine and drains its commands (<c>HandleInput</c>).</summary>
    void Handle(SupervisionInput input);
    /// <summary>Consumes the due pending relaunch. <see cref="SupervisionClock.Tick"/> calls this
    /// immediately before <see cref="TryLaunchPendingNow"/> as part of the relaunch contract.</summary>
    void ClearPendingLaunch();
    /// <summary>Attempts the deferred launch. Returns true when a process started, false when the
    /// launch threw. Tick feeds <see cref="LaunchExecuted"/>/<see cref="LaunchFailed"/> from the
    /// result, so the outcome decision lives in tested clock code (ticket 06-F2).</summary>
    bool TryLaunchPendingNow();
    /// <summary>Runs the host's wire-time race guard for the just-launched process, AFTER the
    /// launch outcome was fed: a process that already exited must be seen as launched-then-exited,
    /// in that order (ticket 06 review F4). Only called when the launch succeeded.</summary>
    void WireLaunchedProcess();
    /// <summary>Feeds the FallenBack dialog's auto-retry countdown (gaze-UX A3). The countdown
    /// decides its own deadline and fires the shared retry path at most once; driving it from THIS
    /// tick is what makes "the clock tick drives the countdown" a clock-level, testable fact.</summary>
    void TickFallenBackAutoRetry(DateTime now);
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
        // (spec failure mode #2). Ticket 06-C: also in RECOVERING, so a recovery relaunch that
        // never starts heartbeating books a strike, is killed and is retried (or escalates),
        // instead of leaving the supervisor stuck in RECOVERING forever.
        if (StartupGraceElapsed(now, host.Machine.State, host.Machine.LaunchingSince, settings.StartupGraceSeconds))
        {
            Log.Warning("Startup grace ({Grace}s) elapsed in {State} with no heartbeat; recovering.",
                settings.StartupGraceSeconds, host.Machine.State);
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
        // relaunch (clear-then-launch) is the tick's contract, not the host's.
        var pendingLaunch = host.PendingLaunchAt;
        if (pendingLaunch is not null)
        {
            if (!LaunchExpected(host.Machine.State))
            {
                // Ticket 06-A: the machine no longer expects a launch — the app was adopted or
                // came up another way (or recovery escalated). Consume it; launching would race a
                // live instance into the app's single-instance mutex (04-review F1).
                host.ClearPendingLaunch();
            }
            else if (RelaunchDue(now, pendingLaunch))
            {
                host.ClearPendingLaunch();
                // Ticket 06-B: a Recovering relaunch is gated on the FRESH enabled read — enabled
                // was true when the strike was booked, but the user can switch supervision off
                // during the backoff. Launching is never gated: the tray's "Launch EyeCtrl now"
                // must not become a dead button while supervision is off.
                if (host.Machine.State == SupervisorState.Recovering && !host.SupervisionEnabled)
                {
                    host.Handle(new SupervisionDisabled(now));
                }
                else
                {
                    // Ticket 06-F2: the launch outcome is fed here, not by SupervisorApp glue, so
                    // deleting a feeder is a clock-level mutation covered by clock tests. F4: the
                    // wire-time race guard runs after the stamp, so an already-exited process is
                    // seen as launched-then-exited (one strike), not exit-then-stamp.
                    var launched = host.TryLaunchPendingNow();
                    SupervisionInput outcome = launched ? new LaunchExecuted(now) : new LaunchFailed(now);
                    host.Handle(outcome);
                    if (launched) host.WireLaunchedProcess();
                }
            }
        }

        // A3: the FallenBack dialog's auto-retry countdown - the tick's last wall-clock decision,
        // checked after the deferred relaunch. It only fires in FallenBack (where nothing above is
        // active), and its retry path feeds FallenBackRetry, which leaves the state again.
        host.TickFallenBackAutoRetry(now);
    }

    /// <summary>A pending launch still has a state that expects it: Launching (a requested launch —
    /// logon, tray, restart) or Recovering (a recovery relaunch).</summary>
    public static bool LaunchExpected(SupervisorState state) =>
        state is SupervisorState.Launching or SupervisorState.Recovering;

    /// <summary>Launching or Recovering for longer than <paramref name="startupGraceSeconds"/> with no heartbeat.</summary>
    public static bool StartupGraceElapsed(DateTime now, SupervisorState state, DateTime? launchingSince, int startupGraceSeconds) =>
        state is SupervisorState.Launching or SupervisorState.Recovering && launchingSince.HasValue &&
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
