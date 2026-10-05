namespace EyeCtrl.Supervisor.Supervision;

/// <summary>
/// The caretaker-facing phrase for each <see cref="SupervisorState"/>, shared by the tray tooltip and the
/// setup window's status line. Deliberately exhaustive: adding a state must break at the mapping (the
/// discard arm throws <see cref="ArgumentOutOfRangeException"/>) rather than fall back to a phrase that
/// names the wrong state.
/// </summary>
internal static class SupervisorStateText
{
    /// <summary>The tray tooltip's prefix. The shell truncates <c>NotifyIcon.Text</c> past 63 characters
    /// (<see cref="UI.SupervisorTrayIcon.SetStatusText"/>), so the prefix counts toward that budget.</summary>
    public const string TrayPrefix = "EyeCtrl Supervisor: ";

    /// <summary>Returns the bare phrase for <paramref name="state"/>, with the caller adding its own prefix.</summary>
    public static string Describe(SupervisorState state) => state switch
    {
        SupervisorState.IdleWatch => "idle",
        SupervisorState.Launching => "starting EyeCtrl",
        SupervisorState.Supervising => "watching EyeCtrl",
        SupervisorState.Recovering => "restarting EyeCtrl",
        SupervisorState.CleanExitPending => "EyeCtrl is closing",
        SupervisorState.FallenBack => "EyeCtrl stopped — backup program running",
        _ => throw new ArgumentOutOfRangeException(
            nameof(state), state, "No caretaker phrase for this SupervisorState."),
    };
}
