namespace EyeCtrl.App.Services;

using System;
using System.Threading;
using EyeCtrl.Core.Settings;

/// <summary>How far a supervisor bring-up attempt got.</summary>
public enum SupervisorBringUpOutcome
{
    /// <summary>No process was started: nothing was persisted, and the Run key was not touched.</summary>
    LaunchFailed,

    /// <summary>A process started and Enabled was persisted and reconciled, but nothing answered on
    /// the heartbeat pipe within the wait.</summary>
    StartedNotReachable,

    /// <summary>The supervisor answered on the heartbeat pipe: the app is adopted.</summary>
    Reachable
}

/// <summary>
/// The shared "start the supervisor and require evidence" sequence: heartbeat publishing on, a
/// started process, Enabled flipped in memory and persisted, the Run key reconciled to the
/// supervised policy, then a bounded wait for the supervisor to answer. The restart path (which
/// upgrades a restart to the recoverable path) and the Settings row (which turns supervision on
/// in place) both run this one sequence, so their orders cannot drift.
///
/// <para>Failure handling deliberately lives in the callers, because it differs. The restart path
/// treats anything short of <see cref="SupervisorBringUpOutcome.Reachable"/> as "fall through to
/// self-restart" and keeps Enabled persisted (a supervisor that answers late can still adopt the
/// replacement, and it kills instances that do not heartbeat). The Settings row rolls back to
/// false, because the running app must not stay armed kill-for-respawn — the live crash policy
/// reads <c>Settings.Supervision.Enabled</c> at fault time — with no watcher.</para>
/// </summary>
public sealed class SupervisorBringUp
{
    private readonly EyeCtrlSettings _settings;
    private readonly Action _enablePublishing;
    private readonly Func<bool> _launchSupervisor;
    private readonly Action<bool> _persistEnabled;
    private readonly Action _reconcileRunKey;
    private readonly Func<bool> _tryReachSupervisor;
    private readonly Func<DateTime> _utcNow;
    private readonly Action<TimeSpan> _sleep;

    /// <summary>How long to wait for a just-started supervisor to answer on the pipe.</summary>
    public static readonly TimeSpan StartWait = TimeSpan.FromSeconds(15);

    /// <summary>Gap between reachability polls while waiting for the supervisor.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <param name="settings">Live settings; <c>Supervision.Enabled</c> is flipped here, so the
    /// running session's crash policy follows the successful bring-up.</param>
    /// <param name="enablePublishing">Turns on heartbeat publishing (the first step).</param>
    /// <param name="launchSupervisor">Starts the supervisor; false on any failure.</param>
    /// <param name="persistEnabled">Writes the flag through the sanctioned path (never the App writer).</param>
    /// <param name="reconcileRunKey">Applies the supervised launch-at-logon policy (see
    /// <c>StartOnBootPolicy</c>); runs AFTER the persist, the order the old combined persist lambda had.</param>
    /// <param name="tryReachSupervisor">Probes the heartbeat pipe; true only if a payload was delivered.</param>
    /// <param name="utcNow">Clock for the reachability budget; tests inject a fake. Real time by default.</param>
    /// <param name="sleep">Sleep between polls; tests inject the fake clock's advance. Real sleep by default.</param>
    public SupervisorBringUp(
        EyeCtrlSettings settings,
        Action enablePublishing,
        Func<bool> launchSupervisor,
        Action<bool> persistEnabled,
        Action reconcileRunKey,
        Func<bool> tryReachSupervisor,
        Func<DateTime>? utcNow = null,
        Action<TimeSpan>? sleep = null)
    {
        _settings = settings;
        _enablePublishing = enablePublishing;
        _launchSupervisor = launchSupervisor;
        _persistEnabled = persistEnabled;
        _reconcileRunKey = reconcileRunKey;
        _tryReachSupervisor = tryReachSupervisor;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _sleep = sleep ?? Thread.Sleep;
    }

    /// <summary>
    /// Runs the sequence. The persist step can throw (a settings write), and so can the Run-key
    /// reconcile (a registry write); the callers' handlers turn that into a report rather than a
    /// dead control.
    /// </summary>
    public SupervisorBringUpOutcome TryBringUp()
    {
        // Publishing FIRST. It cannot throw, and a probe that adopts us before we publish is
        // the delayed-kill shape from Task 11. Safe to leave on even if the rest fails:
        // heartbeats nobody listens to cost nothing.
        _enablePublishing();

        // Launch BEFORE persisting. Writing Enabled to disk is not just a note to the
        // replacement — it hands launch-at-logon to the supervisor (the caller's reconcile).
        // Persisting before anything had been started meant a launch that never produced a
        // process still latched a flag documented "opt-in for v1" AND surrendered the only
        // mechanism that starts the app. Require evidence of a process first.
        if (!_launchSupervisor())
        {
            return SupervisorBringUpOutcome.LaunchFailed;
        }

        // A process was started, so flip the running session (the crash policy reads this live)
        // and persist — deliberately NOT gated on the poll below. The bar is a started
        // supervisor, not a reachable one: a supervisor that answers late can still adopt the
        // running app, and it kills instances that do not heartbeat. Persisting is what keeps
        // the app publishing.
        _settings.Supervision.Enabled = true;
        _persistEnabled(true);

        // Reconcile launch-at-logon with the supervised policy, after the persist, so the
        // policy reads a flag that is already on disk.
        _reconcileRunKey();

        // Poll rather than sleep-then-check: a supervisor that comes up in 300ms should
        // not cost the user the whole budget.
        var deadline = _utcNow() + StartWait;
        while (_utcNow() < deadline)
        {
            if (_tryReachSupervisor())
            {
                return SupervisorBringUpOutcome.Reachable;
            }

            _sleep(PollInterval);
        }

        return SupervisorBringUpOutcome.StartedNotReachable;
    }
}
