namespace EyeCtrl.Setup.Tests;

using System.Text.Json;
using EyeCtrl.Setup;
using EyeCtrl.Setup.Tests.TestSupport;

public class StatusServiceTests
{
    [Fact]
    public void Status_ReportsTheInstalledAndPreviousVersions()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("2.0.0");
        harness.SeedPrevious("1.0.0");
        harness.Processes.Add(harness.Paths.AppExePath);

        var report = harness.Status.GetReport();

        Check.That(report.Installed).IsTrue();
        Check.That(report.Version).IsEqualTo("2.0.0");
        Check.That(report.PreviousVersion).IsEqualTo("1.0.0");
        Check.That(report.State).IsEqualTo("installed");
        Check.That(report.AppRunning).IsTrue();
        Check.That(report.SupervisorRunning).IsFalse();
        Check.That(report.ElevationAllowed).IsTrue();
    }

    [Fact]
    public void Status_NamesTheInterruptedStates()
    {
        var harness = new SetupHarness();
        harness.SeedStaging("2.0.0");

        var report = harness.Status.GetReport();

        Check.That(report.Installed).IsFalse();
        Check.That(report.StagingPending).IsTrue();
        Check.That(report.State).IsEqualTo("stagingPending");
    }

    [Fact]
    public void Status_IncludesTheLastResult()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("1.0.0");
        harness.Recorder.Write(new SetupResult
        {
            Verb = SetupVerb.Update,
            Outcome = SetupOutcome.RolledBack,
            Version = "2.0.0",
            PreviousVersion = "1.0.0",
            Time = harness.Clock.UtcNow,
        });

        var report = harness.Status.GetReport();

        Check.That(report.LastResult).IsNotNull();
        Check.That(report.LastResult!.Outcome).IsEqualTo(SetupOutcome.RolledBack);
    }

    [Fact]
    public void Status_ReportsARunningSupervisor()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("1.0.0");
        harness.Processes.Add(harness.Paths.SupervisorExePath);

        var report = harness.Status.GetReport();

        Check.That(report.AppRunning).IsFalse();
        Check.That(report.SupervisorRunning).IsTrue();
    }

    [Fact]
    public void Status_ReportsAPendingRunOnceRepair()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("1.0.0");
        harness.Registry.SeedString(
            Microsoft.Win32.RegistryHive.CurrentUser, SetupConstants.RunOnceKeyPath,
            SetupConstants.RepairRunOnceName, "\"run\" repair");

        Check.That(harness.Status.GetReport().RepairPending).IsTrue();
    }

    [Fact]
    public void Status_ReportsAPendingJournalRepair()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("1.0.0");
        harness.Journal.Write(new EyeCtrl.Setup.Install.InstallJournal(
            EyeCtrl.Setup.Install.InstallOperation.Update, 2));

        Check.That(harness.Status.GetReport().RepairPending).IsTrue();
    }

    [Fact]
    public void Status_IsNotPending_WithoutARepairMark()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("1.0.0");

        Check.That(harness.Status.GetReport().RepairPending).IsFalse();
    }

    [Fact]
    public void Status_IsReadOnly()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("1.0.0");
        harness.Processes.Add(harness.Paths.AppExePath);
        var before = harness.Log.Count;

        _ = harness.Status.GetReport();

        var mutations = harness.Log.Skip(before).Where(entry =>
            entry.StartsWith("mkdir:", StringComparison.Ordinal)
            || entry.StartsWith("move:", StringComparison.Ordinal)
            || entry.StartsWith("delete", StringComparison.Ordinal)
            || entry.StartsWith("write:", StringComparison.Ordinal)
            || entry.StartsWith("reg:write", StringComparison.Ordinal)
            || entry.StartsWith("reg:delete", StringComparison.Ordinal)
            || entry.StartsWith("start:", StringComparison.Ordinal)
            || entry.StartsWith("stop:", StringComparison.Ordinal)
            || entry.StartsWith("kill:", StringComparison.Ordinal)
            || entry.StartsWith("reboot:", StringComparison.Ordinal)).ToList();
        Check.That(mutations).IsEmpty();
    }

    [Fact]
    public void TheStatusJson_IsCamelCase()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("1.0.0");

        var json = JsonSerializer.Serialize(harness.Status.GetReport(), SetupJson.Options);

        Check.That(json).Contains("\"installed\": true");
        Check.That(json).Contains("\"appRunning\"");
        Check.That(json).Contains("\"elevationAllowed\"");
    }
}
