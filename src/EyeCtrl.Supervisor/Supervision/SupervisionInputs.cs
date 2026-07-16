namespace EyeCtrl.Supervisor.Supervision;

using System;

public abstract record SupervisionInput(DateTime At);
public sealed record LaunchRequest(DateTime At) : SupervisionInput(At);     // logon+StartOnBoot / tray "Launch now"
public sealed record AdoptRequest(DateTime At) : SupervisionInput(At);      // manual-launch first heartbeat
public sealed record HeartbeatReceived(DateTime At) : SupervisionInput(At);
public sealed record HeartbeatStale(DateTime At) : SupervisionInput(At);
public sealed record GazeStreamDead(DateTime At) : SupervisionInput(At);
public sealed record ProcessExited(DateTime At) : SupervisionInput(At);
public sealed record CleanExitRequested(DateTime At, bool ClientVerified) : SupervisionInput(At);
public sealed record DeadlineElapsed(DateTime At) : SupervisionInput(At);
public sealed record SurvivedWindow(DateTime At) : SupervisionInput(At);    // survived a full crash window -> reset
