namespace EyeCtrl.Setup.Tests;

using EyeCtrl.Setup;
using EyeCtrl.Setup.Abstractions;
using EyeCtrl.Setup.Install;
using EyeCtrl.Setup.Tests.TestSupport;
using EyeCtrl.Setup.Windows;

/// <summary>
/// The owner's update flow needs the REAL profile, and its Setup runs from the inbox under
/// <c>%LOCALAPPDATA%</c> — which the profile resolver classifies as a dev build by location.
/// The design (coordinator ruling, 19.11): Install-EyeCtrl.ps1 starts the inbox Setup with
/// <c>--real-profile</c>; the strict parser accepts the flag on EVERY verb and on the internal
/// jobs; Setup forwards it to every child it launches.
/// </summary>
public class SetupRealProfileTests
{
    // ── The strict parser: the flag is legal on every verb ──────────────────────────────

    [Theory]
    [InlineData("update")]
    [InlineData("rollback")]
    [InlineData("repair")]
    [InlineData("status")]
    [InlineData("uninstall")]
    public void EveryPublicVerb_AcceptsTheRealProfileFlag(string verb)
    {
        var command = SetupCommandLine.Parse(verb == "update"
            ? ["update", @"C:\inbox\2.0.0", "--real-profile"]
            : [verb, "--real-profile"]);

        Check.WithCustomMessage($"{verb} must accept --real-profile").That(command.Error).IsNull();
        Check.That(command.RealProfile).IsTrue();
    }

    [Theory]
    [InlineData("uninstall")]
    [InlineData("rollback")]
    public void PublicVerb_WithoutTheFlag_LeavesItUnset(string verb)
    {
        var command = SetupCommandLine.Parse([verb]);

        Check.That(command.Error).IsNull();
        Check.That(command.RealProfile).IsFalse();
    }

    [Fact]
    public void Uninstall_AcceptsTheFlag_BesideQuietAndKeep()
    {
        var command = SetupCommandLine.Parse(["uninstall", "--quiet", "--keep", "--real-profile"]);

        Check.That(command.Error).IsNull();
        Check.That(command.Quiet).IsTrue();
        Check.That(command.KeepData).IsEqualTo(true);
        Check.That(command.RealProfile).IsTrue();
    }

    [Fact]
    public void InternalJobs_AcceptTheRealProfileFlag()
    {
        var command = SetupCommandLine.Parse(
            ["--internal", "apply-update", "--run-id", "r1", "--real-profile"]);

        Check.That(command.Error).IsNull();
        Check.That(command.RealProfile).IsTrue();
    }

    [Fact]
    public void Update_StillRefusesEverythingElse()
    {
        // Strictness is unchanged: only the package directory and --real-profile are legal.
        Check.That(SetupCommandLine.Parse(["update", @"C:\inbox\2.0.0", "--flagella"]).Error).IsNotNull();
        Check.That(SetupCommandLine.Parse(["status", "extra"]).Error).IsNotNull();
    }

    // ── The forward: children of a flagged Setup keep the flag ──────────────────────────

    private static ShellExecuteElevationBroker BuildBroker(SetupHarness harness, bool forward)
    {
        return new ShellExecuteElevationBroker(
            harness.Paths, harness.Elevated, harness.Environment, harness.ElevationState,
            new ElevationPolicyGate(harness.Registry), new FakeElevatedLauncher(), harness.PhaseResults,
            harness.Logging,
            forwardRealProfile: () => forward);
    }

