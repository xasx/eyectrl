namespace EyeCtrl.Setup.Tests;

using EyeCtrl.Core.Services;
using EyeCtrl.Setup;
using EyeCtrl.Setup.Abstractions;
using EyeCtrl.Setup.Install;
using EyeCtrl.Setup.Tests.TestSupport;
using EyeCtrl.Setup.Windows;
using Microsoft.Win32;

/// <summary>
/// Fixup 01 D3: every production entry point that starts Setup must end up with the INSTALLED
/// App's per-user facts — the profile folder (and uninstall <c>--delete</c> target), the pass
/// rule's log root, <c>setup-last.json</c>, and the Setup session mutex name.
/// <para>
/// The entry points here are the Setup-side ones: the owner's script start, the run-copy
/// hand-offs for update / rollback / repair / uninstall (plain and <c>--delete</c>), the elevated
/// children, and the RunOnce logon repair. The App-side three (Settings → Update offer, →
/// Rollback, → Uninstall) live in <c>EyeCtrl.App.Tests</c>, where the real hand-off argv is
/// captured from <c>SetupGateways</c>.
/// </para>
/// <para>
/// Mutants these must catch: <b>MF</b> (the installed check always false) turns the installed
/// App's own answer dev, so every assertion below goes RED; <b>"Setup rule = location"</b> turns
/// the run-copy/inbox Setup dev, so they go RED too.
/// </para>
/// </summary>
public class SetupInstalledProfileEntryPointTests
{
    private static SetupWorkflow ForwardingWorkflow(SetupHarness harness) =>
        new(
            harness.Paths, harness.Policy, harness.Broker, harness.AppStart, harness.Planner,
            harness.Files, harness.Processes, harness.Choice, harness.RunIds, harness.Metadata,
            harness.Recorder, harness.Environment, harness.Clock, harness.Logging,
            // A Release Setup resolved non-dev, so it forwards the flag (D2). The resolver is
            // static per process and a test host is always a Debug/dev build; the seam is the
            // only way to speak for the installed product.
            forwardRealProfile: () => true);

    private static SetupHarness FromTheInstalledSetup()
    {
        var harness = new SetupHarness();
        harness.SeedInstalled("1.0.0");
        harness.Environment.ProcessPath = harness.Paths.InstalledSetupExePath;
        harness.ListOwnProcess();
        return harness;
    }

    // ── Install by script (Install-EyeCtrl.ps1) ─────────────────────────────────────────

    [Fact]
    public void InstallByScript_ResolvesTheInstalledProfile()
    {
        // The wrapper's real argument line, comments stripped: 'update "<dir>" --real-profile'.
        var package = Path.Combine(
            InstalledProductProfile.LocalAppData, "EyeCtrl", "updates", "2.0.0");
        var arguments = InstalledProductProfile.ReadInstallScriptArguments(package);

        InstalledProductProfile.AssertEntryPoint(
            "Install-EyeCtrl.ps1 start",
            Path.Combine(package, "EyeCtrl.Setup.exe"),
            arguments);
    }

    // ── Setup → run copy (the medium verb's hand-off), per verb ─────────────────────────

    [Fact]
    public void TheFirstUpdateFromAPreFlagApp_ResolvesTheInstalledProfile()
    {
        // Mixed versions (review 01 F1): the installed 1.9.0 App's SetupGateways knows no flag, so
        // the very next owner-started update launches the NEW inbox Setup with plain
        // 'update "<dir>"'. The Release Setup must resolve real on its OWN rule — not on a flag
        // the old App cannot pass — or its pass rule reads EyeCtrl.Dev\logs and kills the App.
        var package = Path.Combine(
            InstalledProductProfile.LocalAppData, "EyeCtrl", "updates", "2.0.0");

        InstalledProductProfile.AssertEntryPoint(
            "first update from a pre-flag App",
            Path.Combine(package, "EyeCtrl.Setup.exe"),
            ["update", package]);
    }

