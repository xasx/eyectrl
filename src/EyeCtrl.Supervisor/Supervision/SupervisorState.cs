namespace EyeCtrl.Supervisor.Supervision;

public enum SupervisorState
{
    IdleWatch, Launching, Supervising, Recovering, FallenBack, CleanExitPending
}

public enum SupervisorCommand
{
    None, LaunchApp, ForceKill, LaunchFallbackAt, NotifyCleanExit,
    /// <summary>Supervision is off on disk (ticket 06-B): release the process and go idle without
    /// killing or relaunching.</summary>
    StandDown,
    /// <summary>Entering FallenBack (ticket 07 review F2): the Supervisor has given up, so close
    /// the heartbeat pipe — deliveries then fail, the app's presence gate stays standalone, and
    /// the row tells the truth instead of promising a respawn that will not come.</summary>
    StopListening,
    /// <summary>The dialog's Retry (ticket 07 review F2): reopen the heartbeat pipe so the
    /// retried app can be adopted on its first heartbeat.</summary>
    StartListening
}