    [Fact]
    public void AFlaggedSetup_AppendsTheFlagToEveryElevatedChild()
    {
        var harness = new SetupHarness();
        var broker = BuildBroker(harness, forward: true);

        foreach (var request in new[]
                 {
                     new ElevatedRequest(ElevatedJob.PrepareUpdate, PackageDirectory: @"C:\inbox\2.0.0", RunId: "r1"),
                     new ElevatedRequest(ElevatedJob.ApplyUpdate, RunId: "r1"),
                     new ElevatedRequest(ElevatedJob.PrepareRunCopy, RunId: "r1", SourceSetupExe: @"C:\pf\EyeCtrl\EyeCtrl.Setup.exe"),
                     new ElevatedRequest(ElevatedJob.ApplyRollback, RunId: "r1"),
                     new ElevatedRequest(ElevatedJob.Revert, RunId: "r1"),
                     new ElevatedRequest(ElevatedJob.Uninstall, RunId: "r1"),
                     new ElevatedRequest(ElevatedJob.Repair, RunId: "r1"),
                 })
        {
            var (_, arguments) = broker.BuildLaunch(request);
            Check.WithCustomMessage($"{request.Job} child argv must carry the flag: {string.Join(' ', arguments)}")
                .That(arguments).Contains("--real-profile");

            // And the appended argv must still parse back to the same job (the strict parser
            // accepts the flag on internal jobs too).
            var parsed = SetupCommandLine.Parse(arguments);
            Check.WithCustomMessage($"flagged argv for {request.Job} must parse").That(parsed.Error).IsNull();
            Check.That(parsed.RealProfile).IsTrue();
        }
    }

    [Fact]
    public void AnUnflaggedSetup_AddsNoFlagToAnyChild()
    {
        var harness = new SetupHarness();
        var broker = BuildBroker(harness, forward: false);

        var (_, arguments) = broker.BuildLaunch(new ElevatedRequest(ElevatedJob.ApplyUpdate, RunId: "r1"));

        Check.That(arguments).Not.Contains("--real-profile");
    }

    [Fact]
    public void AFlaggedSetup_ForwardsTheFlagToTheRunCopy()
    {
        // The medium verb's hand-off: the run copy is started with the PUBLIC verb's arguments,
        // and a flagged Setup appends the flag there too — the whole update keeps the real profile.
        var harness = new SetupHarness();
        harness.SeedInstalled("1.0.0");
        var package = @"C:\inbox\2.0.0";
        harness.SeedPackage(package, "2.0.0");
        harness.Environment.ProcessPath = harness.Paths.InstalledSetupExePath;
        harness.ListOwnProcess();
        var workflow = new SetupWorkflow(
            harness.Paths, harness.Policy, harness.Broker, harness.AppStart, harness.Planner,
            harness.Files, harness.Processes, harness.Choice, harness.RunIds, harness.Metadata,
            harness.Recorder, harness.Environment, harness.Clock, harness.Logging,
            forwardRealProfile: () => true);

        var result = workflow.Update(package);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.HandedOff);
        Check.That(harness.Log).Contains(
            "start:" + harness.Paths.RunSetupExePath(harness.RunIds.Id) + "|update|" + package + "|--real-profile");
    }

    // ── The owner's script passes the flag ──────────────────────────────────────────────

    [Fact]
    public void InstallEyeCtrlScript_StartsTheInboxSetupWithTheFlag()
    {
        // The inbox Setup is not under Program Files; without the flag it would resolve dev and
        // write setup-last.json and the setup log into EyeCtrl.Dev. The wrapper is the owner's
        // flow, so it must pass the flag.
        var script = Path.Combine(FindRepoRoot(), "tools", "release", "Install-EyeCtrl.ps1");
        Check.WithCustomMessage($"script not found at {script}").That(File.Exists(script)).IsTrue();

        var text = File.ReadAllText(script);
        Check.WithCustomMessage(
                "Install-EyeCtrl.ps1 must start Setup with --real-profile (the inbox exe is not " +
                "an installed location; see ProfileLocation)")
            .That(text.Contains("--real-profile", StringComparison.Ordinal)).IsTrue();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EyeCtrl.slnx")))
                return dir.FullName;
            dir = dir.Parent!;
        }
        throw new InvalidOperationException(
            "Could not locate the repository root (EyeCtrl.slnx) above " + AppContext.BaseDirectory);
    }
}
