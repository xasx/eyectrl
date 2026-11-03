namespace EyeCtrl.Setup.Tests;

using EyeCtrl.Setup;
using EyeCtrl.Setup.Install;
using EyeCtrl.Setup.Tests.TestSupport;
using Microsoft.Win32;

public class InstallStateMachineTests
{
    private static SetupHarness Harness()
    {
        var harness = new SetupHarness();
        return harness;
    }

    [Fact]
    public void ApplyUpdate_RenamesInstallToPrevious_ThenStagingToInstall()
    {
        var harness = Harness();
        harness.SeedInstalled("1.0.0");
        harness.SeedStaging("2.0.0");

        var result = harness.State.ApplyUpdate(@"C:\Program Files\EyeCtrl.run\r1\EyeCtrl.Setup.exe");

        Check.That(result.Ok).IsTrue();
        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot).Version).IsEqualTo("2.0.0");
        Check.That(harness.Metadata.Read(harness.Paths.PreviousRoot).Version).IsEqualTo("1.0.0");
        Check.That(harness.Files.DirectoryExists(harness.Paths.StagingRoot)).IsFalse();
        Check.That(harness.Registry.Get(
            RegistryHive.CurrentUser, SetupConstants.RunOnceKeyPath,
            SetupConstants.RepairRunOnceName)).IsNull();
        Check.That(harness.Registry.Get(
            RegistryHive.LocalMachine, SetupConstants.UninstallKeyPath, "DisplayVersion"))
            .IsEqualTo("2.0.0");
    }

    [Fact]
    public void ApplyUpdate_OnAFirstInstall_LeavesNoPrevious()
    {
        var harness = Harness();
        harness.SeedStaging("2.0.0");

        var result = harness.State.ApplyUpdate(@"C:\Program Files\EyeCtrl.run\r1\EyeCtrl.Setup.exe");

        Check.That(result.Ok).IsTrue();
        Check.That(harness.Files.DirectoryExists(harness.Paths.InstallRoot)).IsTrue();
        Check.That(harness.Files.DirectoryExists(harness.Paths.PreviousRoot)).IsFalse();
    }

    [Fact]
    public void ApplyUpdate_WithoutStaging_ChangesNothing()
    {
        var harness = Harness();
        harness.SeedInstalled("1.0.0");

        var result = harness.State.ApplyUpdate(@"C:\Program Files\EyeCtrl.run\r1\EyeCtrl.Setup.exe");

        Check.That(result.Failure).IsEqualTo(SwapFailure.NotApplied);
        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot).Version).IsEqualTo("1.0.0");
    }

    [Fact]
    public void AFailedSecondRename_UndoesTheFirst()
    {
        var harness = Harness();
        harness.SeedInstalled("1.0.0");
        harness.SeedStaging("2.0.0");
        harness.Files.MoveFailure = index => index == 2;

        var result = harness.State.ApplyUpdate(@"C:\Program Files\EyeCtrl.run\r1\EyeCtrl.Setup.exe");

        Check.That(result.Failure).IsEqualTo(SwapFailure.NotApplied);
        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot).Version).IsEqualTo("1.0.0");
        Check.That(harness.Files.DirectoryExists(harness.Paths.PreviousRoot)).IsFalse();
        Check.That(harness.Registry.Get(
            RegistryHive.CurrentUser, SetupConstants.RunOnceKeyPath,
            SetupConstants.RepairRunOnceName)).IsNull();
    }

    [Fact]
    public void AFailedUndo_KeepsTheRunOnceValueForRepair()
    {
        var harness = Harness();
        harness.SeedInstalled("1.0.0");
        harness.SeedStaging("2.0.0");
        harness.Files.MoveFailure = index => index is 2 or 3;

        var result = harness.State.ApplyUpdate(@"C:\Program Files\EyeCtrl.run\r1\EyeCtrl.Setup.exe");

        Check.That(result.Failure).IsEqualTo(SwapFailure.Failed);
        Check.That(harness.Registry.Get(
            RegistryHive.CurrentUser, SetupConstants.RunOnceKeyPath,
            SetupConstants.RepairRunOnceName)).IsNotNull();
        Check.That(harness.Metadata.Read(harness.Paths.PreviousRoot).Version).IsEqualTo("1.0.0");
        Check.That(harness.Files.DirectoryExists(harness.Paths.InstallRoot)).IsFalse();
    }

    [Fact]
    public void TheRunOnceValue_ExistsOnlyBetweenTheRenames()
    {
        var harness = Harness();
        harness.SeedInstalled("1.0.0");
        harness.SeedStaging("2.0.0");

        harness.State.ApplyUpdate(@"C:\Program Files\EyeCtrl.run\r1\EyeCtrl.Setup.exe");

        var set = harness.Log.IndexOf("reg:write:" + SetupConstants.RepairRunOnceName);
        var firstMove = harness.Log.FindIndex(entry => entry.StartsWith("move:", StringComparison.Ordinal));
        var clear = harness.Log.IndexOf("reg:delete:" + SetupConstants.RepairRunOnceName);
        Check.That(set).IsStrictlyGreaterThan(-1);
        Check.That(firstMove).IsStrictlyGreaterThan(set);
        Check.That(clear).IsStrictlyGreaterThan(firstMove);
    }

    [Fact]
    public void RevertFailedUpdate_PutsTheBrokenVersionInFailed()
    {
        var harness = Harness();
        harness.SeedInstalled("2.0.0");
        harness.SeedPrevious("1.0.0");

        var result = harness.State.RevertFailedUpdate(@"C:\Program Files\EyeCtrl.run\r1\EyeCtrl.Setup.exe");

        Check.That(result.Ok).IsTrue();
        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot).Version).IsEqualTo("1.0.0");
        Check.That(harness.Metadata.Read(harness.Paths.FailedRoot).Version).IsEqualTo("2.0.0");
        Check.That(harness.Files.DirectoryExists(harness.Paths.PreviousRoot)).IsFalse();
        Check.That(harness.Registry.Get(
            RegistryHive.LocalMachine, SetupConstants.UninstallKeyPath, "DisplayVersion"))
            .IsEqualTo("1.0.0");
    }

    [Fact]
    public void OwnerRollback_SwapsPreviousIn_AndKeepsTheDisplacedVersionAsPrevious()
    {
        var harness = Harness();
        harness.SeedInstalled("2.0.0");
        harness.SeedPrevious("1.0.0");

        var result = harness.State.ApplyOwnerRollback(@"C:\Program Files\EyeCtrl.run\r1\EyeCtrl.Setup.exe");

        Check.That(result.Ok).IsTrue();
        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot).Version).IsEqualTo("1.0.0");
        Check.That(harness.Metadata.Read(harness.Paths.PreviousRoot).Version).IsEqualTo("2.0.0");
        Check.That(harness.Files.DirectoryExists(harness.Paths.StagingRoot)).IsFalse();
    }

    [Fact]
    public void AFailedOwnerRollbackThirdRename_UndoesTheFirstTwo()
    {
        var harness = Harness();
        harness.SeedInstalled("2.0.0");
        harness.SeedPrevious("1.0.0");
        harness.Files.MoveFailure = index => index == 3;

        var result = harness.State.ApplyOwnerRollback(@"C:\Program Files\EyeCtrl.run\r1\EyeCtrl.Setup.exe");

        Check.That(result.Failure).IsEqualTo(SwapFailure.NotApplied);
        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot).Version).IsEqualTo("2.0.0");
        Check.That(harness.Metadata.Read(harness.Paths.PreviousRoot).Version).IsEqualTo("1.0.0");
        Check.That(harness.Registry.Get(
            RegistryHive.CurrentUser, SetupConstants.RunOnceKeyPath,
            SetupConstants.RepairRunOnceName)).IsNull();
    }

    [Fact]
    public void OwnerRollback_WithoutPrevious_ChangesNothing()
    {
        var harness = Harness();
        harness.SeedInstalled("2.0.0");

        var result = harness.State.ApplyOwnerRollback(@"C:\Program Files\EyeCtrl.run\r1\EyeCtrl.Setup.exe");

        Check.That(result.Failure).IsEqualTo(SwapFailure.NotApplied);
        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot).Version).IsEqualTo("2.0.0");
    }

    // ── Every crash point is repairable ──────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void EveryUpdateCrashPoint_IsRepaired(int failingMove)
    {
        var harness = Harness();
        harness.SeedInstalled("1.0.0");
        harness.SeedStaging("2.0.0");
        harness.Files.MoveFailure = index => index == failingMove;

        harness.State.ApplyUpdate(@"C:\Program Files\EyeCtrl.run\r1\EyeCtrl.Setup.exe");
        var repair = harness.State.Repair(@"C:\Program Files\EyeCtrl.run\r1\EyeCtrl.Setup.exe");

        Check.That(repair.Ok).IsTrue();
        Check.That(harness.Files.DirectoryExists(harness.Paths.InstallRoot)).IsTrue();
        Check.That(harness.Registry.Get(
            RegistryHive.CurrentUser, SetupConstants.RunOnceKeyPath,
            SetupConstants.RepairRunOnceName)).IsNull();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EveryOwnerRollbackCrashPoint_IsRepaired(int failingMove)
    {
        var harness = Harness();
        harness.SeedInstalled("2.0.0");
        harness.SeedPrevious("1.0.0");
        harness.Files.MoveFailure = index => index == failingMove;

        harness.State.ApplyOwnerRollback(@"C:\Program Files\EyeCtrl.run\r1\EyeCtrl.Setup.exe");
        var repair = harness.State.Repair(@"C:\Program Files\EyeCtrl.run\r1\EyeCtrl.Setup.exe");

        Check.That(repair.Ok).IsTrue();
        Check.That(harness.Files.DirectoryExists(harness.Paths.InstallRoot)).IsTrue();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void EveryRevertCrashPoint_IsRepaired(int failingMove)
    {
        var harness = Harness();
        harness.SeedInstalled("2.0.0");
        harness.SeedPrevious("1.0.0");
        harness.Files.MoveFailure = index => index == failingMove;

        harness.State.RevertFailedUpdate(@"C:\Program Files\EyeCtrl.run\r1\EyeCtrl.Setup.exe");
        var repair = harness.State.Repair(@"C:\Program Files\EyeCtrl.run\r1\EyeCtrl.Setup.exe");

        Check.That(repair.Ok).IsTrue();
        Check.That(harness.Files.DirectoryExists(harness.Paths.InstallRoot)).IsTrue();
    }

    [Fact]
    public void ACrashBetweenTheRenames_IsRepairedByPromotingStaging()
    {
        // The exact state after install -> previous and before staging -> install.
        var harness = Harness();
        harness.SeedPrevious("1.0.0");
        harness.SeedStaging("2.0.0");
        harness.Registry.SeedString(
            RegistryHive.CurrentUser, SetupConstants.RunOnceKeyPath,
            SetupConstants.RepairRunOnceName, "\"run\" repair");

        var repair = harness.State.Repair(@"C:\Program Files\EyeCtrl.run\r1\EyeCtrl.Setup.exe");

        Check.That(repair.Ok).IsTrue();
        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot).Version).IsEqualTo("2.0.0");
        Check.That(harness.Registry.Get(
            RegistryHive.CurrentUser, SetupConstants.RunOnceKeyPath,
            SetupConstants.RepairRunOnceName)).IsNull();
    }

    [Fact]
    public void ACrashWithOnlyPrevious_IsRepairedByPromotingPrevious()
    {
        var harness = Harness();
        harness.SeedPrevious("1.0.0");
        harness.Registry.SeedString(
            RegistryHive.CurrentUser, SetupConstants.RunOnceKeyPath,
            SetupConstants.RepairRunOnceName, "\"run\" repair");

        var repair = harness.State.Repair(@"C:\Program Files\EyeCtrl.run\r1\EyeCtrl.Setup.exe");

        Check.That(repair.Ok).IsTrue();
        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot).Version).IsEqualTo("1.0.0");
    }

    [Fact]
    public void Repair_WithAnInstallAndLeftoverStaging_DiscardsStaging()
    {
        var harness = Harness();
        harness.SeedInstalled("1.0.0");
        harness.SeedStaging("2.0.0");

        var repair = harness.State.Repair(@"C:\Program Files\EyeCtrl.run\r1\EyeCtrl.Setup.exe");

        Check.That(repair.Ok).IsTrue();
        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot).Version).IsEqualTo("1.0.0");
        Check.That(harness.Files.DirectoryExists(harness.Paths.StagingRoot)).IsFalse();
    }

    [Fact]
    public void Repair_WithNothingToRepair_FailsAndKeepsRunOnce()
    {
        var harness = Harness();

        var repair = harness.State.Repair(@"C:\Program Files\EyeCtrl.run\r1\EyeCtrl.Setup.exe");

        Check.That(repair.Failure).IsEqualTo(SwapFailure.Failed);
    }

    [Fact]
    public void Snapshot_NamesEveryState()
    {
        var harness = Harness();
        Check.That(harness.State.Snapshot().State).IsEqualTo("missing");

        harness.SeedStaging("2.0.0");
        Check.That(harness.State.Snapshot().State).IsEqualTo("stagingPending");

        harness.Files.DeleteDirectory(harness.Paths.StagingRoot, recursive: true);
        harness.SeedPrevious("1.0.0");
        Check.That(harness.State.Snapshot().State).IsEqualTo("previousOnly");

        harness.SeedInstalled("2.0.0");
        Check.That(harness.State.Snapshot().State).IsEqualTo("installed");
        Check.That(harness.State.Snapshot().InstalledVersion).IsEqualTo("2.0.0");
        Check.That(harness.State.Snapshot().PreviousVersion).IsEqualTo("1.0.0");
    }
}
