namespace EyeCtrl.App.Services;

using System;
using System.Threading;

/// <summary>
/// The agreement between an outgoing EyeCtrl instance and the replacement it launches.
///
/// The child signals <see cref="ReadyEventName"/> as soon as it is running and BEFORE
/// it waits for the single-instance mutex. That ordering is load-bearing in both
/// directions:
///
/// <list type="bullet">
/// <item>Signal-then-wait lets the parent learn "my replacement started" while the
/// parent still holds the mutex — which is the only moment at which the parent can
/// still change its mind and stay running.</item>
/// <item>Wait-then-signal would deadlock: the child cannot take the mutex until the
/// parent exits, and the parent will not exit until the child signals.</item>
/// </list>
///
/// What the signal proves is deliberately modest: the exe path resolved, the runtime
/// loaded, the single-file bundle extracted, and nothing blocked the launch. It does
/// NOT prove the child will finish initialising — see the startup-failure fallback.
/// </summary>
public static class RestartHandoff
{
    /// <summary>Command-line argument marking a process as a restart replacement.</summary>
    public const string Argument = "--restart-handoff";

    /// <summary>
    /// Name of the event a restart child sets to announce it is alive and queued.
    /// <c>Local\</c> (not <c>Global\</c>) to match the single-instance mutex: multi-user
    /// and RDP are non-goals, and a session-local name avoids cross-session collisions.
    ///
    /// Owned by the PARENT for the duration of the handoff: the parent creates it BEFORE
    /// launching the child and holds it open across the wait (see
    /// <see cref="SelfRestartStrategy.CreateDefault"/>). That is what makes the child's
    /// signal reliable in both directions — see <see cref="SignalReady"/>.
    /// </summary>
    public const string ReadyEventName = @"Local\EyeCtrl.App.RestartReady";

    /// <summary>
    /// How long a restart child waits for the outgoing instance to release the mutex
    /// before giving up and exiting quietly. Generous: the parent's OnExit saves
    /// settings and the user dictionary and tears down the gaze engine.
    /// </summary>
    public static readonly TimeSpan MutexWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a parent waits for its child to signal readiness before treating the
    /// restart as failed. Covers a cold start of a self-contained single-file build.
    /// </summary>
    public static readonly TimeSpan ReadyWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Sets the ready event. Safe to call when no parent is listening — see the note below.
    /// </summary>
    /// <remarks>
    /// The child OPENS the event the parent already owns rather than creating-or-opening it.
    /// <see cref="EventWaitHandle.OpenExisting"/> is used deliberately:
    /// <list type="bullet">
    /// <item><b>A missing event means no parent is waiting</b> (the parent always creates it
    /// before launching). A standalone launch must not fabricate an orphaned Set event into
    /// the void, so a missing event is a clean no-op, not a signal. <c>CreateOrOpen</c> would
    /// leave a stray Set event from a launch with no listener, which the NEXT restart's parent
    /// could observe as a false ready.</item>
    /// <item>The child never RESETs the event. The parent created it unset and holds it; the
    /// child only ever Sets. Reversed roles (child creating, parent observing) would need the
    /// creator to set <c>initialState</c>, which only applies on first creation — a leftover
    /// event would be opened <i>still Set</i> from a prior run and produce a false positive.</item>
    /// </list>
    /// </remarks>
    public static void SignalReady()
    {
        try
        {
            using var ready = EventWaitHandle.OpenExisting(ReadyEventName);
            ready.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // No parent created this event, so there is no handoff to acknowledge. The child
            // proceeds with normal startup — it was not launched as a restart replacement, or
            // the parent died before we got here. Do NOT fabricate an event: a stray Set event
            // with no owner would be observed as a false ready by the next restart's parent.
        }
        catch
        {
            // Best-effort. A child that cannot signal for any other reason will be killed by
            // the parent's timeout, and the parent stays running — the safe outcome either way.
        }
    }
}
