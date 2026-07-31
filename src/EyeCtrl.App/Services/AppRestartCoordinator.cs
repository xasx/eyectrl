namespace EyeCtrl.App.Services;

using System;
using System.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Owns the decision to restart, and — more importantly — the decision to refuse.
///
/// <b>The rule this class exists to enforce: never shut down until a successor has been
/// proven to exist.</b> Two strategies can supply that proof, tried in order of safety:
///
/// <list type="number">
/// <item><b>Supervisor-mediated.</b> Preferred wherever available, because it is the only
/// one that recovers a relaunch which then fails — the supervisor retries; a process that
/// has exited cannot. Requires a live answer on the heartbeat pipe, not merely
/// <c>Supervision.Enabled</c>, which records only that one was configured.</item>
/// <item><b>Self-restart.</b> Launches a replacement and waits for it to announce itself
/// before exiting. Covers the unsupervised case, which is the default.</item>
/// </list>
///
/// Both strategies run OFF the dispatcher: the probe can wait up to 500ms and the
/// self-restart up to 30s, and a frozen dispatcher means no gaze processing — the user
/// loses control of the machine for the duration. <see cref="HandleRestart"/> marshals
/// the outcome back to the dispatcher, which only ever executes the shutdown or the
/// refusal.
///
/// If neither can prove it, the restart is abandoned and the app keeps running — always
/// recoverable, unlike exiting into nothing.
/// </summary>
public sealed class AppRestartCoordinator
{
    private readonly EyeCtrlSettings _settings;
    private readonly Func<bool> _tryReachSupervisor;
    private readonly Func<RestartOutcome> _selfRestart;
    private readonly Action _shutdown;
    private readonly Action<string> _reportUnavailable;
    private readonly Action<Action> _runInBackground;
    private readonly Action<Action> _postToUi;

    /// <summary>
    /// 0 = idle, 1 = a restart attempt is in progress. Guards against a second dwell
    /// launching a second child while the first readiness wait is still running — the
    /// loser would sit on the single-instance mutex for 30s and then exit.
    ///
    /// Latched with <see cref="Interlocked.Exchange(ref int, int)"/> and released with
    /// <see cref="Volatile.Write(ref int, int)"/>. Every exit from the background body
    /// must release it, including the exceptional one: a latch that is never cleared
    /// deadens the button for the remainder of the session, silently.
    /// </summary>
    private int _restartInFlight;

    /// <summary>Creates the coordinator and subscribes it to <see cref="RestartAppRequestedMessage"/>.</summary>
    /// <param name="messenger">Application messenger.</param>
    /// <param name="settings">Loaded settings; <c>Supervision.Enabled</c> selects the first strategy.</param>
    /// <param name="tryReachSupervisor">Probes the heartbeat pipe; true only if a payload was delivered.</param>
    /// <param name="selfRestart">Runs the self-restart handshake. Blocks; never called on the dispatcher.</param>
    /// <param name="shutdown">Begins application shutdown. Normally <c>Application.Shutdown</c>.</param>
    /// <param name="reportUnavailable">Tells the user why the restart did not happen.</param>
    /// <param name="runInBackground">Runs work off the dispatcher.</param>
    /// <param name="postToUi">Marshals work back onto the dispatcher.</param>
    public AppRestartCoordinator(
        IMessenger messenger,
        EyeCtrlSettings settings,
        Func<bool> tryReachSupervisor,
        Func<RestartOutcome> selfRestart,
        Action shutdown,
        Action<string> reportUnavailable,
        Action<Action> runInBackground,
        Action<Action> postToUi)
    {
        _settings = settings;
        _tryReachSupervisor = tryReachSupervisor;
        _selfRestart = selfRestart;
        _shutdown = shutdown;
        _reportUnavailable = reportUnavailable;
        _runInBackground = runInBackground;
        _postToUi = postToUi;

        messenger.Register<RestartAppRequestedMessage>(this, (_, _) => HandleRestart());
    }

    /// <summary>
    /// Set only when the SUPERVISOR will perform the relaunch, and read by
    /// <c>App.OnExit</c> when it stamps the final heartbeat.
    ///
    /// Deliberately not set for a self-restart: a supervisor that is alive despite a
    /// failed probe would then launch a second instance beside the child already
    /// started, and one of them would die on the single-instance mutex.
    /// </summary>
    public bool SupervisorRelaunchRequested { get; private set; }

    /// <summary>
    /// Picks a strategy and runs it. Returns immediately; the work continues in the
    /// background. The dispatcher never blocks: the probe, the bring-up poll and the
    /// self-restart wait all happen off-thread, and the dispatcher only ever executes
    /// the shutdown or the refusal — a frozen dispatcher means no gaze processing, i.e.
    /// the user loses control of the machine for the duration.
    /// </summary>
    public void HandleRestart()
    {
        // One restart attempt at a time. A second dwell while the first readiness wait
        // is still running must not launch a second child (it would queue on the mutex
        // for 30s and then exit). Re-armed on the refusal path, so a failed attempt
        // does not deaden the button.
        if (Interlocked.Exchange(ref _restartInFlight, 1) != 0)
        {
            return;
        }

        _runInBackground(() =>
        {
            // Everything below runs on a thread-pool thread, where an escaping exception
            // becomes an unobserved faulted Task — silent. If that happened, _postToUi would
            // never run, _restartInFlight would stay latched, and the button would be dead
            // for the rest of the session with no modal and no log: the user dwells the one
            // control meant to get them unstuck and nothing happens. So the whole body is
            // guarded, and the guard re-arms.
            try
            {
                // Strategy 1 — supervisor-mediated. Enabled is necessary but not sufficient:
                // the app may have been launched by hand with no supervisor running.
                if (_settings.Supervision.Enabled && _tryReachSupervisor())
                {
                    _postToUi(() =>
                    {
                        SupervisorRelaunchRequested = true;   // before _shutdown: OnExit reads this
                        _shutdown();
                    });
                    return;
                }

                // Strategy 2 — self-restart. Begin() blocks for up to the readiness timeout.
                var outcome = _selfRestart();
                _postToUi(() =>
                {
                    if (outcome == RestartOutcome.Started)
                    {
                        _shutdown();
                    }
                    else
                    {
                        Volatile.Write(ref _restartInFlight, 0);   // re-arm: the user may try again
                        _reportUnavailable(SelfRestartStrategy.Describe(outcome));
                    }
                });
            }
            catch (Exception ex)
            {
                // Never leave the button latched. The app is still running by construction —
                // nothing here calls _shutdown outside the proven-successor branches — so the
                // safe report is the generic one.
                Volatile.Write(ref _restartInFlight, 0);
                _postToUi(() => _reportUnavailable(
                    "Restart failed: " + ex.Message + " EyeCtrl is still running."));
            }
        });
    }
}
