namespace EyeCtrl.Setup.Tests;

using EyeCtrl.Setup;
using EyeCtrl.Setup.Tests.TestSupport;
using EyeCtrl.Setup.Windows;

public class WorkingDirectoryTests
{
    [Fact]
    public void TheEntryPoint_SetsANeutralWorkingDirectory()
    {
        var originalDirectory = Environment.CurrentDirectory;
        var originalOut = Console.Out;
        try
        {
            // Otherwise the equality below could pass because the host was already there.
            Check.That(SetupWorkingDirectory.Neutral).Not.IsEqualTo(originalDirectory);

            var exitCode = Program.Main(["--help"]);

            Check.That(exitCode).IsEqualTo(SetupExitCodes.Ok);
            Check.That(Environment.CurrentDirectory).IsEqualTo(SetupWorkingDirectory.Neutral);
            Check.That(IsUnderAnEyeCtrlFolder(@"C:\Program Files", Environment.CurrentDirectory)).IsFalse();
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void Enter_SwallowsARefusedSet_SoTheInstallStillRuns()
    {
        var entered = SetupWorkingDirectory.Enter(
            SetupWorkingDirectory.Neutral,
            _ => throw new IOException("refused"));

        Check.That(entered).IsFalse();
    }

    [Fact]
    public void TheNeutralFolder_IsNeverOneOfTheEyeCtrlFolders()
    {
        Check.That(IsUnderAnEyeCtrlFolder(@"C:\Program Files", SetupWorkingDirectory.Neutral)).IsFalse();
        Check.That(SetupPaths.IsUnder(@"C:\Program Files\EyeCtrl.run", SetupWorkingDirectory.Neutral))
            .IsFalse();
    }

    [Fact]
    public void IsUnderAnEyeCtrlFolder_RecognisesEveryInstallFolder()
    {
        // The guard the tests below rely on is not vacuous.
        Check.That(IsUnderAnEyeCtrlFolder(@"C:\Program Files", @"C:\Program Files\EyeCtrl")).IsTrue();
        Check.That(IsUnderAnEyeCtrlFolder(@"C:\Program Files", @"C:\Program Files\EyeCtrl.previous\x")).IsTrue();
        Check.That(IsUnderAnEyeCtrlFolder(@"C:\Program Files", @"C:\Program Files\EyeCtrl.staging")).IsTrue();
        Check.That(IsUnderAnEyeCtrlFolder(@"C:\Program Files", @"C:\Program Files\EyeCtrl.failed")).IsTrue();
        Check.That(IsUnderAnEyeCtrlFolder(@"C:\Program Files", @"C:\Program Files\EyeCtrl.run\r1")).IsTrue();
        Check.That(IsUnderAnEyeCtrlFolder(@"C:\Program Files", @"C:\Program Files\EyeCtrlOther")).IsFalse();
    }

    [Fact]
    public void EveryProcessStart_CarriesTheNeutralWorkingDirectory()
    {
        // Update starts the App; the hand-off starts the run copy; rollback starts the run copy
        // and then the App; repair starts the App.
        var update = new SetupHarness();
        update.SeedInstalled("1.0.0");
        update.SeedPackage(@"C:\inbox\2.0.0", "2.0.0");
        update.Workflow.Update(@"C:\inbox\2.0.0");

        var handoff = new SetupHarness();
        handoff.SeedInstalled("1.0.0");
        handoff.SeedPackage(@"C:\inbox\2.0.0", "2.0.0");
        handoff.Environment.ProcessPath = handoff.Paths.InstalledSetupExePath;
        handoff.Workflow.Update(@"C:\inbox\2.0.0");

        var rollback = new SetupHarness();
        rollback.SeedInstalled("1.0.0");
        rollback.SeedPrevious("0.9.0");
        rollback.Workflow.Rollback();

        var repair = new SetupHarness();
        repair.SeedInstalled("1.0.0");
        repair.Workflow.Repair();

        foreach (var harness in new[] { update, handoff, rollback, repair })
        {
            Check.That(harness.Processes.Starts).Not.IsEmpty();
            Check.That(harness.Processes.Starts).ContainsOnlyElementsThatMatch(
                start => start.WorkingDirectory == SetupWorkingDirectory.Neutral);
            Check.That(harness.Processes.Starts).ContainsOnlyElementsThatMatch(
                start => !IsUnderAnEyeCtrlFolder(harness.Paths.ProgramFilesRoot, start.WorkingDirectory));
        }
    }

    [Fact]
    public void TheProcessSeam_AppliesTheWorkingDirectoryAndShellExecute()
    {
        var startInfo = WindowsProcessControl.BuildStartInfo(
            @"C:\Program Files\EyeCtrl\EyeCtrl.App.exe",
            SetupWorkingDirectory.Neutral,
            ["--test-mode"]);

        Check.That(startInfo.UseShellExecute).IsTrue();
        Check.That(startInfo.WorkingDirectory).IsEqualTo(SetupWorkingDirectory.Neutral);
        Check.That(startInfo.ArgumentList).ContainsExactly("--test-mode");
    }

    [Fact]
    public void TheRunasLauncher_AppliesTheWorkingDirectoryAndRunas()
    {
        var startInfo = ShellExecuteRunAsLauncher.BuildStartInfo(
            @"C:\Program Files\EyeCtrl.run\r1\EyeCtrl.Setup.exe",
            ["--internal", "apply-update", "--run-id", "r1"],
            SetupWorkingDirectory.Neutral);

        Check.That(startInfo.UseShellExecute).IsTrue();
        Check.That(startInfo.Verb).IsEqualTo("runas");
        Check.That(startInfo.WorkingDirectory).IsEqualTo(SetupWorkingDirectory.Neutral);
        Check.That(startInfo.ArgumentList).ContainsExactly(
            "--internal", "apply-update", "--run-id", "r1");
    }

    /// <summary>True when <paramref name="path"/> is inside any of the five EyeCtrl* folders.</summary>
    private static bool IsUnderAnEyeCtrlFolder(string programFilesRoot, string path)
    {
        var roots = new[]
        {
            SetupConstants.InstallFolderName,
            SetupConstants.PreviousFolderName,
            SetupConstants.StagingFolderName,
            SetupConstants.FailedFolderName,
            SetupConstants.RunFolderName,
        };

        return roots.Any(folder =>
            SetupPaths.IsUnder(Path.Combine(programFilesRoot, folder), path));
    }
}
