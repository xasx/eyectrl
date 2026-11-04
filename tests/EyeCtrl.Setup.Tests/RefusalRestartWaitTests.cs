namespace EyeCtrl.Setup.Tests;

using EyeCtrl.Setup;
using EyeCtrl.Setup.Abstractions;
using EyeCtrl.Setup.Tests.TestSupport;

/// <summary>
/// S6f M4: on a refusal, the App that handed off to Setup is still in <c>OnExit</c>. Setup must
/// wait, bounded, for it to go before deciding whether to restart EyeCtrl; a single snapshot saw
/// it, started nothing, and EyeCtrl vanished.
/// </summary>
public class RefusalRestartWaitTests
{
    private static SetupHarness BlockedUpdate()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("1.0.0");
        harness.SeedPackage(@"C:\inbox\2.0.0", "2.0.0");
        harness.SetPolicy(5);
        return harness;
    }

    [Fact]
    public void AHandingOffApp_ThatExits_IsWaitedFor_ThenTheInstalledVersionStarts()
    {
        var harness = BlockedUpdate();
        var appPid = harness.Processes.Add(harness.Paths.AppExePath);

        var result = harness.Workflow.Update(@"C:\inbox\2.0.0");

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Blocked);
        Check.That(harness.Processes.WaitTimeouts).ContainsExactly(TimeSpan.FromSeconds(15));
        Check.That(harness.Processes.Log).Contains("wait:" + appPid);

        // wait first, then exactly one start (the App the wait made room for).
        var waitAt = harness.Processes.Log.IndexOf("wait:" + appPid);
        var startAt = harness.Processes.Log.FindIndex(
            entry => entry.StartsWith("start:" + harness.Paths.AppExePath, StringComparison.Ordinal));
        Check.That(waitAt).IsStrictlyLessThan(startAt);
        Check.That(harness.Processes.Starts).HasSize(1);
        Check.That(harness.Processes.Processes.Count(p =>
            p.ImagePath == harness.Paths.AppExePath)).IsEqualTo(1);
    }

    [Fact]
    public void AHandingOffApp_ThatNeverExits_IsNotStartedASecondTime()
    {
        var harness = BlockedUpdate();
        var appPid = harness.Processes.Add(harness.Paths.AppExePath);
        harness.Processes.WaitForExitResult = false;

        var result = harness.Workflow.Update(@"C:\inbox\2.0.0");

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Blocked);
        Check.That(harness.Processes.WaitTimeouts).ContainsExactly(TimeSpan.FromSeconds(15));
        Check.That(harness.Processes.Log).Contains("wait:" + appPid);
        Check.That(harness.Processes.Starts).IsEmpty();
        Check.That(harness.Processes.Processes.Count(p =>
            p.ImagePath == harness.Paths.AppExePath)).IsEqualTo(1);
        Check.That(harness.Logging.Lines.Any(line =>
            line.Contains("not starting a second instance", StringComparison.Ordinal))).IsTrue();
    }

    [Fact]
    public void NoHandingOffApp_StartsWithoutWaiting()
    {
        var harness = BlockedUpdate();

        var result = harness.Workflow.Update(@"C:\inbox\2.0.0");

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Blocked);
        Check.That(harness.Processes.WaitTimeouts).IsEmpty();
        Check.That(harness.Processes.Starts).HasSize(1);
        Check.That(harness.Processes.Starts[0].ExePath).IsEqualTo(harness.Paths.AppExePath);
    }

    [Fact]
    public void ARefusedElevatedRollback_AlsoWaitsForTheHandingOffApp()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("1.0.0");
        harness.SeedPrevious("0.9.0");
        var appPid = harness.Processes.Add(harness.Paths.AppExePath);
        harness.Broker.Handler = request => request.Job == ElevatedJob.ApplyRollback
            ? new ElevatedPhaseResult(ElevatedPhaseStatus.Blocked, "elevationPolicy")
            : harness.Elevated.Execute(request);

        var result = harness.Workflow.Rollback();

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Blocked);
        Check.That(harness.Processes.Log).Contains("wait:" + appPid);
        Check.That(harness.Processes.Starts).HasSize(1);
        Check.That(harness.Processes.Starts[0].ExePath).IsEqualTo(harness.Paths.AppExePath);
    }
}
