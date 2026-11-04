namespace EyeCtrl.Setup.Tests;

using EyeCtrl.Setup;
using EyeCtrl.Setup.Abstractions;
using EyeCtrl.Setup.Tests.TestSupport;

/// <summary>
/// F2: a medium verb started from one of the four folders a swap renames must prepare a run copy,
/// re-launch the same verb from it, and exit. The harness lists the running Setup process, which
/// is what the real process table contains while the medium process waits on its elevated child.
/// </summary>
public class RenamedFolderHandoffTests
{
    private static SetupHarness FromInstalledSetup()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("1.0.0");
        harness.Environment.ProcessPath = harness.Paths.InstalledSetupExePath;
        harness.ListOwnProcess();
        return harness;
    }

    [Fact]
    public void UpdateFromTheInstalledSetup_HandsOffFromARunCopy_AndDoesNotSwapHere()
    {
        var harness = FromInstalledSetup();
        var package = @"C:\inbox\2.0.0";
        harness.SeedPackage(package, "2.0.0");

        var result = harness.Workflow.Update(package);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.HandedOff);
        Check.That(harness.Broker.Requests.Select(r => r.Job)).ContainsExactly(ElevatedJob.PrepareRunCopy);
        Check.That(harness.Broker.Requests[0].SourceSetupExe).IsEqualTo(harness.Paths.InstalledSetupExePath);
        Check.That(harness.Log).Contains(
            "start:" + harness.Paths.RunSetupExePath(harness.RunIds.Id) + "|update|" + package);
        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot).Version).IsEqualTo("1.0.0");
        Check.That(harness.Recorder.TryReadLast()).IsNull();
        Check.That(result.ProcessId).IsNotNull();
        Check.That(SetupExitCodes.For(result.Outcome)).IsEqualTo(SetupExitCodes.HandedOff);
    }

    [Fact]
    public void UninstallFromTheInstalledSetup_HandsOffWithTheQuietFlag()
    {
        var harness = FromInstalledSetup();

        var result = harness.Workflow.Uninstall(quiet: true, keepData: null);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.HandedOff);
        Check.That(harness.Log).Contains(
            "start:" + harness.Paths.RunSetupExePath(harness.RunIds.Id) + "|uninstall|--quiet");
        Check.That(harness.Files.DirectoryExists(harness.Paths.InstallRoot)).IsTrue();
    }

    [Fact]
    public void RollbackFromTheInstalledSetup_HandsOff()
    {
        var harness = FromInstalledSetup();
        harness.SeedPrevious("0.9.0");

        var result = harness.Workflow.Rollback();

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.HandedOff);
        Check.That(harness.Log).Contains(
            "start:" + harness.Paths.RunSetupExePath(harness.RunIds.Id) + "|rollback");
    }

    [Fact]
    public void RepairFromTheInstalledSetup_HandsOff()
    {
        var harness = FromInstalledSetup();

        var result = harness.Workflow.Repair();

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.HandedOff);
        Check.That(harness.Log).Contains(
            "start:" + harness.Paths.RunSetupExePath(harness.RunIds.Id) + "|repair");
    }

    [Fact]
    public void AProcessStartedFromPrevious_AlsoHandsOff()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("2.0.0");
        harness.SeedPrevious("1.0.0");
        harness.Environment.ProcessPath = harness.Paths.PreviousSetupExePath;
        harness.ListOwnProcess();

        var result = harness.Workflow.Repair();

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.HandedOff);
        Check.That(harness.Broker.Requests[0].SourceSetupExe).IsEqualTo(harness.Paths.PreviousSetupExePath);
    }

    [Fact]
    public void ARefusedHandoffRunCopy_StopsTheHandoff()
    {
        var harness = FromInstalledSetup();
        var package = @"C:\inbox\2.0.0";
        harness.SeedPackage(package, "2.0.0");
        harness.Signature.Result = new EyeCtrl.Setup.Abstractions.FileSignatureCheck(
            false, null, "tampered");

        var result = harness.Workflow.Update(package);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.VerifyFailed);
        Check.That(harness.Broker.Requests.Select(r => r.Job)).ContainsExactly(ElevatedJob.PrepareRunCopy);
        Check.That(harness.Log.Any(e =>
            e.Equals("start:" + harness.Paths.RunSetupExePath(harness.RunIds.Id), StringComparison.OrdinalIgnoreCase)))
            .IsFalse();
        Check.That(harness.Recorder.TryReadLast()?.Outcome).IsEqualTo(SetupOutcome.VerifyFailed);
    }

    [Fact]
    public void AProcessAlreadyInTheRunRoot_DoesNotHandOff()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("2.0.0");
        harness.SeedPrevious("1.0.0");
        harness.Environment.ProcessPath = harness.Paths.RunSetupExePath("old");
        harness.Files.AddFile(harness.Environment.ProcessPath);
        harness.ListOwnProcess();

        var result = harness.Workflow.Rollback();

        Check.That(result.Outcome).IsNotEqualTo(SetupOutcome.HandedOff);
        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Ok);
        Check.That(harness.Recorder.TryReadLast()).IsNotNull();
    }

    [Fact]
    public void AHandedOffChild_ReusesItsRunIdForUpdate()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("1.0.0");
        var package = @"C:\inbox\2.0.0";
        harness.SeedPackage(package, "2.0.0");
        harness.Environment.ProcessPath = harness.Paths.RunSetupExePath("run1");
        harness.SeedVersionFolder(harness.Paths.RunFolder("run1"), "1.0.0");
        harness.ListOwnProcess();
        harness.RunIds.Id = "run2";

        var result = harness.Workflow.Update(package);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Ok);
        Check.That(harness.Broker.Requests.Any(r =>
            r.Job == ElevatedJob.PrepareUpdate && r.RunId == "run1" && r.ReuseRunCopy)).IsTrue();
        Check.That(harness.Broker.Requests.Any(r => r.Job == ElevatedJob.PrepareRunCopy)).IsFalse();
        Check.That(harness.Files.FileExists(harness.Paths.RunSetupExePath("run1"))).IsTrue();
        Check.That(harness.Files.DirectoryExists(harness.Paths.RunFolder("run2"))).IsFalse();
    }

    [Fact]
    public void AHandedOffChild_ReusesItsRunIdForUninstall()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("1.0.0");
        harness.Environment.ProcessPath = harness.Paths.RunSetupExePath("run1");
        harness.Files.AddFile(harness.Environment.ProcessPath);
        harness.ListOwnProcess();
        harness.RunIds.Id = "run2";

        var result = harness.Workflow.Uninstall(quiet: true, keepData: null);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Uninstalled);
        Check.That(harness.Broker.Requests.Any(r =>
            r.Job == ElevatedJob.Uninstall && r.RunId == "run1")).IsTrue();
        Check.That(harness.Broker.Requests.Any(r => r.Job == ElevatedJob.PrepareRunCopy)).IsFalse();
        Check.That(harness.Files.DirectoryExists(harness.Paths.RunFolder("run2"))).IsFalse();
    }
}
