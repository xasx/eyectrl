namespace EyeCtrl.App.Services;

using System;
using EyeCtrl.Setup;

/// <summary>
/// How one uninstall hand-off ended, as Settings must react to it (C9 S6e fixup-08). The owner's
/// 06.11 decision: Settings hides while Setup's own Q8 is up and comes back on every outcome that
/// does not remove EyeCtrl — cancel, „Nein, behalten", Esc, close, the five-minute timeout, a
/// refused or a failed run. Only <see cref="Uninstalled" /> leaves it hidden, because Setup then
/// ends the App as today.
/// </summary>
public enum UninstallHandoffEnd
{
    /// <summary>Setup recorded <see cref="SetupOutcome.Uninstalled" />: EyeCtrl is being removed,
    /// so Settings must stay hidden.</summary>
    Uninstalled,

    /// <summary>Setup recorded a terminal outcome for this uninstall that is not „uninstalled" —
    /// the owner cancelled or kept the data, the policy refused it, or the run failed.</summary>
    NotUninstalled,

    /// <summary>
    /// No outcome could be established: Setup vanished without writing a record, the record could
    /// not be read, the watcher faulted, or the watch hit its cap. Treated like
    /// <see cref="NotUninstalled" /> — never strand the owner.
    /// </summary>
    Unknown,
}

/// <summary>
/// Watches ONE uninstall hand-off to its end (C9 S6e fixup-08). The hand-off crosses three
/// processes — the installed Setup's launcher, its run copy, and an elevated child — so a PID
/// watch cannot see the end. The watch instead reads Setup's one result record
/// (<c>setup-last.json</c>, the same file the S6f notice reads) and Setup's own single-session
/// mutex, which the launcher holds through the run-copy takeover and the elevated phase.
/// </summary>
public interface IUninstallHandoffWatch
{
    /// <summary>
    /// Starts the watch. <paramref name="ended" /> is called exactly once, on the thread that runs
    /// the schedule seam (the UI thread in production, the caller's thread in a test).
    /// </summary>
    /// <param name="handoffStartedAt">When the hand-off was started, captured BEFORE the launch: a
    /// record older than this belongs to some earlier Setup run.</param>
    /// <param name="ended">Receives the end and Setup's own recorded outcome when there was one
    /// (null for a watcher-established end). Called exactly once — never both a record and a
    /// timeout.</param>
    void Start(
        DateTimeOffset handoffStartedAt, Action<UninstallHandoffEnd, SetupOutcome?> ended);
}

/// <summary>Creates a fresh one-shot watch per hand-off.</summary>
public interface IUninstallHandoffWatchFactory
{
    /// <summary>Creates the watch for one hand-off.</summary>
    IUninstallHandoffWatch Create();
}

/// <summary>
/// The Settings side of the uninstall hand-off (C9 S6e fixup-08). Settings is an ordinary window
/// but still topmost, and (since #316) it sits in ZBID_UIACCESS; Setup runs with
/// <c>uiAccess="false"</c>, so its Q8 can never be raised above Settings. The only fix is for
/// Settings to get out of the way for the length of Setup's window.
/// </summary>
public interface IUninstallHandoffPresenter
{
    /// <summary>
    /// Starts the uninstall and, only on <see cref="SetupHandoffStart.Started" />, hides Settings
    /// and starts the watch that brings it back on every non-uninstall end. A refusal
    /// (<see cref="SetupHandoffStart.RefusedWhileActive" />) and a failed launch change nothing,
    /// exactly as before fixup-08.
    /// </summary>
    /// <param name="gateways">The Settings page's setup gateways.</param>
    /// <returns>What the hand-off did, for the page's failure line.</returns>
    SetupHandoffStart Start(ISetupGateways gateways);
}

/// <summary>
/// The production presenter: hides Settings only once Setup really started, then hands the wait to
/// a fresh <see cref="IUninstallHandoffWatch" />. Every seam is injected, so the contract
/// (Started hides, Failed/Refused do not, a fault still brings Settings back) is headlessly
/// testable with recorders.
/// </summary>
public sealed class UninstallHandoffPresenter : IUninstallHandoffPresenter
{
    private readonly Func<bool> _hide;
    private readonly Action<UninstallHandoffEnd, SetupOutcome?> _restore;
    private readonly IUninstallHandoffWatchFactory _watches;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Action<string> _warn;

    /// <summary>Creates the presenter.</summary>
    /// <param name="hide">Hides Settings for the hand-off; returns whether it actually hid a
    /// visible window (false means there is nothing to bring back).</param>
    /// <param name="restore">Brings Settings back, or deliberately leaves it hidden for
    /// <see cref="UninstallHandoffEnd.Uninstalled" />. Runs on the UI thread.</param>
    /// <param name="watches">Creates the one-shot watch.</param>
    /// <param name="utcNow">The clock captured before the launch; defaults to UtcNow.</param>
    /// <param name="warn">Receives one machine-readable line when a seam faults; never user text.</param>
    public UninstallHandoffPresenter(
        Func<bool> hide,
        Action<UninstallHandoffEnd, SetupOutcome?> restore,
        IUninstallHandoffWatchFactory watches,
        Func<DateTimeOffset>? utcNow = null,
        Action<string>? warn = null)
    {
        _hide = hide ?? throw new ArgumentNullException(nameof(hide));
        _restore = restore ?? throw new ArgumentNullException(nameof(restore));
        _watches = watches ?? throw new ArgumentNullException(nameof(watches));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _warn = warn ?? (_ => { });
    }

    /// <inheritdoc />
    public SetupHandoffStart Start(ISetupGateways gateways)
    {
        ArgumentNullException.ThrowIfNull(gateways);

        // Before the launch: a record written by this run can never be older than this, so it
        // cannot be confused with the previous run's record (the 05.11 sitting left one behind).
        var startedAt = _utcNow();

        var start = gateways.StartUninstallHandoff();
        if (start != SetupHandoffStart.Started)
            return start;

        bool hidden;
        try
        {
            hidden = _hide();
        }
        catch (Exception ex)
        {
            // Setup is running; a hide that faulted leaves today's behaviour (Settings covers Q8)
            // rather than a half-hidden window, and there is nothing to restore.
            _warn($"uninstall hand-off: hiding Settings failed: {ex.GetType().Name}");
            hidden = false;
        }

        if (!hidden)
            return start;

        try
        {
            _watches.Create().Start(startedAt, _restore);
        }
        catch (Exception ex)
        {
            // Never strand: if the watch cannot even start, Settings comes back at once.
            _warn($"uninstall hand-off: starting the completion watch failed: {ex.GetType().Name}");
            _restore(UninstallHandoffEnd.Unknown, null);
        }

        return start;
    }
}
