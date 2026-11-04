namespace EyeCtrl.Setup.Tests;

using EyeCtrl.Setup;
using EyeCtrl.Setup.Abstractions;
using EyeCtrl.Setup.Tests.TestSupport;
using Microsoft.Win32;

public class RollbackWorkflowTests
{
    [Fact]
    public void Rollback_RestoresThePreviousVersion_AndKeepsTheDisplacedOne()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("2.0.0");
        harness.SeedPrevious("1.0.0");

        var result = harness.Workflow.Rollback();

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Ok);
        Check.That(result.Version).IsEqualTo("1.0.0");
        Check.That(result.PreviousVersion).IsEqualTo("2.0.0");
        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot).Version).IsEqualTo("1.0.0");
        Check.That(harness.Metadata.Read(harness.Paths.PreviousRoot).Version).IsEqualTo("2.0.0");
        Check.That(harness.Registry.Get(
            RegistryHive.LocalMachine, SetupConstants.UninstallKeyPath, "DisplayVersion"))
            .IsEqualTo("1.0.0");
        Check.That(harness.Recorder.TryReadLast()?.Verb).IsEqualTo(SetupVerb.Rollback);
    }

    [Fact]
    public void Rollback_WithoutAPreviousVersion_ChangesNothing()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("2.0.0");

        var result = harness.Workflow.Rollback();

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.NotApplied);
        Check.That(harness.Broker.Requests).IsEmpty();
        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot).Version).IsEqualTo("2.0.0");
    }

    [Fact]
    public void AFailedRollbackTarget_RollsForwardToTheDisplacedVersion()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("2.0.0");
        harness.SeedPrevious("1.0.0");
        harness.LogProbe.Results.Enqueue(AppStartupTokenLine.UiAccessFalse);
        harness.LogProbe.Results.Enqueue(AppStartupTokenLine.UiAccessTrue);

        var result = harness.Workflow.Rollback();

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Failed);
        Check.That(result.Detail).Contains("rollbackTargetFailed");
        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot).Version).IsEqualTo("2.0.0");
        Check.That(result.Version).IsEqualTo("1.0.0");
    }
}

