namespace EyeCtrl.App.Services;

using System;
using EyeCtrl.Core.Settings;

/// <summary>
/// The App's in-place supervision switch for the Settings row. Turning on reuses the shared
/// <see cref="SupervisorBringUp"/> sequence; turning off persists the flag through the sanctioned
/// write path and restores self-registration for launch-at-logon.
///
/// <para>
/// The row runs on the UI thread while a bring-up can poll for up to
/// <see cref="SupervisorBringUp.StartWait"/>, so both requests return immediately and complete in
/// the background. The outcome is published by flipping the live <see cref="EyeCtrlSettings"/>
/// (the crash policy reads <c>Supervision.Enabled</c> at fault time — a failed turn-on must not
/// leave kill-for-respawn armed with no watcher) and by raising <see cref="StateChanged"/> on the
/// UI thread, which the settings window turns into a rebuild.
/// </para>
/// </summary>
public sealed class SupervisionToggleService
{
    private readonly EyeCtrlSettings _settings;
    private readonly SupervisorBringUp _bringUp;
    private readonly Action<bool> _persistEnabled;
    private readonly Func<bool> _supervisorInstalled;
    private readonly Action _reconcileRunKey;
    private readonly Action<Action> _runInBackground;
    private readonly Action<Action> _postToUi;

    private bool _inFlight;
    private bool _turningOn;
    private string? _lastFailure;

    /// <param name="settings">The live settings instance the app's crash policy reads.</param>
    /// <param name="bringUp">The shared publish → launch → persist → Run-key → poll sequence.</param>
    /// <param name="persistEnabled">The sanctioned supervision.enabled write path
    /// (<see cref="SupervisionEnabledWriter.Persist"/> in production, never the App writer).</param>
    /// <param name="supervisorInstalled">The Supervisor exe probe (the row's availability).</param>
    /// <param name="reconcileRunKey">
    /// Applies the supervised launch-at-logon policy. Reads the live flag, so the same delegate
    /// serves bring-up (flag already true) and turn-off (flag already false).
    /// </param>
    /// <param name="runInBackground">Runs work off the dispatcher.</param>
    /// <param name="postToUi">Marshals back onto the dispatcher.</param>
    public SupervisionToggleService(
        EyeCtrlSettings settings,
        SupervisorBringUp bringUp,
        Action<bool> persistEnabled,
        Func<bool> supervisorInstalled,
        Action reconcileRunKey,
        Action<Action> runInBackground,
        Action<Action> postToUi)
    {
        _settings = settings;
        _bringUp = bringUp;
        _persistEnabled = persistEnabled;
        _supervisorInstalled = supervisorInstalled;
        _reconcileRunKey = reconcileRunKey;
        _runInBackground = runInBackground;
        _postToUi = postToUi;
    }

    /// <summary>Raised whenever the row needs rebuilding (request start and completion).</summary>
    public event Action? StateChanged;

    /// <summary>Whether the Supervisor executable is present; without it the row is unavailable.</summary>
    public bool IsAvailable => _supervisorInstalled();

    /// <summary>The live setting; the row's toggle and the crash policy both read it.</summary>
    public bool Enabled => _settings.Supervision.Enabled;

    /// <summary>True while a turn-on or turn-off is running; the toggle stays put until it completes.</summary>
    public bool InFlight => _inFlight;

    /// <summary>The row's status line: progress, the last failure, or what the current state means.</summary>
    public string StatusText
    {
        get
        {
            if (_inFlight)
            {
                return _turningOn
                    ? "Turning on\u2026 this can take up to 15 seconds."
                    : "Turning off\u2026";
            }

            if (_lastFailure is not null)
            {
                return _lastFailure;
            }

            // The off text describes the world where the Supervisor re-reads the flag at every
            // recovery decision and stands down when it is false (ticket 06, which merges before
            // this row): nothing restarts EyeCtrl after a crash, exactly as "Off" says.
            return _settings.Supervision.Enabled
                ? "On \u2014 EyeCtrl restarts automatically if it stops responding or crashes."
                : "Off \u2014 if EyeCtrl stops responding or crashes, it is not restarted automatically.";
        }
    }

