namespace EyeCtrl.Supervisor.Supervision;

/// <summary>
/// Ticket 06-A: whether the supervisor may emit its startup <c>LaunchRequest</c>. A launch while
/// EyeCtrl is already running produces a duplicate that dies on the app's single-instance mutex;
/// every such exit books a strike, so four reach FallenBack/TD Control over a healthy app
/// (ticket 04 review F1). When the app is already running the supervisor stays in IdleWatch and
/// adopts it on its first heartbeat instead.
/// </summary>
internal static class StartupLaunchPolicy
{
    public static bool ShouldLaunchAtStartup(bool enabled, bool startOnBoot, bool appAlreadyRunning) =>
        enabled && startOnBoot && !appAlreadyRunning;
}
