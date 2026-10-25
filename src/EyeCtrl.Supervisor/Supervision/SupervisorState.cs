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
    /// retried app can reach the Supervisor; it goes straight to Supervising on its first
    /// verified heartbeat (A7 fixup-01/S1: the retry leaves the machine in Launching).</summary>
    StartListening,
    /// <summary>A7 fixup-01 (S1): the FallenBack retry launched the app itself, so a deferred
    /// recovery relaunch still queued from the escalation must be dropped — otherwise the next
    /// clock tick would launch a SECOND instance behind the retry, and the two would race the
    /// app's single-instance mutex (the loser's exit books a strike against the survivor).</summary>
    ClearPendingLaunch
}
