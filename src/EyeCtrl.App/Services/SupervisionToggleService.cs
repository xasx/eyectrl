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

            // The off text says plainly what happens to a supervisor that is already running:
            // it was adopted into Supervising and does not consult the setting again, so it
            // keeps watching until EyeCtrl exits cleanly — then it stays idle in the background.
            return _settings.Supervision.Enabled
                ? "On \u2014 EyeCtrl restarts automatically if it stops responding or crashes."
                : "Off \u2014 a Supervisor that is already running keeps watching until EyeCtrl exits, "
                    + "then stays idle. If EyeCtrl crashes first, nothing restarts it.";
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
                return RollBack("Could not save supervision settings: " + ex.Message);
            }
        });
    }

    /// <summary>
    /// Turns supervised lifecycle off for the next start: persists false through the sanctioned
    /// path and restores self-registration. The running supervisor is not stopped — see
    /// <see cref="StatusText"/> for what that means. Returns immediately; the outcome arrives
    /// via <see cref="StateChanged"/>.
    /// </summary>
    public void RequestDisable()
    {
        if (_inFlight || !_settings.Supervision.Enabled)
        {
            return;
        }

        StartRequest(turningOn: false, () =>
        {
            try
            {
                _settings.Supervision.Enabled = false;
                _persistEnabled(false);
                _reconcileRunKey();
                return null;
            }
            catch (Exception ex)
            {
                // The on-disk flag was not changed, so keep the live flag matching it: the
                // crash policy must describe the recovery that actually exists.
                _settings.Supervision.Enabled = true;
                return "Could not save supervision settings: " + ex.Message;
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
