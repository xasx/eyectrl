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
    StandDown
}
