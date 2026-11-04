namespace EyeCtrl.Setup.Tests;

using EyeCtrl.Setup;
using EyeCtrl.Setup.Abstractions;
using EyeCtrl.Setup.Tests.TestSupport;
using Microsoft.Win32;

public class UpdateWorkflowTests
{
    private static SetupHarness Harness() => new();

    private static string SeedUpdatable(SetupHarness harness)
    {
        harness.SeedInstalled("1.0.0");
        var package = @"C:\inbox\2.0.0";
        harness.SeedPackage(package, "2.0.0");
        return package;
    }

    [Fact]
    public void AGoodUpdate_InstallsStartsAndRecords()
    {
        var harness = Harness();
        var package = SeedUpdatable(harness);

        var result = harness.Workflow.Update(package);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Ok);
        Check.That(result.Version).IsEqualTo("2.0.0");
        Check.That(result.PreviousVersion).IsEqualTo("1.0.0");
        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot).Version).IsEqualTo("2.0.0");
        Check.That(harness.Metadata.Read(harness.Paths.PreviousRoot).Version).IsEqualTo("1.0.0");
        Check.That(harness.Registry.Get(
            RegistryHive.LocalMachine, SetupConstants.UninstallKeyPath, "DisplayVersion"))
            .IsEqualTo("2.0.0");
        Check.That(harness.Processes.Processes.Count(p =>
            p.ImagePath == harness.Paths.AppExePath)).IsEqualTo(1);
        Check.That(harness.Processes.Processes.Any(p =>
            p.ImagePath == harness.Paths.SupervisorExePath)).IsFalse();
        Check.That(harness.Registry.Names(RegistryHive.CurrentUser, SetupConstants.RunKeyPath)).IsEmpty();
        Check.That(harness.Recorder.TryReadLast()?.Outcome).IsEqualTo(SetupOutcome.Ok);
        Check.That(harness.Broker.Requests.Select(r => r.Job)).ContainsExactly(
            ElevatedJob.PrepareUpdate, ElevatedJob.ApplyUpdate);
    }

    [Fact]
    public void ATamperedPackage_IsRefused_AndTheOldVersionIsRestarted()
    {
        var harness = Harness();
        var package = SeedUpdatable(harness);
        harness.Catalog.Result = new CatalogVerificationResult(
            true, SetupConstants.PinnedThumbprint, true, ["onnxruntime.dll"], [], [], null);

        var result = harness.Workflow.Update(package);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.VerifyFailed);
        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot).Version).IsEqualTo("1.0.0");
        Check.That(harness.Files.DirectoryExists(harness.Paths.StagingRoot)).IsFalse();
        Check.That(harness.Log.Any(e => e.StartsWith("start:", StringComparison.Ordinal))).IsTrue();
        Check.That(harness.Broker.Requests.Select(r => r.Job)).ContainsExactly(ElevatedJob.PrepareUpdate);
    }

    [Fact]
    public void ABrokenPolicy_RefusesBeforeAnyChange_AndRestartsTheOldVersion()
    {
        var harness = Harness();
        var package = SeedUpdatable(harness);
        harness.SetPolicy(5);

        var result = harness.Workflow.Update(package);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Blocked);
        Check.That(harness.Broker.Requests).IsEmpty();
        Check.That(harness.Files.DirectoryExists(harness.Paths.StagingRoot)).IsFalse();
        Check.That(harness.Files.DirectoryExists(harness.Paths.RunRoot)).IsFalse();
        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot).Version).IsEqualTo("1.0.0");
        Check.That(harness.Log.Any(e => e.StartsWith("start:", StringComparison.Ordinal))).IsTrue();
    }

    [Fact]
    public void AChildStillRunningAfterTimeout_StopsWithoutRepairOrRestart()
    {
        var harness = Harness();
        var package = SeedUpdatable(harness);
        harness.Broker.Handler = request => request.Job == ElevatedJob.ApplyUpdate
            ? new ElevatedPhaseResult(ElevatedPhaseStatus.Failed, "childStillRunning")
            : harness.Elevated.Execute(request);

        var result = harness.Workflow.Update(package);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Failed);
        Check.That(result.Detail).IsEqualTo("childStillRunning");
        Check.That(harness.Broker.Requests.Any(r => r.Job == ElevatedJob.Repair)).IsFalse();
        Check.That(harness.Log.Any(e => e.StartsWith("start:", StringComparison.Ordinal))).IsFalse();
        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot).Version).IsEqualTo("1.0.0");
    }

    [Fact]
    public void AProcessStillRunningFromTheInstallFolder_RefusesTheSwap()
    {
        var harness = Harness();
        var package = SeedUpdatable(harness);
        harness.Processes.Add(Path.Combine(harness.Paths.InstallRoot, "helper.exe"));

        var result = harness.Workflow.Update(package);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.NotApplied);
        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot).Version).IsEqualTo("1.0.0");
        Check.That(harness.Files.DirectoryExists(harness.Paths.PreviousRoot)).IsFalse();
    }

    [Fact]
    public void AFailedPassRule_RollsBackAndStartsThePreviousVersion()
    {
        var harness = Harness();
        var package = SeedUpdatable(harness);
        harness.LogProbe.Results.Enqueue(AppStartupTokenLine.UiAccessFalse);
        harness.LogProbe.Results.Enqueue(AppStartupTokenLine.UiAccessTrue);

        var result = harness.Workflow.Update(package);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.RolledBack);
        Check.That(result.Version).IsEqualTo("2.0.0");
        Check.That(result.PreviousVersion).IsEqualTo("1.0.0");
        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot).Version).IsEqualTo("1.0.0");
        Check.That(harness.Metadata.Read(harness.Paths.FailedRoot).Version).IsEqualTo("2.0.0");
        Check.That(harness.Registry.Get(
            RegistryHive.LocalMachine, SetupConstants.UninstallKeyPath, "DisplayVersion"))
            .IsEqualTo("1.0.0");
        Check.That(harness.Recorder.TryReadLast()?.Outcome).IsEqualTo(SetupOutcome.RolledBack);
    }

    [Fact]
    public void ANewVersionAndAPreviousVersionThatBothLackUiAccess_ReportDegraded()
    {
        var harness = new SetupHarness();
        var package = SeedUpdatable(harness);
        harness.LogProbe.Results.Enqueue(AppStartupTokenLine.UiAccessFalse);
        harness.LogProbe.Results.Enqueue(AppStartupTokenLine.UiAccessFalse);

        var result = harness.Workflow.Update(package);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Degraded);
        Check.That(result.Version).IsEqualTo("1.0.0"); // the running last resort
        Check.That(harness.Recorder.TryReadLast()?.Outcome).IsEqualTo(SetupOutcome.Degraded);
    }

    [Fact]
    public void AFirstInstallThatFailsThePassRule_ReportsDegraded()
    {
        var harness = Harness();
        var package = @"C:\inbox\2.0.0";
        harness.SeedPackage(package, "2.0.0");
        harness.LogProbe.Result = AppStartupTokenLine.UiAccessFalse;

        var result = harness.Workflow.Update(package);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Degraded);
        Check.That(harness.Files.DirectoryExists(harness.Paths.InstallRoot)).IsTrue();
        Check.That(harness.Files.DirectoryExists(harness.Paths.PreviousRoot)).IsFalse();
        Check.That(harness.Processes.Processes.Any(p =>
            p.ImagePath == harness.Paths.AppExePath)).IsTrue();
    }

    [Fact]
    public void AnElevatedPublicRun_IsRefusedBeforeAnythingStarts()
    {
        var harness = Harness();
        var package = SeedUpdatable(harness);
        harness.Broker.IsElevated = true;

        var result = harness.Workflow.Update(package);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Failed);
        Check.That(result.Detail).IsEqualTo("mustRunMedium");
        Check.That(harness.Broker.Requests).IsEmpty();
        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot).Version).IsEqualTo("1.0.0");
        Check.That(harness.Processes.Processes.Any(p =>
            p.ImagePath == harness.Paths.AppExePath)).IsFalse();
    }

    [Fact]
    public void TheElevatedPhase_ReChecksThePolicy()
    {
        var harness = Harness();
        harness.SeedInstalled("1.0.0");
        var package = @"C:\inbox\2.0.0";
        harness.SeedPackage(package, "2.0.0");
        harness.SetPolicy(5);

        var result = harness.Elevated.Execute(new ElevatedRequest(
            ElevatedJob.PrepareUpdate, PackageDirectory: package, RunId: "r1"));

        Check.That(result.Status).IsEqualTo(ElevatedPhaseStatus.Blocked);
        Check.That(harness.Files.DirectoryExists(harness.Paths.StagingRoot)).IsFalse();
        Check.That(harness.Files.DirectoryExists(harness.Paths.RunRoot)).IsFalse();
    }

    [Fact]
    public void APackageWithoutItsSetupExe_IsRefusedBeforeTheStop()
    {
        var harness = Harness();
        harness.SeedInstalled("1.0.0");
        var package = @"C:\inbox\2.0.0";
        harness.Files.AddFile(Path.Combine(package, SetupConstants.PackageMetadataName),
            SetupHarness.PackageJson("2.0.0"));
        harness.Files.AddFile(Path.Combine(package, SetupConstants.CatalogName));

        var result = harness.Workflow.Update(package);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.VerifyFailed);
        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot).Version).IsEqualTo("1.0.0");
    }
}
