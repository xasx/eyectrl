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
    private readonly Action _reportInProgress;
    private readonly Action<string> _reportUnavailable;
    private readonly Action<Action> _runInBackground;
    private readonly Action<Action> _postToUi;
    private readonly Func<bool> _supervisorInstalled;
    private readonly SupervisorBringUp _bringUp;

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
    /// <param name="reportInProgress">
    /// Tells the user the restart has been accepted and is under way. Invoked on the
    /// CALLER's thread, unlike the outcome callbacks — see <see cref="HandleRestart"/>.
    /// </param>
    /// <param name="reportUnavailable">Tells the user why the restart did not happen.</param>
    /// <param name="runInBackground">Runs work off the dispatcher.</param>
    /// <param name="postToUi">Marshals work back onto the dispatcher.</param>
    /// <param name="supervisorInstalled">Whether a supervisor exe is installed (the offer predicate).</param>
    /// <param name="bringUp">
    /// The shared publish → launch → persist → Run-key → poll sequence. Shared with the Settings
    /// row's turn-on flow so the two orders cannot drift; <see cref="SupervisorBringUp"/> owns the
    /// ordering comments and the constants.
    /// </param>
    public AppRestartCoordinator(
        IMessenger messenger,
        EyeCtrlSettings settings,
        Func<bool> tryReachSupervisor,
        Func<RestartOutcome> selfRestart,
        Action shutdown,
        Action reportInProgress,
        Action<string> reportUnavailable,
        Action<Action> runInBackground,
        Action<Action> postToUi,
        Func<bool> supervisorInstalled,
        SupervisorBringUp bringUp)
    {
        _settings = settings;
        _tryReachSupervisor = tryReachSupervisor;
        _selfRestart = selfRestart;
        _shutdown = shutdown;
        _reportInProgress = reportInProgress;
        _reportUnavailable = reportUnavailable;
        _runInBackground = runInBackground;
        _postToUi = postToUi;
        _supervisorInstalled = supervisorInstalled;
        _bringUp = bringUp;

        messenger.Register<RestartAppRequestedMessage>(this, (_, m) => HandleRestart(m.StartSupervisor));
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
    /// Whether the settings surface should offer to start a supervisor before restarting.
    /// True only when one is installed and none is currently answering — offering what is
    /// not installed, or starting a second one, would both be noise.
    /// </summary>
    /// <remarks>
    /// <b>Not a pure query:</b> the reachability check sends one real heartbeat, and a live
    /// supervisor records/adopts it (see <c>Program.cs</c>'s heartbeat handler). That is safe
    /// here because the probe is only evaluated when <c>Supervision.Enabled</c> is already
    /// true — i.e. periodic heartbeats are already flowing, so the probe's extra payload is a
    /// no-op adoption, not the delayed-kill from Task 11 (which concerns probing while NOT
    /// publishing). The side-effect is called out because a reader named <c>Should…</c> would
    /// not expect it to reach across a process boundary.
    ///
    /// <b>Runs on the dispatcher</b> (the dwell handler asks it synchronously), so its probe
    /// is bounded by the publisher's <see cref="HeartbeatPublisher.DefaultProbeWait"/> (500ms —
    /// the production publisher passes no override): a live supervisor answers in
    /// milliseconds, and an absent one must not freeze the user's gaze control while they
    /// wait for a modal. The restart path itself never probes on the dispatcher — see
    /// <see cref="HandleRestart"/>.
    /// </remarks>
    /// <returns>True if the offer is worth making.</returns>
    public bool ShouldOfferSupervisor() =>
        _supervisorInstalled() && !(_settings.Supervision.Enabled && _tryReachSupervisor());

    /// <summary>
    /// Picks a strategy and runs it. Returns immediately; the work continues in the
    /// background. The dispatcher never blocks: the probe, the bring-up poll and the
    /// self-restart wait all happen off-thread, and the dispatcher only ever executes
    /// the shutdown or the refusal — a frozen dispatcher means no gaze processing, i.e.
    /// the user loses control of the machine for the duration.
    /// </summary>
    /// <param name="startSupervisor">True when the user chose to start a supervisor first.</param>
    public void HandleRestart(bool startSupervisor)
    {
        // One restart attempt at a time (see Task 8). Re-armed on the refusal path.
        if (Interlocked.Exchange(ref _restartInFlight, 1) != 0)
        {
            // Dropped. Deliberately silent: the notice for the attempt already running is
            // still on screen, and replacing it would only make a second dwell look like it
            // started something new.
            return;
        }

        // Acknowledge before any work begins. The self-restart handshake can take 30s, and
        // although the dispatcher stays free throughout, an unacknowledged 30s reads exactly
        // like the hang this feature exists to prevent — and the in-flight guard above means
        // dwelling the button again tells the user nothing either.
        //
        // Called directly rather than through _postToUi: this runs in HandleRestart's
        // prologue, still on the sender's thread, and every sender is on the dispatcher. The
        // production wiring only raises a message, and its handler marshals — the same shape
        // as the refusal path, so a future off-thread sender stays correct.
        _reportInProgress();

        _runInBackground(() =>
        {
            // Guarded exactly as in Task 8, and it matters more here: the bring-up's persist step
            // (SettingsService.Save/SaveSupervisionEnabled) THROWS on invalid settings or an
            // unreadable file. Unguarded, that exception would vanish into an unobserved faulted
            // Task with the button left latched — and with Supervision.Enabled already mutated
            // in memory.
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

                // Strategy 1b — the user asked us to start a supervisor first, which upgrades
                // this restart to the recoverable path AND leaves the replacement watched. Any
                // outcome short of a reachable supervisor falls through to self-restart; the
                // bring-up has already persisted Enabled, which is deliberate (late adoption).
                if (startSupervisor && _bringUp.TryBringUp() == SupervisorBringUpOutcome.Reachable)
                {
                    _postToUi(() =>
                    {
                        SupervisorRelaunchRequested = true;
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
                // Never leave the button latched — see Task 8.
                Volatile.Write(ref _restartInFlight, 0);
                _postToUi(() => _reportUnavailable(
                    "Restart failed: " + ex.Message + " EyeCtrl is still running."));
            }
        });
    }
}
