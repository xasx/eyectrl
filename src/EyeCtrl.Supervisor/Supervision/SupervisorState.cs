namespace EyeCtrl.Supervisor.Supervision;

public enum SupervisorState
{
    IdleWatch, Launching, Supervising, Recovering, FallenBack, CleanExitPending
}

public enum SupervisorCommand
{
    None, LaunchApp, ForceKill, LaunchFallbackAt, NotifyCleanExit
}
