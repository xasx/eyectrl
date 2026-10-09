namespace EyeCtrl.Supervisor.Supervision;

using EyeCtrl.Core.Localization;

/// <summary>
/// The caretaker-facing phrase for each <see cref="SupervisorState"/>, shared by the tray tooltip and the
/// setup window's status line. Deliberately exhaustive: adding a state must break at the mapping (the
/// discard arm throws <see cref="ArgumentOutOfRangeException"/>) rather than fall back to a phrase that
/// names the wrong state.
/// </summary>
/// <remarks>
/// The phrases live on the <c>supervisor</c> surface (T06); the table is a parameter, not the
/// process global, because the Supervisor never activates <see cref="StringTable.Current"/> — each
/// surface resolves its own table through <c>SupervisorLanguageResolver</c>.
/// </remarks>
internal static class SupervisorStateText
{
    /// <summary>The tray tooltip's prefix. The shell truncates <c>NotifyIcon.Text</c> past 63 characters
    /// (<see cref="UI.SupervisorTrayIcon.SetStatusText"/>), so the prefix counts toward that budget.</summary>
    /// <param name="strings">The resolved Supervisor table.</param>
    /// <returns>The prefix the tooltip is composed from.</returns>
    public static string TrayPrefix(StringTable strings) => strings.T("supervisor.tray.prefix");

    /// <summary>Returns the bare phrase for <paramref name="state"/>, with the caller adding its own prefix.</summary>
    /// <param name="strings">The resolved Supervisor table.</param>
    /// <param name="state">The state to name.</param>
    /// <returns>The phrase for the state.</returns>
    public static string Describe(StringTable strings, SupervisorState state) => state switch
    {
        SupervisorState.IdleWatch => strings.T("supervisor.state.idle"),
        SupervisorState.Launching => strings.T("supervisor.state.starting"),
        SupervisorState.Supervising => strings.T("supervisor.state.watching"),
        SupervisorState.Recovering => strings.T("supervisor.state.restarting"),
        SupervisorState.CleanExitPending => strings.T("supervisor.state.closing"),
        SupervisorState.FallenBack => strings.T("supervisor.state.fallen-back"),
        _ => throw new ArgumentOutOfRangeException(
            nameof(state), state, "No caretaker phrase for this SupervisorState."),
    };
}