    [Fact]
    public void UpdateRunCopyHandOff_ResolvesTheInstalledProfile()
    {
        var harness = FromTheInstalledSetup();
        var package = @"C:\inbox\2.0.0";
        harness.SeedPackage(package, "2.0.0");

        var result = ForwardingWorkflow(harness).Update(package);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.HandedOff);
        var start = harness.Processes.Starts.Single();
        Check.That(start.Arguments).Contains("--real-profile");
        InstalledProductProfile.AssertEntryPoint(
            "update run-copy hand-off", start.ExePath, start.Arguments);
    }

    [Fact]
    public void RollbackRunCopyHandOff_ResolvesTheInstalledProfile()
    {
        // Settings → Rollback: the installed Setup hands off to EyeCtrl.run\<id>\, the folder the
        // location rule classifies as dev. Its pass rule must still read the real App's log.
        var harness = FromTheInstalledSetup();
        harness.SeedPrevious("0.9.0");

        var result = ForwardingWorkflow(harness).Rollback();

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.HandedOff);
        var start = harness.Processes.Starts.Single();
        Check.That(start.Arguments).Contains("--real-profile");
        InstalledProductProfile.AssertEntryPoint(
            "rollback run-copy hand-off", start.ExePath, start.Arguments);
    }

    [Fact]
    public void RepairRunCopyHandOff_ResolvesTheInstalledProfile()
    {
        var harness = FromTheInstalledSetup();

        var result = ForwardingWorkflow(harness).Repair();

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.HandedOff);
        var start = harness.Processes.Starts.Single();
        Check.That(start.Arguments).Contains("--real-profile");
        InstalledProductProfile.AssertEntryPoint(
            "repair run-copy hand-off", start.ExePath, start.Arguments);
    }

    [Fact]
    public void UninstallRunCopyHandOff_ResolvesTheInstalledProfile()
    {
        var harness = FromTheInstalledSetup();

        var result = ForwardingWorkflow(harness).Uninstall(quiet: true, keepData: null);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.HandedOff);
        var start = harness.Processes.Starts.Single();
        Check.That(start.Arguments).Contains("--real-profile");
        InstalledProductProfile.AssertEntryPoint(
            "uninstall run-copy hand-off", start.ExePath, start.Arguments);
    }

    [Fact]
    public void UninstallDelete_ResolvesTheInstalledProfile_AndTheDeleteTargetFollowsIt()
    {
        // The owner asked for the data to be deleted: the delete target is the resolved
        // AppDataRoot, so it must be the installed App's profile — never EyeCtrl.Dev.
        var harness = FromTheInstalledSetup();

        var result = ForwardingWorkflow(harness).Uninstall(quiet: false, keepData: false);

        Check.That(result.Outcome).IsEqualTo(SetupOutcome.HandedOff);
        var start = harness.Processes.Starts.Single();
        Check.That(start.Arguments).Contains("--delete");
        Check.That(start.Arguments).Contains("--real-profile");

        var setup = InstalledProductProfile.ResolveSetup(start.ExePath, start.Arguments);
        InstalledProductProfile.AssertResolvesTheInstalledProduct(
            "uninstall --delete run-copy hand-off", setup);
        Check.That(SetupPaths.FromProfile(setup).AppDataRoot)
            .IsEqualTo(InstalledProductProfile.InstalledApp.Directory);
    }

    [Fact]
    public void ThePhysicalDeleteTarget_IsTheResolvedProfile_NeverTheDevOne()
    {
        // PhysicalAppDataDeletion deletes exactly SetupPaths.AppDataRoot. Drive it against a
        // throw-away pair of folders (temp only — never %APPDATA%): the resolved profile goes,
        // the dev profile beside it stays.
        var root = Directory.CreateTempSubdirectory("eyectrl-delete-test").FullName;
        try
        {
            var installed = Path.Combine(root, "EyeCtrl");
            var dev = Path.Combine(root, "EyeCtrl.Dev");
            Directory.CreateDirectory(installed);
            Directory.CreateDirectory(dev);
            File.WriteAllText(Path.Combine(installed, "settings.json"), "{}");
            File.WriteAllText(Path.Combine(dev, "settings.json"), "{}");

            var setup = ProfileLocation.Resolve(
                Path.Combine(InstalledProductProfile.LocalAppData, "EyeCtrl", "updates", "2.0.0", "EyeCtrl.Setup.exe"),
                isDebugBuild: false,
                args: ["uninstall", "--delete"],
                root,
                root,
                root,
                isSetupProcess: true);
            var paths = SetupPaths.FromProfile(setup);

            var result = new PhysicalAppDataDeletion(paths).Delete();

            Check.That(result.Succeeded).IsTrue();
            Check.That(Directory.Exists(installed)).IsFalse();
            Check.That(Directory.Exists(dev)).IsTrue();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ── Setup → elevated children ────────────────────────────────────────────────────────

    [Fact]
    public void EveryElevatedChildArgv_ResolvesTheInstalledProfile()
    {
        var harness = FromTheInstalledSetup();
        harness.Environment.ProcessPath = harness.Paths.InstalledSetupExePath;
        var broker = new ShellExecuteElevationBroker(
            harness.Paths, harness.Elevated, harness.Environment, harness.ElevationState,
            new ElevationPolicyGate(harness.Registry), new FakeElevatedLauncher(),
            harness.PhaseResults, harness.Logging,
            forwardRealProfile: () => true);

        foreach (var request in new[]
                 {
                     new ElevatedRequest(ElevatedJob.PrepareUpdate, PackageDirectory: @"C:\inbox\2.0.0", RunId: "r1"),
                     new ElevatedRequest(ElevatedJob.ApplyUpdate, RunId: "r1"),
                     new ElevatedRequest(ElevatedJob.PrepareRunCopy, RunId: "r1", SourceSetupExe: harness.Paths.InstalledSetupExePath),
                     new ElevatedRequest(ElevatedJob.ApplyRollback, RunId: "r1"),
                     new ElevatedRequest(ElevatedJob.Revert, RunId: "r1"),
                     new ElevatedRequest(ElevatedJob.Uninstall, RunId: "r1"),
                     new ElevatedRequest(ElevatedJob.Repair, RunId: "r1"),
                 })
        {
            var (exePath, arguments) = broker.BuildLaunch(request);
            InstalledProductProfile.AssertEntryPoint(
                $"elevated child {request.Job}", exePath, arguments);
        }
    }

    // ── The production session guard names ──────────────────────────────────────────────

    [Fact]
    public void TheSessionGuardNames_FollowTheirProcessRules()
    {
        // Setup's session name comes from the SETUP rule; the App's pre-hand-off probe comes from
        // the APP rule. In this Debug test host both are the dev name — the pin is WHICH resolver
        // feeds each, so a future edit cannot quietly hand the installed product's mutex to a
        // dev Setup (or fence the installed Setup out with a .Dev name).
        Check.That(SetupSessionGuard.SessionMutexName)
            .IsEqualTo(ProfileLocation.CurrentSetup.SetupSessionMutexName);
        Check.That(SetupSessionGuard.AppProbeMutexName)
            .IsEqualTo(ProfileLocation.Current.SetupSessionMutexName);
        Check.That(SetupSessionGuard.SessionMutexName)
            .IsEqualTo(@"Local\EyeCtrl.Setup.Session.Dev");
    }

    // ── The RunOnce logon repair ────────────────────────────────────────────────────────

    [Fact]
    public void TheRunOnceRepairCommand_ResolvesTheInstalledProfile()
    {
        // The elevated swap writes HKCU RunOnce\EyeCtrlRepair = "<run copy>" repair. After a
        // crash that is the entry point the next logon runs — with no flag of its own.
        var harness = new SetupHarness();
        harness.SeedInstalled("1.0.0");
        harness.SeedStaging("2.0.0");
        // The second rename and the undo both fail, leaving the RunOnce value in place exactly
        // as an interrupted swap does.
        harness.Files.MoveFailure = index => index is 2 or 3;

        var result = harness.State.ApplyUpdate(harness.Paths.RunSetupExePath("r1"));
        Check.That(result.Failure).IsEqualTo(SwapFailure.Failed);

        var value = harness.Registry.Get(
            RegistryHive.CurrentUser, SetupConstants.RunOnceKeyPath,
            SetupConstants.RepairRunOnceName);
        Check.That(value).IsNotNull();

        var (exePath, arguments) = InstalledProductProfile.ParseRunOnceValue((string)value!);
        Check.That(arguments).Contains("repair");
        InstalledProductProfile.AssertEntryPoint("RunOnce repair", exePath, arguments);
    }
}