public class UninstallWorkflowTests
{
    [Fact]
    public void ARollbackApplyStillRunning_DoesNotRestartTheApp()
    {
        // H1: the apply-rollback child may still be renaming EyeCtrl and EyeCtrl.previous.
        var harness = new SetupHarness();
        harness.SeedInstalled("2.0.0");
        harness.SeedPrevious("1.0.0");
        harness.Broker.Handler = request => request.Job == ElevatedJob.ApplyRollback
            ? new ElevatedPhaseResult(ElevatedPhaseStatus.Failed, "childStillRunning")
            : harness.Elevated.Execute(request);

        var result = harness.Workflow.Rollback();

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Failed);
        Check.That(result.Detail).IsEqualTo("childStillRunning");
        Check.That(harness.Log.Any(e =>
            e.StartsWith("start:" + harness.Paths.AppExePath, StringComparison.Ordinal))).IsFalse();
    }

    [Fact]
    public void QuietUninstall_AlwaysKeepsTheData_AndNeverAsks()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("1.0.0");
        harness.Processes.Add(harness.Paths.AppExePath);
        harness.Processes.Add(harness.Paths.SupervisorExePath);
        harness.Journal.Write(new EyeCtrl.Setup.Install.InstallJournal(
            EyeCtrl.Setup.Install.InstallOperation.Update, 2));
        harness.Registry.SeedString(
            RegistryHive.CurrentUser, SetupConstants.RunKeyPath, "EyeCtrl", "x");
        harness.Registry.SeedString(
            RegistryHive.CurrentUser, SetupConstants.RunKeyPath, "Other", "y");
        harness.Choice.Choice = UninstallChoice.Delete;

        var result = harness.Workflow.Uninstall(quiet: true, keepData: null);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Uninstalled);
        Check.That(harness.Choice.Calls).IsEqualTo(0);
        Check.That(harness.AppData.Calls).IsEqualTo(0);
        Check.That(harness.Files.DirectoryExists(harness.Paths.InstallRoot)).IsFalse();
        Check.That(harness.Registry.Get(
            RegistryHive.LocalMachine, SetupConstants.UninstallKeyPath, "DisplayName")).IsNull();
        Check.That(harness.Registry.Names(RegistryHive.CurrentUser, SetupConstants.RunKeyPath))
            .Contains("Other");
        Check.That(harness.Registry.Names(RegistryHive.CurrentUser, SetupConstants.RunKeyPath))
            .Not.Contains("EyeCtrl");
        Check.That(harness.Log).Contains("reboot:" + harness.Paths.RunFolder("run1"));
        Check.That(harness.Journal.Read()).IsNull();
    }

    [Fact]
    public void AConfirmedDelete_RemovesTheDataAfterTheProgram()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("1.0.0");
        harness.Choice.Choice = UninstallChoice.Delete;

        var result = harness.Workflow.Uninstall(quiet: false, keepData: null);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Uninstalled);
        Check.That(harness.Choice.Calls).IsEqualTo(1);
        Check.That(harness.AppData.Calls).IsEqualTo(1);
        Check.That(result.Detail).IsEqualTo("deleted");
    }

    [Fact]
    public void Cancel_ChangesNothing()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("1.0.0");
        harness.Choice.Choice = UninstallChoice.Cancel;

        var result = harness.Workflow.Uninstall(quiet: false, keepData: null);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Cancelled);
        Check.That(harness.Broker.Requests).IsEmpty();
        Check.That(harness.Files.DirectoryExists(harness.Paths.InstallRoot)).IsTrue();
        Check.That(harness.AppData.Calls).IsEqualTo(0);
    }

    [Fact]
    public void ABlockedPolicy_LeavesEyeCtrlRunning()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("1.0.0");
        harness.Processes.Add(harness.Paths.AppExePath);
        harness.SetPolicy(5);

        var result = harness.Workflow.Uninstall(quiet: true, keepData: null);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Blocked);
        Check.That(harness.Broker.Requests).IsEmpty();
        Check.That(harness.Processes.Processes.Any(p =>
            p.ImagePath == harness.Paths.AppExePath)).IsTrue();
    }

    [Fact]
    public void AFailedRemoval_KeepsTheDataAndTheUninstallEntry()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("1.0.0");
        harness.AppsAndFeatures.Register("1.0.0");
        harness.Files.FailDeleteAt = 1;

        var result = harness.Workflow.Uninstall(quiet: true, keepData: null);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Failed);
        Check.That(harness.AppData.Calls).IsEqualTo(0);
        Check.That(harness.Registry.Get(
            RegistryHive.LocalMachine, SetupConstants.UninstallKeyPath, "DisplayName"))
            .IsEqualTo("EyeCtrl");
    }
}

public class RepairWorkflowTests
{
    [Fact]
    public void Repair_PromotesStaging_AndStartsTheApp()
    {
        var harness = new SetupHarness();
        harness.SeedPrevious("1.0.0");
        harness.SeedStaging("2.0.0");
        harness.Registry.SeedString(
            RegistryHive.CurrentUser, SetupConstants.RunOnceKeyPath,
            SetupConstants.RepairRunOnceName, "\"run\" repair");

        var result = harness.Workflow.Repair();

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Repaired);
        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot).Version).IsEqualTo("2.0.0");
        Check.That(harness.Registry.Get(
            RegistryHive.CurrentUser, SetupConstants.RunOnceKeyPath,
            SetupConstants.RepairRunOnceName)).IsNull();
        Check.That(harness.Processes.Processes.Any(p =>
            p.ImagePath == harness.Paths.AppExePath)).IsTrue();
    }

    [Fact]
    public void Repair_FromTheRunCopy_SkipsTheRunCopyPreparation()
    {
        var harness = new SetupHarness();
        harness.SeedPrevious("1.0.0");
        harness.Environment.ProcessPath = harness.Paths.RunSetupExePath("old");
        harness.Files.AddFile(harness.Paths.RunSetupExePath("old"));

        var result = harness.Workflow.Repair();

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Repaired);
        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot).Version).IsEqualTo("1.0.0");
        Check.That(harness.Broker.Requests.Select(r => r.Job)).ContainsExactly(ElevatedJob.Repair);
        Check.That(harness.Broker.Requests[0].RunId).IsEqualTo("old");
    }

    [Fact]
    public void Repair_WithNothingToRepair_ReportsAllFailed()
    {
        var harness = new SetupHarness();

        var result = harness.Workflow.Repair();

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.AllFailed);
        Check.That(harness.Broker.Requests).IsEmpty();
    }
}
