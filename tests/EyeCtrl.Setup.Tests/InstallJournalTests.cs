namespace EyeCtrl.Setup.Tests;

using EyeCtrl.Setup.Install;
using EyeCtrl.Setup.Tests.TestSupport;

/// <summary>
/// F3: true crash points (the move happens, then the process dies without undo) and the journal
/// that tells repair what the folders hold. P3's rollback-first-rename state must restore the
/// previous version, never delete it.
/// </summary>
public class InstallJournalTests
{
    private const string RunCopy = @"C:\Program Files\EyeCtrl.run\r1\EyeCtrl.Setup.exe";

    private static SetupHarness HarnessWith(string install, string? previous)
    {
        var harness = new SetupHarness();
        if (install is not null)
            harness.SeedInstalled(install);
        if (previous is not null)
            harness.SeedPrevious(previous);
        return harness;
    }

    private static void CrashAfter(SetupHarness harness, int move, Action action)
    {
        harness.Files.CrashAfterMoveAt = move;
        var crashed = false;
        try
        {
            action();
        }
        catch (SimulatedCrashException)
        {
            crashed = true;
        }

        harness.Files.CrashAfterMoveAt = null;
        Check.That(crashed).IsTrue();
    }

    [Fact]
    public void UpdateCrashAfterTheFirstRename_RepairCompletesTheUpdate()
    {
        var harness = HarnessWith("1.0.0", previous: "0.9.0");
        harness.SeedStaging("2.0.0");

        CrashAfter(harness, 1, () => harness.State.ApplyUpdate(RunCopy));

        Check.That(harness.Files.DirectoryExists(harness.Paths.InstallRoot)).IsFalse();
        Check.That(harness.Journal.Read()!.Operation).IsEqualTo(InstallOperation.Update);
        var repair = harness.State.Repair(RunCopy);        Check.That(repair.Ok).IsTrue();
        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot).Version).IsEqualTo("2.0.0");
        Check.That(harness.Metadata.Read(harness.Paths.PreviousRoot).Version).IsEqualTo("1.0.0");
        Check.That(harness.Journal.Read()).IsNull();
    }

    [Fact]
    public void UpdateCrashAfterTheSecondRename_RepairFindsTheStartableInstall()
    {
        var harness = HarnessWith("1.0.0", previous: null);
        harness.SeedStaging("2.0.0");

        CrashAfter(harness, 2, () => harness.State.ApplyUpdate(RunCopy));

        var repair = harness.State.Repair(RunCopy);
        Check.That(repair.Ok).IsTrue();
        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot).Version).IsEqualTo("2.0.0");
        Check.That(harness.Journal.Read()).IsNull();
    }

    [Fact]
    public void RollbackCrashAfterTheFirstRename_RepairRestoresPrevious_InsteadOfDeletingIt()
    {
        var harness = HarnessWith("2.0.0", previous: "1.0.0");

        CrashAfter(harness, 1, () => harness.State.ApplyOwnerRollback(RunCopy));

        // staging now holds the previous version; the install is still 2.0.0.
        Check.That(harness.Journal.Read()!.Operation).IsEqualTo(InstallOperation.OwnerRollback);
        Check.That(harness.Metadata.Read(harness.Paths.StagingRoot).Version).IsEqualTo("1.0.0");
        Check.That(harness.Metadata.Read(harness.Paths.PreviousRoot).Version).IsNull();
        var repair = harness.State.Repair(RunCopy);
        Check.That(repair.Ok).IsTrue();
        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot).Version).IsEqualTo("2.0.0");
        Check.That(harness.Metadata.Read(harness.Paths.PreviousRoot).Version).IsEqualTo("1.0.0");
        Check.That(harness.Files.DirectoryExists(harness.Paths.StagingRoot)).IsFalse();
        Check.That(harness.Journal.Read()).IsNull();
    }

    [Fact]
    public void RollbackCrashAfterTheSecondRename_RepairCompletesTheRollback()
    {
        var harness = HarnessWith("2.0.0", previous: "1.0.0");

        CrashAfter(harness, 2, () => harness.State.ApplyOwnerRollback(RunCopy));

        Check.That(harness.Files.DirectoryExists(harness.Paths.InstallRoot)).IsFalse();
        Check.That(harness.Metadata.Read(harness.Paths.PreviousRoot).Version).IsEqualTo("2.0.0");
        Check.That(harness.Metadata.Read(harness.Paths.StagingRoot).Version).IsEqualTo("1.0.0");
        var repair = harness.State.Repair(RunCopy);
        Check.That(repair.Ok).IsTrue();
        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot).Version).IsEqualTo("1.0.0");
        Check.That(harness.Metadata.Read(harness.Paths.PreviousRoot).Version).IsEqualTo("2.0.0");
    }

    [Fact]
    public void RollbackCrashAfterTheThirdRename_RepairKeepsBothVersions()
    {
        var harness = HarnessWith("2.0.0", previous: "1.0.0");

        CrashAfter(harness, 3, () => harness.State.ApplyOwnerRollback(RunCopy));

        var repair = harness.State.Repair(RunCopy);
        Check.That(repair.Ok).IsTrue();
        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot).Version).IsEqualTo("1.0.0");
        Check.That(harness.Metadata.Read(harness.Paths.PreviousRoot).Version).IsEqualTo("2.0.0");
        Check.That(harness.Journal.Read()).IsNull();
    }

    [Fact]
    public void RevertCrashAfterTheFirstRename_RepairRestoresPrevious()
    {
        var harness = HarnessWith("2.0.0", previous: "1.0.0");

        CrashAfter(harness, 1, () => harness.State.RevertFailedUpdate(RunCopy));

        Check.That(harness.Files.DirectoryExists(harness.Paths.InstallRoot)).IsFalse();
        Check.That(harness.Metadata.Read(harness.Paths.FailedRoot).Version).IsEqualTo("2.0.0");
        Check.That(harness.Journal.Read()!.Operation).IsEqualTo(InstallOperation.Revert);
        var repair = harness.State.Repair(RunCopy);
        Check.That(repair.Ok).IsTrue();
        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot).Version).IsEqualTo("1.0.0");
        Check.That(harness.Metadata.Read(harness.Paths.FailedRoot).Version).IsEqualTo("2.0.0");
    }

    [Fact]
    public void RevertCrashAfterTheSecondRename_RepairKeepsTheRestoredInstall()
    {
        var harness = HarnessWith("2.0.0", previous: "1.0.0");

        CrashAfter(harness, 2, () => harness.State.RevertFailedUpdate(RunCopy));

        var repair = harness.State.Repair(RunCopy);
        Check.That(repair.Ok).IsTrue();
        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot).Version).IsEqualTo("1.0.0");
    }

    [Fact]
    public void AStaleRollbackJournal_DoesNotDeleteTheRealPreviousVersion()
    {
        // J1: stale OwnerRollback journal + an unapplied 3.0 in staging + the real previous 1.0.
        var harness = HarnessWith("2.0.0", previous: "1.0.0");
        harness.SeedStaging("3.0.0");
        harness.Journal.Write(new InstallJournal(InstallOperation.OwnerRollback, 1));

        var repair = harness.State.Repair(RunCopy);

        Check.That(repair.Ok).IsTrue();
        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot).Version).IsEqualTo("2.0.0");
        Check.That(harness.Metadata.Read(harness.Paths.PreviousRoot).Version).IsEqualTo("1.0.0");
        Check.That(harness.Files.DirectoryExists(harness.Paths.StagingRoot)).IsFalse();
        Check.That(harness.Journal.Read()).IsNull();
    }

    [Fact]
    public void TheJournal_IsClearedAfterACompletedTransition()
    {
        var harness = HarnessWith("1.0.0", previous: null);
        harness.SeedStaging("2.0.0");

        var result = harness.State.ApplyUpdate(RunCopy);

        Check.That(result.Ok).IsTrue();
        Check.That(harness.Journal.Read()).IsNull();
    }
}
