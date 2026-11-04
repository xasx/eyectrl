namespace EyeCtrl.Setup.Tests;

using EyeCtrl.Core.Services;
using EyeCtrl.Setup;
using EyeCtrl.Setup.Tests.TestSupport;

public class StopCoordinatorTests
{
    private static SetupHarness Harness() => new();

    [Fact]
    public void TheCleanPath_StopsTheIdleSupervisor_AndNeverKillsTheApp()
    {
        var harness = Harness();
        harness.Processes.Add(harness.Paths.SupervisorExePath);

        var result = harness.Stop.StopForSwap(TimeSpan.FromSeconds(5));

        Check.That(result.Ok).IsTrue();
        Check.That(result.WasKillPath).IsFalse();
        Check.That(harness.Log).Contains("stop:5000");
        Check.That(harness.Log.Any(entry => entry.StartsWith("kill:", StringComparison.Ordinal))).IsFalse();
        Check.That(harness.HeldButton.Calls).IsEqualTo(0);
    }

    [Fact]
    public void ACleanAppExit_IsNotKilled()
    {
        var harness = Harness();
        harness.Processes.Add(harness.Paths.AppExePath);
        harness.Processes.Add(harness.Paths.SupervisorExePath);
        harness.Processes.WaitForExitResult = true;

        var result = harness.Stop.StopForSwap(TimeSpan.FromSeconds(5));

        Check.That(result.Ok).IsTrue();
        Check.That(result.WasKillPath).IsFalse();
        Check.That(harness.Log.Any(entry => entry.StartsWith("kill:", StringComparison.Ordinal))).IsFalse();
        Check.That(harness.HeldButton.Calls).IsEqualTo(0);
    }

    [Fact]
    public void TheKillPath_StopsTheSupervisorBeforeTheApp_AndReleasesTheMarker()
    {
        var harness = Harness();
        var app = harness.Processes.Add(harness.Paths.AppExePath);
        var supervisor = harness.Processes.Add(harness.Paths.SupervisorExePath);
        harness.Processes.WaitForExitResult = false;
        harness.HeldButton.Result = new HeldButtonReleaseResult(true, true, null, false);

        var result = harness.Stop.StopForSwap(TimeSpan.FromSeconds(5));

        Check.That(result.Ok).IsTrue();
        Check.That(result.WasKillPath).IsTrue();
        var stop = harness.Log.IndexOf("stop:" + supervisor);
        var kill = harness.Log.IndexOf("kill:" + app);
        Check.That(stop).IsStrictlyGreaterThan(-1);
        Check.That(kill).IsStrictlyGreaterThan(stop);
        Check.That(harness.HeldButton.Calls).IsEqualTo(1);
    }

    [Fact]
    public void Uninstall_AlwaysTakesTheKillPath()
    {
        var harness = Harness();
        var app = harness.Processes.Add(harness.Paths.AppExePath);
        var supervisor = harness.Processes.Add(harness.Paths.SupervisorExePath);
        harness.Processes.WaitForExitResult = true;

        var result = harness.Stop.StopForSwap(TimeSpan.FromSeconds(5), forceKillPath: true);

        Check.That(result.WasKillPath).IsTrue();
        Check.That(harness.Log.IndexOf("stop:" + supervisor))
            .IsStrictlyLessThan(harness.Log.IndexOf("kill:" + app));
    }

    [Fact]
    public void AProcessStillRunningFromTheInstallFolder_BlocksTheSwap()
    {
        var harness = Harness();
        harness.Processes.Add(Path.Combine(harness.Paths.InstallRoot, "helper.exe"));

        var result = harness.Stop.StopForSwap(TimeSpan.FromSeconds(5));

        Check.That(result.Ok).IsFalse();
        Check.That(result.Blocker).IsEqualTo("helper.exe");
    }

    [Fact]
    public void AProcessFromAnotherFolder_DoesNotBlock()
    {
        var harness = Harness();
        harness.Processes.Add(@"C:\Program Files\Other\Other.exe");

        var result = harness.Stop.StopForSwap(TimeSpan.FromSeconds(5));

        Check.That(result.Ok).IsTrue();
    }

    [Fact]
    public void TheKillPath_LeavesUnrelatedChildrenAlive()
    {
        var harness = Harness();
        harness.Processes.Add(harness.Paths.AppExePath);
        var child = harness.Processes.Add(@"C:\Program Files\Other\browser.exe");
        harness.Processes.WaitForExitResult = false;

        var result = harness.Stop.StopForSwap(TimeSpan.FromSeconds(5));

        Check.That(result.Ok).IsTrue();
        Check.That(harness.Processes.Processes.Any(p => p.Id == child)).IsTrue();
    }

    [Fact]
    public void TheImageCheck_CountsAProcessWhoseImageIsTheSupervisor()
    {
        var harness = Harness();
        harness.Processes.Add(harness.Paths.SupervisorExePath);

        Check.That(harness.Stop.HasProcessFromInstallDirectory(harness.Processes.Snapshot())).IsTrue();
    }
}
