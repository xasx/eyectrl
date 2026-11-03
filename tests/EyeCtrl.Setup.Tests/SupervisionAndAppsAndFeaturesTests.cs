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

    [Fact]
    public void AnUpdateWithSupervisionOn_StillStartsOnlyTheApp_AndLeavesTheRunKeyAlone()
    {
        var harness = new SetupHarness();
        harness.Settings.State = new SupervisionBootState(true, true);
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
