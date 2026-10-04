namespace EyeCtrl.App.Services;

using System;
using EyeCtrl.Core.Settings;

/// <summary>
/// Ticket 07: the App ensures a Supervisor exists when supervision is switched on — at startup,
/// or when the row's retry asks for it. After a reboot nothing starts the Supervisor (no installer
/// task exists), yet <c>enabled=true</c> arms the app's kill-for-respawn crash policy, so On was
/// worse than Off. This closes that gap: enable publishing (safe, and mandatory before any probe
/// can adopt the app), adopt an already-running Supervisor via the probe, otherwise launch one,
/// then wait for it to answer.
/// </summary>
/// <remarks>
/// Deliberately write-free: no persist step and no launch-at-logon reconcile (the class has no
/// dependency for either). "Answers" means <see cref="HeartbeatPublisher.TryReachSupervisor"/>
/// delivered a payload to the identity-checked Supervisor on the pipe. A failed launch leaves no
/// trace and is retried on the next app start or row retry.
/// </remarks>
public sealed class SupervisorStartupEnsure
{
    /// <summary>Bounded wait for a just-launched supervisor to answer; the bring-up's budget.</summary>
    public static readonly TimeSpan StartWait = SupervisorBringUp.StartWait;

    /// <summary>Gap between reachability polls; the bring-up's interval.</summary>
    public static readonly TimeSpan PollInterval = SupervisorBringUp.PollInterval;

    private readonly EyeCtrlSettings _settings;
    private readonly Action _enablePublishing;
    private readonly Func<bool> _launchSupervisor;
    private readonly Func<bool> _tryReachSupervisor;
    private readonly Func<DateTime> _utcNow;
    private readonly Action<TimeSpan> _sleep;

    /// <param name="settings">Live settings; the enabled guard reads it.</param>
    /// <param name="enablePublishing">Turns on heartbeat publishing (idempotent).</param>
    /// <param name="launchSupervisor">Starts the Supervisor; false when none is installed or the launch threw.</param>
    /// <param name="tryReachSupervisor">Probes the heartbeat pipe; true only when a payload was delivered.</param>
    /// <param name="utcNow">Clock for the reachability budget; tests inject a fake. Real time by default.</param>
    /// <param name="sleep">Sleep between polls; tests inject the fake clock's advance.</param>
    public SupervisorStartupEnsure(
        EyeCtrlSettings settings,
        Action enablePublishing,
        Func<bool> launchSupervisor,
        Func<bool> tryReachSupervisor,
        Func<DateTime>? utcNow = null,
        Action<TimeSpan>? sleep = null)
    {
        _settings = settings;
        _enablePublishing = enablePublishing;
        _launchSupervisor = launchSupervisor;
        _tryReachSupervisor = tryReachSupervisor;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _sleep = sleep ?? System.Threading.Thread.Sleep;
    }

    /// <summary>
    /// Runs the sequence synchronously and reports whether a Supervisor is answering at the end.
    /// false when supervision is off, the launch failed, or the launched Supervisor never answered.
    /// Callers run this off the UI thread (the toggle's background step).
    /// </summary>
    public bool Run()
    {
        if (!_settings.Supervision.Enabled) return false;

        _enablePublishing();

        // An already-running Supervisor adopts the app on this probe; no launch needed. (A probe
        // without publishing would be a delayed kill — EnablePublishing ran first, above.)
        if (_tryReachSupervisor()) return true;

        if (!_launchSupervisor())
        {
            Serilog.Log.Warning("Startup ensure: the Supervisor could not be started; continuing unsupervised.");
            return false;
        }

        var deadline = _utcNow() + StartWait;
        while (_utcNow() < deadline)
        {
            if (_tryReachSupervisor()) return true;
            _sleep(PollInterval);
        }

        Serilog.Log.Warning(
            "Startup ensure: the Supervisor started but never answered within {Seconds:F0}s.",
            StartWait.TotalSeconds);
        return false;
    }
}
