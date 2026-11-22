namespace EyeCtrl.Setup.Tests;

using EyeCtrl.Setup;
using EyeCtrl.Setup.Install;
using EyeCtrl.Setup.Tests.TestSupport;

public class ShortcutTests
{
    /// <summary>The machine's common programs folder, as the harness's fake layout has it.</summary>
    private const string ProgramsRoot = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs";

    /// <summary>The machine's common desktop, as the harness's fake layout has it.</summary>
    private const string DesktopRoot = @"C:\Users\Public\Desktop";

    /// <summary>Seeds a first install (no previous version) and runs the update verb.</summary>
    private static SetupResult RunFirstInstall(SetupHarness harness)
    {
        var package = @"C:\inbox\1.0.0";
        harness.SeedPackage(package, "1.0.0");
        return harness.Workflow.Update(package);
    }

    [Fact]
    public void AFirstInstall_CreatesBothShortcuts_PointingAtTheInstalledApp()
    {
        var harness = new SetupHarness();

        var result = RunFirstInstall(harness);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Ok);
        Check.That(harness.Files.FileExists(harness.Paths.StartMenuShortcutPath)).IsTrue();
        Check.That(harness.Files.FileExists(harness.Paths.DesktopShortcutPath)).IsTrue();
        Check.That(harness.Links.Calls).ContainsExactly(
            $@"{ProgramsRoot}\EyeCtrl.lnk|{harness.Paths.AppExePath}",
            $@"{DesktopRoot}\EyeCtrl.lnk|{harness.Paths.AppExePath}");
        Check.That(harness.Logging.Lines.Any(l => l == "INF Created the Start menu shortcut.")).IsTrue();
        Check.That(harness.Logging.Lines.Any(l => l == "INF Created the desktop shortcut.")).IsTrue();
    }

    [Fact]
    public void AnExistingShortcut_IsNeverOverwritten()
    {
        var harness = new SetupHarness();
        harness.Files.AddFile(harness.Paths.StartMenuShortcutPath, "existing");
        harness.Files.AddFile(harness.Paths.DesktopShortcutPath, "existing");

        var result = RunFirstInstall(harness);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Ok);
        Check.That(harness.Links.Calls).IsEmpty();
        Check.That(harness.Files.ReadAllText(harness.Paths.StartMenuShortcutPath)).IsEqualTo("existing");
        Check.That(harness.Files.ReadAllText(harness.Paths.DesktopShortcutPath)).IsEqualTo("existing");
    }

    [Fact]
    public void AShortcutWriteFailure_IsABestEffortWarning()
    {
        var harness = new SetupHarness();
        harness.Links.OnWrite = () => throw new InvalidOperationException("shell refused");

        var result = RunFirstInstall(harness);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Ok);
        Check.That(harness.Metadata.Read(harness.Paths.InstallRoot).Version).IsEqualTo("1.0.0");
        Check.That(harness.Logging.Lines.Any(l => l.StartsWith(
            "WRN Could not create the Start menu shortcut", StringComparison.Ordinal))).IsTrue();
        Check.That(harness.Logging.Lines.Any(l => l.StartsWith(
            "WRN Could not create the desktop shortcut", StringComparison.Ordinal))).IsTrue();
    }

    [Fact]
    public void OneFailingShortcut_DoesNotStopTheOther()
    {
        var harness = new SetupHarness();
        var calls = 0;
        harness.Links.OnWrite = () =>
        {
            calls++;
            if (calls == 1)
                throw new InvalidOperationException("shell refused");
        };

        RunFirstInstall(harness);

        Check.That(harness.Files.FileExists(harness.Paths.StartMenuShortcutPath)).IsFalse();
        Check.That(harness.Files.FileExists(harness.Paths.DesktopShortcutPath)).IsTrue();
        Check.That(harness.Links.Calls).HasSize(2);
    }

    [Fact]
    public void AnUninstall_RemovesBothShortcuts()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("1.0.0");
        harness.Files.AddFile(harness.Paths.StartMenuShortcutPath);
        harness.Files.AddFile(harness.Paths.DesktopShortcutPath);

        var result = harness.Workflow.Uninstall(quiet: true, keepData: null);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Uninstalled);
        Check.That(harness.Files.FileExists(harness.Paths.StartMenuShortcutPath)).IsFalse();
        Check.That(harness.Files.FileExists(harness.Paths.DesktopShortcutPath)).IsFalse();
        Check.That(harness.Logging.Lines.Any(l => l == "INF Uninstall removed the Start menu shortcut.")).IsTrue();
        Check.That(harness.Logging.Lines.Any(l => l == "INF Uninstall removed the desktop shortcut.")).IsTrue();
    }

    [Fact]
    public void AnUninstallWithoutShortcuts_IsSilentAboutThem()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("1.0.0");

        var result = harness.Workflow.Uninstall(quiet: true, keepData: null);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Uninstalled);
        Check.That(harness.Logging.Lines.Where(l => l.Contains("shortcut", StringComparison.Ordinal)).ToList())
            .IsEmpty();
    }

    [Fact]
    public void ARevert_RecreatesTheMissingShortcuts()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("2.0.0");
        harness.SeedPrevious("1.0.0");

        var outcome = harness.State.RevertFailedUpdate(harness.Paths.RunSetupExePath("run1"));

        Check.That(outcome.Ok).IsTrue();
        Check.That(harness.Files.FileExists(harness.Paths.StartMenuShortcutPath)).IsTrue();
        Check.That(harness.Files.FileExists(harness.Paths.DesktopShortcutPath)).IsTrue();
        Check.That(harness.Links.Calls).HasSize(2);
    }

    [Fact]
    public void ARepair_FindsAStartableInstallAndEnsuresTheShortcuts()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("3.0.0");

        var outcome = harness.State.Repair(harness.Paths.RunSetupExePath("run1"));

        Check.That(outcome.Ok).IsTrue();
        Check.That(harness.Files.FileExists(harness.Paths.StartMenuShortcutPath)).IsTrue();
        Check.That(harness.Files.FileExists(harness.Paths.DesktopShortcutPath)).IsTrue();
    }

    [Fact]
    public void AnOwnerRollback_CreatesTheMissingShortcuts()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("2.0.0");
        harness.SeedPrevious("1.0.0");

        var outcome = harness.State.ApplyOwnerRollback(harness.Paths.RunSetupExePath("run1"));

        Check.That(outcome.Ok).IsTrue();
        Check.That(harness.Files.FileExists(harness.Paths.StartMenuShortcutPath)).IsTrue();
        Check.That(harness.Files.FileExists(harness.Paths.DesktopShortcutPath)).IsTrue();
        Check.That(harness.Links.Calls).HasSize(2);
    }

    [Fact]
    public void ARepairFromTheJournal_CreatesTheMissingShortcuts()
    {
        // The Update journal's "first rename never started" state: install + uncommitted staging.
        var harness = new SetupHarness();
        harness.SeedInstalled("1.0.0");
        harness.SeedStaging("2.0.0");
        harness.Journal.Write(new InstallJournal(InstallOperation.Update, 1));

        var outcome = harness.State.Repair(harness.Paths.RunSetupExePath("run1"));

        Check.That(outcome.Ok).IsTrue();
        Check.That(harness.Files.DirectoryExists(harness.Paths.StagingRoot)).IsFalse();
        Check.That(harness.Files.FileExists(harness.Paths.StartMenuShortcutPath)).IsTrue();
        Check.That(harness.Files.FileExists(harness.Paths.DesktopShortcutPath)).IsTrue();
        Check.That(harness.Links.Calls).HasSize(2);
    }

    [Fact]
    public void TheCreatedLogLine_NamesTheLocationTheWriteWentTo()
    {
        // Pins the log label to the path: with the Start menu link already present, only the
        // desktop link is written, so only the "desktop" line may appear (the M4 mutant that
        // swapped the two labels logged "Start menu" for the desktop write and survived).
        var harness = new SetupHarness();
        harness.Files.AddFile(harness.Paths.StartMenuShortcutPath, "existing");

        RunFirstInstall(harness);

        Check.That(harness.Links.Calls).ContainsExactly(
            $@"{DesktopRoot}\EyeCtrl.lnk|{harness.Paths.AppExePath}");
        Check.That(harness.Logging.Lines.Any(l => l == "INF Created the desktop shortcut.")).IsTrue();
        Check.That(harness.Logging.Lines.Any(l => l.Contains("Start menu", StringComparison.Ordinal))).IsFalse();
    }

    [Fact]
    public void AFailedShortcutRemoval_IsBestEffort_AndIsolatesTheTwoLinks()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("1.0.0");
        harness.Files.AddFile(harness.Paths.StartMenuShortcutPath);
        harness.Files.AddFile(harness.Paths.DesktopShortcutPath);
        harness.Files.DeleteFileFailure = path =>
            string.Equals(path, harness.Paths.StartMenuShortcutPath, StringComparison.OrdinalIgnoreCase);

        var result = harness.Workflow.Uninstall(quiet: true, keepData: null);

        // A .lnk that stays never turns a removed program into a failed uninstall; the other
        // link is still removed.
        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Uninstalled);
        Check.That(harness.Files.FileExists(harness.Paths.StartMenuShortcutPath)).IsTrue();
        Check.That(harness.Files.FileExists(harness.Paths.DesktopShortcutPath)).IsFalse();
        Check.That(harness.Logging.Lines.Any(l => l.StartsWith(
            "WRN Could not remove the Start menu shortcut", StringComparison.Ordinal))).IsTrue();
        Check.That(harness.Logging.Lines.Any(l => l == "INF Uninstall removed the desktop shortcut.")).IsTrue();
    }
}
