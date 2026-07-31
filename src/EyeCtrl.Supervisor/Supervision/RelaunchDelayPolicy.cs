namespace EyeCtrl.Supervisor.Supervision;

using System;

/// <summary>
/// How long to wait before acting on a <see cref="SupervisorCommand.LaunchApp"/>.
///
/// Backoff exists to stop a crash loop hammering the process table. A user-requested
/// restart is not a crash loop: it books no strike, and the user is waiting for the
/// only interface they can operate to come back. Making them wait up to 60s because
/// the app happened to crash earlier in the window reads as a hang.
///
/// The restart is identified by the state PAIR, not by the previous state alone. Two
/// transitions leave <see cref="SupervisorState.CleanExitPending"/> emitting LaunchApp:
/// Task 2's restart branch (to <see cref="SupervisorState.Launching"/>, no strike) and
/// the hung-shutdown force-kill (DeadlineElapsed → GoRecovering → Recovering/FallenBack,
/// which DOES book a strike). Only the first may skip backoff.
/// </summary>
public static class RelaunchDelayPolicy
{
    /// <summary>
    /// Chooses the delay for a launch command.
    /// </summary>
    /// <param name="stateWhenCommanded">The state the machine was in before this input.</param>
    /// <param name="stateAfterInput">
    /// The state the machine reached. Together with <paramref name="stateWhenCommanded"/>
    /// this identifies the emitting transition: <c>CleanExitPending → Launching</c> is the
    /// user-requested restart, and the only path that reaches LaunchApp without a strike.
    /// <c>CleanExitPending → Recovering/FallenBack</c> is the hung-shutdown force-kill,
    /// which is a real crash recovery and must keep its brake.
    /// </param>
    /// <param name="backoffDelay">The crash-backoff delay that would otherwise apply.</param>
    /// <returns>Zero for a user-requested restart; <paramref name="backoffDelay"/> otherwise.</returns>
    public static TimeSpan For(
        SupervisorState stateWhenCommanded,
        SupervisorState stateAfterInput,
        TimeSpan backoffDelay) =>
        stateWhenCommanded == SupervisorState.CleanExitPending
            && stateAfterInput == SupervisorState.Launching
                ? TimeSpan.Zero
                : backoffDelay;
}
