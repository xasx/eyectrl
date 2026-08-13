namespace EyeCtrl.App.Tests.Startup;

using EyeCtrl.App.Startup;

public class InstanceStartupPolicyTests
{
    [Fact]
    public void NoId_RunsAsTheDefaultInstance()
    {
        var d = InstanceStartupPolicy.Evaluate(rawId: null, isTestMode: false, isRestartHandoff: false);

        Check.That(d.Outcome).IsEqualTo(InstanceStartupOutcome.RunDefault);
        Check.That(d.InstanceId).IsNull();
        Check.That(d.CanLog).IsTrue();
    }

    [Fact]
    public void BlankId_RunsAsTheDefaultInstance()
    {
        var d = InstanceStartupPolicy.Evaluate("   ", isTestMode: false, isRestartHandoff: false);

        Check.That(d.Outcome).IsEqualTo(InstanceStartupOutcome.RunDefault);
    }

    [Fact]
    public void ValidId_WithTestMode_RunsScoped()
    {
        var d = InstanceStartupPolicy.Evaluate("run7", isTestMode: true, isRestartHandoff: false);

        Check.That(d.Outcome).IsEqualTo(InstanceStartupOutcome.RunScoped);
        Check.That(d.InstanceId).IsEqualTo("run7");
    }

    [Fact]
    public void MalformedId_Refuses_AndCannotLog()
    {
        var d = InstanceStartupPolicy.Evaluate("bad id", isTestMode: true, isRestartHandoff: false);

        Check.That(d.Outcome).IsEqualTo(InstanceStartupOutcome.Refuse);
        Check.That(d.CanLog).IsFalse();      // no legal profile dir => no scoped log dir
        Check.That(d.Reason!).Contains("EYECTRL_INSTANCE_ID");
    }

    [Fact]
    public void MalformedId_CarriesNoInstanceId_SoNoCallerCanDeriveAPathFromIt()
    {
        // CanLog: false is the instruction; a null InstanceId is what makes it hard to disobey.
        // If the malformed id leaked through, AppPaths.Resolve would throw on it -- during the
        // refusal path, turning a clean exit 78 into an unhandled startup crash.
        var d = InstanceStartupPolicy.Evaluate("bad id", isTestMode: true, isRestartHandoff: false);

        Check.That(d.InstanceId).IsNull();
    }

    [Fact]
    public void ValidId_WithoutTestMode_Refuses_AndCanLog()
    {
        // The concurrency boundary: --test-mode forces MouseGazeProvider, so scoping cannot be used
        // to put two instances on one eye tracker.
        var d = InstanceStartupPolicy.Evaluate("run7", isTestMode: false, isRestartHandoff: false);

        Check.That(d.Outcome).IsEqualTo(InstanceStartupOutcome.Refuse);
        Check.That(d.CanLog).IsTrue();       // the id is valid, so a scoped log dir exists
        Check.That(d.Reason!).Contains("--test-mode");
    }

    [Fact]
    public void ValidId_WithRestartHandoff_Refuses()
    {
        var d = InstanceStartupPolicy.Evaluate("run7", isTestMode: true, isRestartHandoff: true);

        Check.That(d.Outcome).IsEqualTo(InstanceStartupOutcome.Refuse);
        Check.That(d.CanLog).IsTrue();
        Check.That(d.Reason!).Contains("restart");
    }

    [Fact]
    public void MalformedId_TakesPrecedenceOverTheOtherRefusals()
    {
        // Order matters: a malformed id must be reported as such, because it is the only refusal
        // that must not initialise Serilog.
        var d = InstanceStartupPolicy.Evaluate("bad id", isTestMode: false, isRestartHandoff: true);

        Check.That(d.Outcome).IsEqualTo(InstanceStartupOutcome.Refuse);
        Check.That(d.CanLog).IsFalse();
    }

    [Fact]
    public void RefusalExitCode_IsEXCONFIG()
    {
        Check.That(InstanceStartupPolicy.RefusalExitCode).IsEqualTo(78);
    }

    [Fact]
    public void UnscopedHandoff_IsAllowed()
    {
        // A restart handoff on the DEFAULT instance is legitimate and must not be refused.
        var d = InstanceStartupPolicy.Evaluate(null, isTestMode: false, isRestartHandoff: true);

        Check.That(d.Outcome).IsEqualTo(InstanceStartupOutcome.RunDefault);
    }

    [Fact]
    public void RunScoped_CarriesTheTrimmedId_NotTheRawValue()
    {
        // The id is used to build a mutex name, a pipe name and a directory segment. A caller that
        // took the raw value would bind a name with whitespace in it.
        var d = InstanceStartupPolicy.Evaluate("  run7  ", isTestMode: true, isRestartHandoff: false);

        Check.That(d.Outcome).IsEqualTo(InstanceStartupOutcome.RunScoped);
        Check.That(d.InstanceId).IsEqualTo("run7");
    }

    [Fact]
    public void EveryRefusal_ExplainsItself()
    {
        // A refusal exits 78 with nothing on screen -- the reason is the only diagnostic there is.
        InstanceStartupDecision[] refusals =
        [
            InstanceStartupPolicy.Evaluate("bad id", isTestMode: true, isRestartHandoff: false),
            InstanceStartupPolicy.Evaluate("run7", isTestMode: false, isRestartHandoff: false),
            InstanceStartupPolicy.Evaluate("run7", isTestMode: true, isRestartHandoff: true),
        ];

        foreach (var d in refusals)
        {
            Check.That(d.Outcome).IsEqualTo(InstanceStartupOutcome.Refuse);
            Check.That(d.Reason).IsNotNull();
            Check.That(d.Reason!).IsNotEmpty();
        }
    }

    [Fact]
    public void NonRefusals_CarryNoReason()
    {
        Check.That(InstanceStartupPolicy.Evaluate(null, false, false).Reason).IsNull();
        Check.That(InstanceStartupPolicy.Evaluate("run7", true, false).Reason).IsNull();
    }
}
