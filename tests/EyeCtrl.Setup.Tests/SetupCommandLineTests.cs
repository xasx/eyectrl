namespace EyeCtrl.Setup.Tests;

using EyeCtrl.Setup;

public class SetupCommandLineTests
{
    [Fact]
    public void Update_TakesOnePackageDirectory()
    {
        var command = SetupCommandLine.Parse(["update", @"C:\inbox\2.0.0"]);

        Check.That(command.Error).IsNull();
        Check.That(command.Job).IsEqualTo(SetupJob.Update);
        Check.That(command.PackageDirectory).IsEqualTo(@"C:\inbox\2.0.0");
    }

    [Fact]
    public void Update_WithoutAPackage_IsAUsageError()
    {
        var command = SetupCommandLine.Parse(["update"]);

        Check.That(command.Error).IsNotNull();
        Check.That(command.Job).IsEqualTo(SetupJob.Help);
    }

    [Fact]
    public void Uninstall_Quiet_IsParsed()
    {
        var command = SetupCommandLine.Parse(["uninstall", "--quiet"]);

        Check.That(command.Error).IsNull();
        Check.That(command.Job).IsEqualTo(SetupJob.Uninstall);
        Check.That(command.Quiet).IsTrue();
    }

    [Fact]
    public void Uninstall_KeepAndDelete_AreMutuallyExclusive()
    {
        var command = SetupCommandLine.Parse(["uninstall", "--keep", "--delete"]);

        Check.That(command.Error).IsNotNull();
    }

    [Fact]
    public void Status_TakesNoArguments()
    {
        var command = SetupCommandLine.Parse(["status"]);

        Check.That(command.Error).IsNull();
        Check.That(command.Job).IsEqualTo(SetupJob.Status);
    }

    [Fact]
    public void Status_WithAnArgument_IsRefused()
    {
        Check.That(SetupCommandLine.Parse(["status", "extra"]).Error).IsNotNull();
    }

    [Fact]
    public void Internal_PrepareUpdate_NeedsPackageAndRunId()
    {
        Check.That(SetupCommandLine.Parse(["--internal", "prepare-update", @"C:\inbox", "--run-id", "r1"]).Job)
            .IsEqualTo(SetupJob.InternalPrepareUpdate);
        Check.That(SetupCommandLine.Parse(["--internal", "prepare-update", @"C:\inbox"]).Error).IsNotNull();
        Check.That(SetupCommandLine.Parse(["--internal", "prepare-update", @"C:\inbox", "--run-id", "r1"])
            .PackageDirectory).IsEqualTo(@"C:\inbox");
    }

    [Fact]
    public void Internal_PrepareRunCopy_NeedsASource()
    {
        Check.That(SetupCommandLine.Parse([
                "--internal", "prepare-run-copy", "--source", @"C:\Program Files\EyeCtrl\EyeCtrl.Setup.exe",
                "--run-id", "r1"]).SourceSetupExe)
            .IsEqualTo(@"C:\Program Files\EyeCtrl\EyeCtrl.Setup.exe");
    }

    [Fact]
    public void Internal_Uninstall_ParsesTheDataChoice()
    {
        var command = SetupCommandLine.Parse(["--internal", "uninstall", "--delete", "--run-id", "r1"]);

        Check.That(command.Job).IsEqualTo(SetupJob.InternalUninstall);
        Check.That(command.KeepData == false).IsTrue();
    }

    [Fact]
    public void UnknownVerb_IsAUsageError()
    {
        Check.That(SetupCommandLine.Parse(["frobnicate"]).Error).Contains("unknown verb");
    }

    [Fact]
    public void Help_IsRecognised()
    {
        Check.That(SetupCommandLine.Parse(["--help"]).Job).IsEqualTo(SetupJob.Help);
        Check.That(SetupCommandLine.Parse([]).Error).IsNotNull();
    }
}