    /// <summary>
    /// Starts supervised lifecycle in place: the shared bring-up, with rollback to off if the
    /// supervisor does not answer. Returns immediately; the outcome arrives via
    /// <see cref="StateChanged"/>.
    /// </summary>
    public void RequestEnable()
    {
        if (_inFlight || _settings.Supervision.Enabled)
        {
            return;
        }

        if (!_supervisorInstalled())
        {
            _lastFailure = "Automatic crash recovery is unavailable: the EyeCtrl Supervisor is not installed.";
            StateChanged?.Invoke();
            return;
        }

        StartRequest(turningOn: true, () =>
        {
            try
            {
                return _bringUp.TryBringUp() switch
                {
                    SupervisorBringUpOutcome.Reachable => (string?)null,
                    SupervisorBringUpOutcome.StartedNotReachable => RollBack(
                        "The Supervisor started but did not answer, so supervision was turned off again."),
                    // LaunchFailed: no process, nothing persisted — nothing to roll back.
                    _ => "The Supervisor could not be started, so supervision is off.",
                };
            }
            catch (Exception ex)
            {
                // The persist step can throw; the live flag may already be true, so disarm it.
                // The row gets a short message — ex.Message can carry a full path.
                Serilog.Log.Warning(ex, "Enabling supervision failed while saving settings.");
                return RollBack("Could not save supervision settings; supervision is off.");
            }
        });
    }

    /// <summary>
    /// Turns supervised lifecycle off for the next start: persists false through the sanctioned
    /// path and restores self-registration. A supervisor process that is already running is not
    /// stopped; it re-reads the flag at its next recovery decision and stands down, so nothing
    /// restarts or kills EyeCtrl once Off. Returns immediately; the outcome arrives via
    /// <see cref="StateChanged"/>.
    /// </summary>
    public void RequestDisable()
    {
        if (_inFlight || !_settings.Supervision.Enabled)
        {
            return;
        }

        StartRequest(turningOn: false, () =>
        {
            // Persist first, in its own try. If the WRITE fails, disk still says enabled and the
            // live flag must keep matching it — the crash policy must describe the recovery that
            // actually exists. Only once the write lands may the session stand down; a reconcile
            // failure past that point must NOT re-arm the flag (disk says unsupervised).
            try
            {
                _persistEnabled(false);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Persisting supervision.enabled=false failed.");
                return "Could not save supervision settings; supervision is still on.";
            }

            _settings.Supervision.Enabled = false;

            try
            {
                _reconcileRunKey();
                return null;
            }
            catch (Exception ex)
            {
                // Supervision IS off (the persist landed); only launch-at-logon is stale. The
                // user is told, and the flag stays off because that is what disk says.
                Serilog.Log.Warning(ex, "Reconciling launch-at-logon after turn-off failed.");
                return "Supervision is off, but launch-at-logon could not be updated.";
            }
        });
    }

    private void StartRequest(bool turningOn, Func<string?> work)
    {
        _inFlight = true;
        _turningOn = turningOn;
        _lastFailure = null;
        StateChanged?.Invoke();   // called on the UI thread: show progress before the wait

        _runInBackground(() =>
        {
            var failure = work();
            _postToUi(() =>
            {
                _inFlight = false;
                _lastFailure = failure;
                StateChanged?.Invoke();
            });
        });
    }

    /// <summary>Rolls a failed turn-on back to off on every layer the bring-up touched.</summary>
    private string RollBack(string failure)
    {
        _settings.Supervision.Enabled = false;

        // Best effort, and deliberately independent: if the write that failed is still failing,
        // the in-memory flag is the one that must not stay armed — the crash policy reads it live.
        try { _persistEnabled(false); }
        catch (Exception ex) { Serilog.Log.Warning(ex, "Rolling supervision.enabled back to false failed."); }

        try { _reconcileRunKey(); }
        catch (Exception ex) { Serilog.Log.Warning(ex, "Reconciling launch-at-logon after a failed supervision turn-on failed."); }

        return failure;
    }
}
