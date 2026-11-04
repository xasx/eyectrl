namespace EyeCtrl.Setup.Tests;

using EyeCtrl.Setup;
using EyeCtrl.Setup.Abstractions;
using EyeCtrl.Setup.Install;
using EyeCtrl.Setup.Tests.TestSupport;
using Microsoft.Win32;

public class SupervisionStartPlannerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void EveryCombination_StartsOnlyTheAppOnce(bool supervision, bool startOnBoot)
    {
        var harness = new SetupHarness();
        harness.Settings.State = new SupervisionBootState(supervision, startOnBoot);

        var plan = harness.Planner.Plan();

        Check.That(plan.StartAppOnce).IsTrue();
        Check.That(plan.StartSupervisor).IsFalse();
        Check.That(plan.WriteStartOnBootRunKey).IsFalse();
        Check.That(plan.AppWillStartSupervisor).IsEqualTo(supervision);
        Check.That(plan.StartOnBootEnabled).IsEqualTo(startOnBoot);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AnUpdate_StartsOnlyTheApp_ForEverySupervisionAndBootCombination(
        bool supervision, bool startOnBoot)
    {
        var harness = new SetupHarness();
        harness.Settings.State = new SupervisionBootState(supervision, startOnBoot);
        harness.SeedInstalled("1.0.0");
        var package = @"C:\inbox\2.0.0";
        harness.SeedPackage(package, "2.0.0");

        var result = harness.Workflow.Update(package);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.Ok);
        Check.That(harness.Processes.Processes.Count(p =>
            p.ImagePath == harness.Paths.AppExePath)).IsEqualTo(1);
        Check.That(harness.Processes.Processes.Any(p =>
            p.ImagePath == harness.Paths.SupervisorExePath)).IsFalse();
        Check.That(harness.Registry.Names(RegistryHive.CurrentUser, SetupConstants.RunKeyPath)).IsEmpty();
        Check.That(harness.Logging.Lines.Any(line =>
            line.Contains("Start plan: app=True supervisor=False runKey=False", StringComparison.Ordinal)))
            .IsTrue();
    }
}

public class AppsAndFeaturesWriterTests
{
    [Fact]
    public void Register_WritesTheExactContractValues()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("2.0.0");

        harness.AppsAndFeatures.Register("2.0.0");

        var key = SetupConstants.UninstallKeyPath;
        Check.That(harness.Registry.Get(RegistryHive.LocalMachine, key, "DisplayName"))
            .IsEqualTo("EyeCtrl");
        Check.That(harness.Registry.Get(RegistryHive.LocalMachine, key, "DisplayVersion"))
            .IsEqualTo("2.0.0");
        Check.That(harness.Registry.Get(RegistryHive.LocalMachine, key, "Publisher"))
            .IsEqualTo("EyeCtrl");
        Check.That(harness.Registry.Get(RegistryHive.LocalMachine, key, "InstallLocation"))
            .IsEqualTo(harness.Paths.InstallRoot);
        Check.That(harness.Registry.Get(RegistryHive.LocalMachine, key, "DisplayIcon"))
            .IsEqualTo(harness.Paths.AppExePath + ",0");
        Check.That(harness.Registry.Get(RegistryHive.LocalMachine, key, "NoModify")).IsEqualTo(1);
        Check.That(harness.Registry.Get(RegistryHive.LocalMachine, key, "NoRepair")).IsEqualTo(1);
        Check.That(harness.Registry.Get(RegistryHive.LocalMachine, key, "UninstallString"))
            .IsEqualTo("\"" + harness.Paths.InstalledSetupExePath + "\" uninstall");
        Check.That(harness.Registry.Get(RegistryHive.LocalMachine, key, "QuietUninstallString"))
            .IsEqualTo("\"" + harness.Paths.InstalledSetupExePath + "\" uninstall --quiet");
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1024, 1)]
    [InlineData(1025, 2)]
    [InlineData(2048, 2)]
    public void Register_WritesTheInstallFolderSizeInKb(int bytes, int expectedKb)
    {
        // S6c D7: Apps & Features showed no size because EstimatedSize was never written.
        var harness = new SetupHarness();
        harness.Files.AddFile(
            Path.Combine(harness.Paths.InstallRoot, "payload.bin"),
            new string('x', bytes));

        harness.AppsAndFeatures.Register("2.0.0");

        Check.That(harness.Registry.ReadDword(
            RegistryHive.LocalMachine, SetupConstants.UninstallKeyPath, "EstimatedSize"))
            .IsEqualTo(expectedKb);
    }

    [Fact]
    public void Remove_DeletesTheEntry()
    {
        var harness = new SetupHarness();
        harness.AppsAndFeatures.Register("1.0.0");

        harness.AppsAndFeatures.Remove();

        Check.That(harness.Registry.Get(
            RegistryHive.LocalMachine, SetupConstants.UninstallKeyPath, "DisplayName")).IsNull();
    }

    [Fact]
    public void TheUninstallCommand_IsRunCopySafe()
    {
        var installed = @"C:\Program Files\EyeCtrl\EyeCtrl.Setup.exe";

        Check.That(AppsAndFeaturesWriter.UninstallCommand(installed))
            .IsEqualTo("\"C:\\Program Files\\EyeCtrl\\EyeCtrl.Setup.exe\" uninstall");
        Check.That(AppsAndFeaturesWriter.QuietUninstallCommand(installed))
            .IsEqualTo("\"C:\\Program Files\\EyeCtrl\\EyeCtrl.Setup.exe\" uninstall --quiet");
    }
}
