namespace EyeCtrl.Setup.Tests;

using EyeCtrl.Setup;
using EyeCtrl.Setup.Abstractions;
using EyeCtrl.Setup.Tests.TestSupport;
using EyeCtrl.Setup.Windows;

public class ElevationBrokerTests
{
    private static (ShellExecuteElevationBroker Broker, FakeElevatedLauncher Launcher, SetupHarness Harness)
        Build(IElevationPolicy? policy = null)
    {
        var harness = new SetupHarness();
        harness.Environment.ProcessPath = harness.Paths.InstalledSetupExePath;
        var launcher = new FakeElevatedLauncher();
        var broker = new ShellExecuteElevationBroker(
            harness.Paths, harness.Elevated, harness.Environment,
            policy ?? new ElevationPolicyGate(harness.Registry), launcher, harness.Logging);
        return (broker, launcher, harness);
    }

    private static IReadOnlyList<ElevatedRequest> EveryJob() =>
    [
        new(ElevatedJob.PrepareUpdate, PackageDirectory: @"C:\inbox\2.0.0", RunId: "r1"),
        new(ElevatedJob.ApplyUpdate, RunId: "r1"),
        new(ElevatedJob.PrepareRunCopy,
            RunId: "r1", SourceSetupExe: @"C:\Program Files\EyeCtrl\EyeCtrl.Setup.exe"),
        new(ElevatedJob.ApplyRollback, RunId: "r1"),
        new(ElevatedJob.Revert, RunId: "r1"),
        new(ElevatedJob.Uninstall, RunId: "r1", KeepData: true),
        new(ElevatedJob.Uninstall, RunId: "r1", KeepData: false),
        new(ElevatedJob.Repair, RunId: "r1"),
    ];

    [Fact]
    public void EveryJob_BuildsAnArgv_ThatParsesBackToTheSameRequest()
    {
        var (broker, _, _) = Build();

        foreach (var request in EveryJob())
        {
            var (_, arguments) = broker.BuildLaunch(request);
            var parsed = SetupCommandLine.Parse(arguments);

            Check.WithCustomMessage($"argv for {request.Job}: {string.Join(' ', arguments)}")
                .That(parsed.Error).IsNull();
            Check.WithCustomMessage($"round trip for {request.Job}")
                .That(SetupComposition.ToRequest(parsed)).IsEqualTo(request);
        }
    }

    [Fact]
    public void EveryJob_LaunchesExactlyOnce()
    {
        var (broker, launcher, _) = Build();

        foreach (var request in EveryJob())
            broker.Run(request);

        // EveryJob has two uninstall variants, so one launch per request.
        Check.That(launcher.Calls).HasSize(EveryJob().Count);
    }

    [Fact]
    public void PrepareUpdate_LaunchesOnlyThePreparePhase()
    {
        var (broker, launcher, _) = Build();

        var result = broker.Run(new ElevatedRequest(
            ElevatedJob.PrepareUpdate, PackageDirectory: @"C:\inbox\2.0.0", RunId: "r1"));

        Check.That(result.Succeeded).IsTrue();
        Check.That(launcher.Calls).HasSize(1);
        Check.That(launcher.Calls[0].Arguments).Contains("prepare-update");
        Check.That(launcher.Calls[0].Arguments).Not.Contains("apply-update");
    }

    [Fact]
    public void ApplyUpdate_LaunchesTheRunCopy_WithOnlyTheRunId()
    {
        var (broker, launcher, harness) = Build();

        broker.Run(new ElevatedRequest(ElevatedJob.ApplyUpdate, RunId: "r1"));

        Check.That(launcher.Calls).HasSize(1);
        Check.That(launcher.Calls[0].ExePath).IsEqualTo(harness.Paths.RunSetupExePath("r1"));
        Check.That(launcher.Calls[0].Arguments).ContainsExactly(
            "--internal", "apply-update", "--run-id", "r1");
    }

    [Fact]
    public void ThePolicy_IsReReadBeforeEveryLaunch()
    {
        var policy = new SequenceElevationPolicy();
        policy.Enqueue(true);   // prepare
        policy.Enqueue(false);  // apply: policy changed mid-run
        var (broker, launcher, _) = Build(policy);

        var prepare = broker.Run(new ElevatedRequest(
            ElevatedJob.PrepareUpdate, PackageDirectory: @"C:\inbox\2.0.0", RunId: "r1"));
        var apply = broker.Run(new ElevatedRequest(ElevatedJob.ApplyUpdate, RunId: "r1"));

        Check.That(prepare.Succeeded).IsTrue();
        Check.That(apply.Status).IsEqualTo(ElevatedPhaseStatus.Blocked);
        Check.That(launcher.Calls).HasSize(1);
    }

    [Fact]
    public void ATimedOutChild_IsFailed()
    {
        var (broker, launcher, _) = Build();
        launcher.Result = new LauncherResult(LauncherOutcome.TimedOut, Detail: "childTimeout");

        var result = broker.Run(new ElevatedRequest(ElevatedJob.ApplyUpdate, RunId: "r1"));

        Check.That(result.Status).IsEqualTo(ElevatedPhaseStatus.Failed);
        Check.That(result.Detail).IsEqualTo("childTimeout");
    }

    [Fact]
    public void AStillRunningChild_IsFailedAndStopsTheNextPhase()
    {
        var (broker, launcher, _) = Build();
        launcher.Result = new LauncherResult(LauncherOutcome.StillRunning, Detail: "childStillRunning");

        var result = broker.Run(new ElevatedRequest(ElevatedJob.ApplyUpdate, RunId: "r1"));

        Check.That(result.Status).IsEqualTo(ElevatedPhaseStatus.Failed);
        Check.That(result.Detail).IsEqualTo("childStillRunning");
    }

    [Fact]
    public void ARefusedElevation_IsBlocked()
    {
        var (broker, launcher, _) = Build();
        launcher.Result = new LauncherResult(LauncherOutcome.Blocked, Detail: "elevationRefused");

        var result = broker.Run(new ElevatedRequest(ElevatedJob.ApplyUpdate, RunId: "r1"));

        Check.That(result.Status).IsEqualTo(ElevatedPhaseStatus.Blocked);
    }

    [Fact]
    public void EveryJob_RefusesWhenThePolicyIsBlocked_BeforeLaunching()
    {
        foreach (var request in EveryJob())
        {
            var policy = new SequenceElevationPolicy();
            policy.Enqueue(false);
            var (broker, launcher, _) = Build(policy);

            var result = broker.Run(request);

            Check.WithCustomMessage($"blocked policy for {request.Job}")
                .That(result.Status).IsEqualTo(ElevatedPhaseStatus.Blocked);
            Check.That(launcher.Calls).IsEmpty();
        }
    }

    [Fact]
    public void AnApplyUpdateWithNoPackageDirectory_Parses()
    {
        var parsed = SetupCommandLine.Parse(["--internal", "apply-update", "--run-id", "r1"]);

        Check.That(parsed.Error).IsNull();
        Check.That(parsed.Job).IsEqualTo(SetupJob.InternalApplyUpdate);
    }
}
