namespace EyeCtrl.Setup.Tests;

using EyeCtrl.Core.Services;
using EyeCtrl.Setup;
using EyeCtrl.Setup.Abstractions;
using EyeCtrl.Setup.Tests.TestSupport;

/// <summary>
/// F4: repair and update must end in a version that passed the rule, or in a revert to the
/// previous version that is checked the same way. Also pins F10's M1/M2 mutants.
/// </summary>
public class PassRuleEndingsTests
{
    [Fact]
    public void Repair_WhenThePromotedVersionFails_RevertsAndChecksThePrevious()
    {
        var harness = new SetupHarness();
        harness.SeedPrevious("1.0.0");
        harness.SeedStaging("2.0.0");
        harness.Registry.SeedString(
            Microsoft.Win32.RegistryHive.CurrentUser, SetupConstants.RunOnceKeyPath,
            SetupConstants.RepairRunOnceName, "\"run\" repair");
        harness.LogProbe.Results.Enqueue(false); // repaired 2.0 fails
        harness.LogProbe.Results.Enqueue(true);  // previous 1.0 passes

        var result = harness.Workflow.Repair();

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.RolledBack);
        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot).Version).IsEqualTo("1.0.0");
        Check.That(harness.Metadata.Read(harness.Paths.FailedRoot).Version).IsEqualTo("2.0.0");
        Check.That(harness.Broker.Requests.Any(r => r.Job == ElevatedJob.Revert)).IsTrue();
    }

    [Fact]
    public void Repair_WhenBothVersionsFail_ReportsAllFailed()
    {
        var harness = new SetupHarness();
        harness.SeedPrevious("1.0.0");
        harness.SeedStaging("2.0.0");
        harness.LogProbe.Result = false;

        var result = harness.Workflow.Repair();

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.AllFailed);
        Check.That(harness.Broker.Requests.Any(r => r.Job == ElevatedJob.Revert)).IsTrue();
    }

    [Fact]
    public void APartialApply_IsRepairedAndThenPassChecked()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("1.0.0");
        var package = @"C:\inbox\2.0.0";
        harness.SeedPackage(package, "2.0.0");
        // Move 2 (staging -> install) and its undo (previous -> install) fail: a partial state.
        harness.Files.MoveFailure = index => index is 2 or 3;

        var result = harness.Workflow.Update(package);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Ok);
        Check.That(result.Detail).IsEqualTo("repairCompleted");
        Check.That(harness.Broker.Requests.Any(r =>
            r.Job == ElevatedJob.Repair && r.RunId == "run1")).IsTrue();
        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot).Version).IsEqualTo("2.0.0");
    }

    [Fact]
    public void ARepairedVersionThatFailsThePassRule_RevertsAndChecksTheOldOne()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("1.0.0");
        var package = @"C:\inbox\2.0.0";
        harness.SeedPackage(package, "2.0.0");
        harness.Files.MoveFailure = index => index is 2 or 3;
        harness.LogProbe.Results.Enqueue(false); // repaired 2.0
        harness.LogProbe.Results.Enqueue(true);  // previous 1.0

        var result = harness.Workflow.Update(package);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.RolledBack);
        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot).Version).IsEqualTo("1.0.0");
    }

    [Fact]
    public void AFailedNewVersion_ReleasesTheHeldButtonOnTheKillPath()
    {
        // F10 M1: Revert must use the kill path even when the App is already dead and only the
        // Supervisor is left. The held-button release is the observable difference.
        var harness = new SetupHarness();
        harness.SeedInstalled("1.0.0");
        var package = @"C:\inbox\2.0.0";
        harness.SeedPackage(package, "2.0.0");
        var supervisorStarted = false;
        harness.Processes.OnStart = path =>
        {
            if (!supervisorStarted
                && string.Equals(path, harness.Paths.AppExePath, StringComparison.OrdinalIgnoreCase))
            {
                supervisorStarted = true;
                harness.Processes.Add(harness.Paths.SupervisorExePath);
            }
        };
        harness.HeldButton.Result = new HeldButtonReleaseResult(true, true, null, false);
        harness.LogProbe.Results.Enqueue(false);
        harness.LogProbe.Results.Enqueue(true);

        var result = harness.Workflow.Update(package);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.RolledBack);
        Check.That(harness.HeldButton.Calls).IsEqualTo(1);
        Check.That(harness.Log.Any(e => e.StartsWith("stop:", StringComparison.Ordinal))).IsTrue();
    }

    [Fact]
    public void ABlockedUpdateWhoseOldVersionCannotPass_ReportsAllFailed()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("1.0.0");
        var package = @"C:\inbox\2.0.0";
        harness.SeedPackage(package, "2.0.0");
        harness.SetPolicy(5);
        harness.LogProbe.Result = false;

        var result = harness.Workflow.Update(package);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.AllFailed);
        Check.That(result.Detail).Contains("restartFailed");
    }
}
