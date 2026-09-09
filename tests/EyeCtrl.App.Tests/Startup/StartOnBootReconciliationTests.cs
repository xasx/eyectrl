namespace EyeCtrl.App.Tests.Startup;

using EyeCtrl.App.Startup;

public class StartOnBootReconciliationTests
{
    [Theory]
    // Supervised AND the supervisor demonstrably owns launch-at-logon -> hand over, don't self-register.
    [InlineData(true, true, true, false)]
    [InlineData(true, false, true, false)]
    // Supervised but NOTHING owns launch-at-logon -> keep self-registering. Handing over to a
    // mechanism that was never installed deletes the only thing that starts the app.
    [InlineData(true, true, false, true)]
    [InlineData(true, false, false, false)]  // StartOnBoot off: still nothing to register
    // Standalone -> the supervisor's ownership is irrelevant.
    [InlineData(false, true, true, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, false)]
    public void DecidesSelfRegistration(
        bool supervised, bool startOnBoot, bool supervisorOwnsLaunchAtLogon, bool expectRegister)
    {
        Check.That(StartOnBootPolicy.ShouldSelfRegister(supervised, startOnBoot, supervisorOwnsLaunchAtLogon))
            .IsEqualTo(expectRegister);
    }

    [Fact]
    public void SupervisedWithoutAnInstalledTask_KeepsTheRunKey()
    {
        // The lockout this signature exists to prevent. Before the third parameter, going
        // supervised unconditionally returned false here, and the caller translates false into
        // "delete the HKCU Run value". Nothing in the repo creates the Scheduled Task meant to
        // replace it, so the next logon started nothing at all — and a user whose only input
        // channel is this app cannot start it by hand.
        Check.That(StartOnBootPolicy.ShouldSelfRegister(
            supervised: true, startOnBoot: true, supervisorOwnsLaunchAtLogon: false)).IsTrue();
    }

    [Fact]
    public void NoSupervisorLaunchRegistrationExistsYet()
    {
        // Pins the honest answer rather than the aspirational one: there is no installer and no
        // Scheduled Task (Supervisor/Program.cs "TODO(installer)"; SupervisorSetupWindow's own
        // "Scheduled Task install is a separate step"). When an installer lands, whoever wires
        // the real probe changes this test deliberately — which is the point of pinning it.
        Check.That(SupervisorLaunchRegistration.IsRegistered).IsFalse();
    }
}
