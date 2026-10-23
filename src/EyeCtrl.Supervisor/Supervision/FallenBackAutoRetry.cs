namespace EyeCtrl.Supervisor.Supervision;

using System;
using EyeCtrl.Supervisor.UI;

/// <summary>
/// The FallenBack dialog's one automatic retry (gaze-UX ticket A3): a 60 s countdown for a user
/// who cannot click the dialog's mouse-only buttons, plus the per-session latch that spends it.
/// </summary>
/// <remarks>
/// <para><see cref="Attach"/> wires one FallenBack episode (fixup-01): it subscribes the dialog's
/// Retry, Dismiss and Close to the countdown's cancel, arms the countdown and shows its line. Both
/// entry points - the Retry button and the countdown - then run the SAME retry path, passed in at
/// attach time as (trigger, launchPaused), so they cannot drift apart. The relaunch is PAUSED
/// (<see cref="LaunchPaused"/>, A3's paused-retry handoff): the retried instance is inert until the
/// user is ready.</para>
/// <para>The latch makes the auto-retry a one-shot safety net, modelled on the supervisor's
/// <c>_fallbackAtLaunched</c>: set once per session, never reset. It is spent when the countdown is
/// ARMED, so the session's single countdown belongs to its FIRST FallenBack episode — a second
/// FallenBack, whether the auto-retry or a manual Retry led back there, waits for a click exactly
/// as before. Without that cap a crash loop becomes an unattended launch loop (each retry relaunches
/// <c>--paused</c>, with no helper present to resume).</para>
/// <para>Clock-injected like <see cref="SupervisionClock"/>: every decision takes the current
/// <c>now</c> as a parameter, so the countdown is tested headless against synthetic timestamps —
/// no dialog shown, no dispatcher, no wall clock. Production feeds it from the supervisor's 1 s
/// clock tick (<see cref="SupervisionClock.Tick"/> via <see cref="ISupervisionClockHost"/>'s
/// <c>TickFallenBackAutoRetry</c>), so a fired countdown is at most one tick late.</para>
/// </remarks>
internal sealed class FallenBackAutoRetry
{
    /// <summary>The countdown shown in the dialog ("Trying again in 60 s…") and enforced here.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(60);

    /// <summary>A3's paused-retry handoff: both entry points relaunch the app PAUSED (inert until
    /// the user is ready), without mutating the persisted setting. The attach tests pin that the
    /// flag reaches the shared retry path as true.</summary>
    public const bool LaunchPaused = true;

    /// <summary>Trigger names the shared retry path receives, for its log line.</summary>
    public const string TriggerDialogRetry = "dialog Retry";

    /// <summary>Trigger names the shared retry path receives, for its log line.</summary>
    public const string TriggerCountdown = "auto-retry countdown";

    /// <summary>True once this session's countdown has been armed. Never reset.</summary>
    private bool _spent;

    /// <summary>When the current episode's countdown expires; null when none is pending.</summary>
    private DateTime? _dueAt;

    /// <summary>The host's retry path, kept for the tick to fire; null when no countdown is pending.</summary>
    private Action? _onAutoRetry;

    /// <summary>True while a countdown is pending (armed and neither fired nor cancelled).</summary>
    public bool IsCountdownArmed => _dueAt is not null;

    /// <summary>
    /// Wires one FallenBack episode onto <paramref name="dialog"/> and arms the session's one
    /// countdown for it (fixup-01's testable seam - the wiring the composition root used to keep):
    /// the Retry button and the countdown both run <paramref name="retryNow"/>, a manual Retry or
    /// any Close cancels the countdown, and the countdown line shows while it is pending. False
    /// from <see cref="TryArm"/> means this episode opens without a countdown and waits for a click.
    /// </summary>
    /// <param name="retryNow">The shared retry path: (trigger, launchPaused). Called with
    /// <see cref="TriggerDialogRetry"/> from the button, <see cref="TriggerCountdown"/> from the
    /// countdown, and <see cref="LaunchPaused"/> from both.</param>
    public void Attach(FallenBackDialog dialog, Action<string, bool> retryNow, DateTime now)
    {
        void Retry(string trigger)
        {
            Cancel();
            dialog.ClearAutoRetryCountdown();
            retryNow(trigger, LaunchPaused);
        }

        dialog.RetryRequested += () => Retry(TriggerDialogRetry);
        dialog.Dismissed += Cancel;
        // ANY close cancels - the title-bar X included, which raises no Dismissed. A user who
        // closed the dialog said "not now"; an auto-retry firing afterwards would relaunch behind
        // their back.
        dialog.Closed += (_, _) => Cancel();
        if (TryArm(now, () => Retry(TriggerCountdown)))
            dialog.ShowAutoRetryCountdown();
    }

    /// <summary>
    /// Starts this episode's countdown, at or after which <see cref="Tick"/> runs
    /// <paramref name="onAutoRetry"/>. False when the session's one countdown is already spent:
    /// that episode opens without one and waits for a click. At most one countdown per session
    /// (fixup-01 F3): a repeat arm is refused even while one is still pending.
    /// </summary>
    public bool TryArm(DateTime now, Action onAutoRetry)
    {
        if (_spent) return false;
        _spent = true;
        _dueAt = now + Delay;
        _onAutoRetry = onAutoRetry;
        return true;
    }

    /// <summary>
    /// A manual Retry or Close: the countdown stops and never fires. Idempotent, and called again
    /// when the fired path closes the dialog - a no-op once the countdown is gone.
    /// </summary>
    public void Cancel()
    {
        _dueAt = null;
        _onAutoRetry = null;
    }

    /// <summary>
    /// One clock tick: fires the pending countdown exactly once, at or after its deadline. The
    /// countdown is consumed BEFORE the action runs, so a retry path that re-enters the tick (or
    /// throws) cannot fire it twice.
    /// </summary>
    public void Tick(DateTime now)
    {
        if (_dueAt is not { } due || now < due) return;
        var fire = _onAutoRetry;
        Cancel();
        fire?.Invoke();
    }
}
