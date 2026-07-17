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
public sealed record StartupGraceElapsed(DateTime At) : SupervisionInput(At); // 60s in LAUNCHING with no heartbeat -> RECOVERING (spec failure mode #2)
public sealed record FallenBackRetry(DateTime At) : SupervisionInput(At);    // user clicked "Retry EyeCtrl once" in the FALLEN_BACK dialog -> IdleWatch (re-adopt)
