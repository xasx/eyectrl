namespace EyeCtrl.Supervisor.Tests;

using EyeCtrl.Core.Services;

/// <summary>
/// D2 of fixup 01: the Supervisor forwards <c>--real-profile</c> whenever IT resolved non-dev —
/// installed by location, or real by flag — so the App it starts resolves the same profile. A dev
/// supervisor stays silent. (The flag-on-argv mechanism itself is pinned by
/// <c>ProcessControllerTests.Launch_WithRealProfile_ForwardsTheFlagToTheApp</c>.)
/// </summary>
public class ProfileForwardingTests
{
    private static ProfileLocation Resolve(string exePath, params string[] args) =>
        ProfileLocation.Resolve(
            exePath, isDebugBuild: false, args,
            @"C:\Program Files", @"C:\Users\test\AppData\Roaming", @"C:\Users\test\AppData\Local");

    [Fact]
    public void AnInstalledSupervisor_ForwardsTheFlag()
    {
        var installed = Resolve(@"C:\Program Files\EyeCtrl\EyeCtrl.Supervisor.exe");

        Check.That(EyeCtrl.Supervisor.Program.ShouldForwardRealProfile(installed)).IsTrue();
    }

    [Fact]
    public void ARealByFlagSupervisor_ForwardsTheFlag()
    {
        var flagged = Resolve(@"C:\w\bin\EyeCtrl.Supervisor.exe", "--real-profile");

        Check.That(EyeCtrl.Supervisor.Program.ShouldForwardRealProfile(flagged)).IsTrue();
    }

    [Fact]
    public void ADevSupervisor_ForwardsNothing()
    {
        var dev = Resolve(@"C:\w\bin\EyeCtrl.Supervisor.exe");

        Check.That(EyeCtrl.Supervisor.Program.ShouldForwardRealProfile(dev)).IsFalse();
    }
}
