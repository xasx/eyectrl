namespace EyeCtrl.Supervisor.Supervision;

using System;

/// <summary>
/// The FallenBack dialog's one automatic retry (gaze-UX ticket A3): a 60 s countdown for a user
/// who cannot click the dialog's mouse-only buttons, plus the per-session latch that spends it.
/// </summary>
/// <remarks>
/// <para>When the countdown expires it runs the SAME retry path as the dialog's Retry button
/// (launch the app with <c>--paused</c>, close the dialog, feed <see cref="FallenBackRetry"/>) —
/// the host passes that path in at arm time, so the two entry points cannot drift apart. A manual
/// Retry or Close cancels the countdown before it fires.</para>
/// <para>The latch makes the auto-retry a one-shot safety net, modelled on the supervisor's
/// <c>_fallbackAtLaunched</c>: set once per session, never reset. It is spent when the countdown is
/// ARMED, so the session's single countdown belongs to its FIRST FallenBack episode — a second
/// FallenBack, whether the auto-retry or a manual Retry led back there, waits for a click exactly
/// as before. Without that cap a crash loop becomes an unattended launch loop (each retry relaunches
/// <c>--paused</c>, with no helper present to resume).</para>
/// <para>Clock-injected like <see cref="SupervisionClock"/>: every decision takes the current
/// <c>now</c> as a parameter, so the countdown is tested headless against synthetic timestamps —
/// no dialog, no dispatcher, no wall clock. Production feeds it from the supervisor's 1 s clock
/// tick (<c>SupervisorApp.OnClockTick</c>), so a fired countdown is at most one tick late.</para>
/// </remarks>
internal sealed class FallenBackAutoRetry
{
    /// <summary>The countdown shown in the dialog ("Trying again in 60 s…") and enforced here.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(60);

    /// <summary>True once this session's countdown has been armed. Never reset.</summary>
    private bool _spent;

    /// <summary>When the current episode's countdown expires; null when none is pending.</summary>
    private DateTime? _dueAt;

    /// <summary>The host's retry path, kept for the tick to fire; null when no countdown is pending.</summary>
    private Action? _onAutoRetry;

    /// <summary>True while a countdown is pending (armed and neither fired nor cancelled).</summary>
    public bool IsCountdownArmed => _dueAt is not null;

    /// <summary>
    /// Starts this episode's countdown, at or after which <see cref="Tick"/> runs
    /// <paramref name="onAutoRetry"/>. False when the session's one countdown is already spent:
    /// that episode opens without one and waits for a click. A repeat call while a countdown is
    /// still pending (the dialog re-showing over an open one) keeps the ORIGINAL deadline — a
    /// re-show must not extend the wait — and re-targets the retry path at the dialog now on
    /// screen.
    /// </summary>
    public bool TryArm(DateTime now, Action onAutoRetry)
    {
        if (_spent && _dueAt is null) return false;
        _spent = true;
        _dueAt ??= now + Delay;
        _onAutoRetry = onAutoRetry;
        return true;
    }

    /// <summary>
    /// A manual Retry or Close: the countdown stops and never fires. Idempotent, and called again
    /// when the fired path closes the dialog — a no-op once the countdown is gone.
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
