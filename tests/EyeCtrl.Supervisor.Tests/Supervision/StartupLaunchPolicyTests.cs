namespace EyeCtrl.Supervisor.Tests.Supervision;

using EyeCtrl.Supervisor.Supervision;
using NFluent;
using Xunit;

/// <summary>
/// Ticket 06-A: the startup-launch decision. A supervisor started while EyeCtrl is already
/// running must not launch a second instance (04-review F1: the duplicate dies on the app's
/// single-instance mutex and every exit books a strike, so 4 strikes reach FallenBack/TD Control
/// over a healthy EyeCtrl).
/// </summary>
public class StartupLaunchPolicyTests
{
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, false)]   // supervised but StartOnBoot off: stay IdleWatch to adopt
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, true)]     // the configured logon launch
    [InlineData(true, true, true, false)]     // the F1 case: already running -> adopt, do not launch
    public void ShouldLaunchAtStartup_Table(bool enabled, bool startOnBoot, bool appAlreadyRunning, bool expected) =>
        Check.That(StartupLaunchPolicy.ShouldLaunchAtStartup(enabled, startOnBoot, appAlreadyRunning)).IsEqualTo(expected);
}
