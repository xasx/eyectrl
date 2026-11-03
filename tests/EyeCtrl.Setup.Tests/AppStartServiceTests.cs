namespace EyeCtrl.Setup.Tests;

using EyeCtrl.Core.Input;
using EyeCtrl.Setup.Install;
using EyeCtrl.Setup.Tests.TestSupport;

public class AppStartServiceTests
{
    private static SetupHarness Harness() => new();

    [Fact]
    public void HighPlusUiAccess_AliveAt20s_AndTheTokenLine_Pass()
    {
        var harness = Harness();
        var started = harness.Clock.UtcNow;

        var outcome = harness.AppStart.StartAndCheck(harness.Paths.AppExePath);

        Check.That(outcome.Passed).IsTrue();
        Check.That(outcome.ProcessId).IsNotNull();
        Check.That(harness.Clock.UtcNow - started >= TimeSpan.FromSeconds(20)).IsTrue();
        Check.That(harness.LogProbe.Calls).HasSize(1);
    }

    [Fact]
    public void WithoutTheUiAccessBit_TheStartFailsAndTheProcessIsKilled()
    {
        var harness = Harness();
        harness.Processes.TokenReader = _ =>
            new ProcessTokenFacts("EyeCtrl.App.exe", 0x3000, false, false, false);

        var outcome = harness.AppStart.StartAndCheck(harness.Paths.AppExePath);

        Check.That(outcome.Passed).IsFalse();
        Check.That(outcome.FailureReason).IsEqualTo(AppStartFailure.TokenTimeout);
        Check.That(harness.Log.Any(e => e.StartsWith("kill:", StringComparison.Ordinal))).IsTrue();
    }

    [Fact]
    public void AMediumToken_FailsEvenWithTheUiAccessBit()
    {
        var harness = Harness();
        harness.Processes.TokenReader = _ =>
            new ProcessTokenFacts("EyeCtrl.App.exe", 0x2000, false, true, false);

        var outcome = harness.AppStart.StartAndCheck(harness.Paths.AppExePath);

        Check.That(outcome.FailureReason).IsEqualTo(AppStartFailure.TokenTimeout);
    }

    [Fact]
    public void AnAppThatExitsBeforeTheTokenRead_FailsFast()
    {
        var harness = Harness();
        harness.Processes.AliveReader = _ => false;

        var outcome = harness.AppStart.StartAndCheck(harness.Paths.AppExePath);

        Check.That(outcome.FailureReason).IsEqualTo(AppStartFailure.ExitedBeforeToken);
    }

    [Fact]
    public void AnAppThatDiesBefore20s_FailsTheAliveCheck()
    {
        var harness = Harness();
        var started = harness.Clock.UtcNow;
        harness.Processes.AliveReader = _ => harness.Clock.UtcNow < started.AddSeconds(19);

        var outcome = harness.AppStart.StartAndCheck(harness.Paths.AppExePath);

        Check.That(outcome.FailureReason).IsEqualTo(AppStartFailure.NotAliveAfter20s);
    }

    [Fact]
    public void AMissingTokenLine_FailsThePassRule()
    {
        var harness = Harness();
        harness.LogProbe.Result = false;

        var outcome = harness.AppStart.StartAndCheck(harness.Paths.AppExePath);

        Check.That(outcome.FailureReason).IsEqualTo(AppStartFailure.MissingTokenLine);
        Check.That(harness.Log.Any(e => e.StartsWith("kill:", StringComparison.Ordinal))).IsTrue();
    }

    [Fact]
    public void AFailedStart_ReportsNotStarted()
    {
        var harness = Harness();
        harness.Processes.StartSucceeds = false;

        var outcome = harness.AppStart.StartAndCheck(harness.Paths.AppExePath);

        Check.That(outcome.FailureReason).IsEqualTo(AppStartFailure.NotStarted);
        Check.That(harness.Clock.Sleeps).IsEmpty();
    }

    [Fact]
    public void TryStart_StartsOnceWithoutThePassRule()
    {
        var harness = Harness();

        var started = harness.AppStart.TryStart(harness.Paths.AppExePath);

        Check.That(started).IsTrue();
        Check.That(harness.LogProbe.Calls).IsEmpty();
    }
}
