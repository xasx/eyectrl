namespace EyeCtrl.Core.Tests.Services;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

/// <summary>
/// The behaviour table of the dev-build profile guard, as pure rows over injectable inputs: no
/// test here touches the machine's real <c>%APPDATA%\EyeCtrl</c> (or <c>EyeCtrl.Dev</c>) — every
/// directory is a string built from fake roots.
/// </summary>
public class ProfileLocationTests
{
    private const string Pf = @"C:\Program Files";
    private const string Roaming = @"C:\Users\test\AppData\Roaming";
    private const string Local = @"C:\Users\test\AppData\Local";

    private static ProfileLocation Resolve(
        string? exePath,
        bool isDebugBuild = false,
        string[]? args = null,
        string? programFilesRoot = Pf,
        string? roamingRoot = Roaming,
        string? localAppDataRoot = Local) =>
        ProfileLocation.Resolve(exePath, isDebugBuild, args ?? [], programFilesRoot!, roamingRoot!, localAppDataRoot!);

    // ── The behaviour table, row by row ─────────────────────────────────────────────────

    [Fact]
    public void ReleaseExe_InEyeCtrlUnderProgramFiles_IsInstalled()
    {
        var p = Resolve(@"C:\Program Files\EyeCtrl\EyeCtrl.App.exe");

        Check.That(p.Mode).IsEqualTo(ProfileMode.Installed);
        Check.That(p.Directory).IsEqualTo(Path.Combine(Roaming, "EyeCtrl"));
        Check.That(p.NameSuffix).IsEmpty();
        Check.That(p.ModeToken).IsEqualTo("installed");
    }

    [Fact]
    public void ReleaseExe_InEyeCtrlPreviousUnderProgramFiles_IsInstalled()
    {
        // The swap renames the install folder; the previous version must keep the real profile.
        var p = Resolve(@"C:\Program Files\EyeCtrl.previous\EyeCtrl.App.exe");

        Check.That(p.Mode).IsEqualTo(ProfileMode.Installed);
    }

    [Fact]
    public void ReleaseExe_InARunCopy_IsDevByLocation_RealByTheForwardedFlag()
    {
        // Setup's elevated run copies live in EyeCtrl.run\<id>\ — TWO levels under Program Files,
        // so the "directly under" rule does not reach them: by location they are dev, exactly like
        // the ticket's %ProgramFiles%\Other\EyeCtrl edge case. The owner's real update therefore
        // does not rely on location: the medium Setup forwards --real-profile to every child, and
        // the flagged run copy resolves the real profile (the second half of this test).
        var byLocation = Resolve(@"C:\Program Files\EyeCtrl.run\abc123\EyeCtrl.Setup.exe");
        Check.That(byLocation.Mode).IsEqualTo(ProfileMode.Dev);

        var flagged = Resolve(
            @"C:\Program Files\EyeCtrl.run\abc123\EyeCtrl.Setup.exe",
            args: ["--internal", "apply-update", "--run-id", "abc123", "--real-profile"]);
        Check.That(flagged.Mode).IsEqualTo(ProfileMode.RealByFlag);
        Check.That(flagged.Directory).IsEqualTo(Path.Combine(Roaming, "EyeCtrl"));
    }

    [Fact]
    public void ReleaseExe_InAWorktreeBin_IsDev()
    {
        var p = Resolve(
            @"C:\Users\andre\worktrees\x\fix\src\EyeCtrl.App\bin\Debug\net10.0-windows\EyeCtrl.App.exe");

        Check.That(p.Mode).IsEqualTo(ProfileMode.Dev);
        Check.That(p.Directory).IsEqualTo(Path.Combine(Roaming, "EyeCtrl.Dev"));
        Check.That(p.NameSuffix).IsEqualTo(".Dev");
        Check.That(p.ModeToken).IsEqualTo("dev");
        Check.That(p.Reason).IsNotEmpty();
    }

    [Fact]
    public void DebugExe_Anywhere_IsDev()
    {
        var p = Resolve(@"C:\Program Files\EyeCtrl\EyeCtrl.App.exe", isDebugBuild: true);

        Check.That(p.Mode).IsEqualTo(ProfileMode.Dev);
        Check.That(p.Directory).IsEqualTo(Path.Combine(Roaming, "EyeCtrl.Dev"));
    }

    [Fact]
    public void AnyOtherBuild_WithRealProfileFlag_GetsTheRealProfile()
    {
        var p = Resolve(
            @"C:\Users\andre\worktrees\x\fix\src\EyeCtrl.App\bin\Debug\net10.0-windows\EyeCtrl.App.exe",
            args: ["--test-mode", "--real-profile"]);

        Check.That(p.Mode).IsEqualTo(ProfileMode.RealByFlag);
        Check.That(p.Directory).IsEqualTo(Path.Combine(Roaming, "EyeCtrl"));
        Check.That(p.NameSuffix).IsEmpty();
        Check.That(p.ModeToken).IsEqualTo("real-by-flag");
    }

    // ── The ticket's edge cases ─────────────────────────────────────────────────────────

    [Fact]
    public void EyeCtrlPrefixFolder_UnderProgramFiles_CountsAsInstalled()
    {
        // The prefix rule: any folder directly under Program Files whose name starts with EyeCtrl.
        var p = Resolve(@"C:\Program Files\EyeCtrlX\EyeCtrl.App.exe");

        Check.That(p.Mode).IsEqualTo(ProfileMode.Installed);
    }

    [Fact]
    public void EyeCtrlFolder_NestedDeeper_IsNotInstalled()
    {
        // "EyeCtrl" under another folder is NOT "directly under Program Files".
        var p = Resolve(@"C:\Program Files\Other\EyeCtrl\EyeCtrl.App.exe");

        Check.That(p.Mode).IsEqualTo(ProfileMode.Dev);
    }

    [Fact]
    public void Matching_IsCaseInsensitive()
    {
        var p = Resolve(@"c:\program files\eyectrl\EyeCtrl.App.exe");

        Check.That(p.Mode).IsEqualTo(ProfileMode.Installed);
    }

    [Fact]
    public void TrailingBackslash_OnAnyRoot_ChangesNothing()
    {
        var p = Resolve(
            @"C:\Program Files\EyeCtrl\EyeCtrl.App.exe",
            programFilesRoot: Pf + @"\",
            roamingRoot: Roaming + @"\",
            localAppDataRoot: Local + @"\");

        Check.That(p.Mode).IsEqualTo(ProfileMode.Installed);
        Check.That(p.Directory).IsEqualTo(Path.Combine(Roaming, "EyeCtrl"));
    }

    [Fact]
    public void MissingExePath_IsDev()
    {
        Check.That(Resolve(null).Mode).IsEqualTo(ProfileMode.Dev);
        Check.That(Resolve(string.Empty).Mode).IsEqualTo(ProfileMode.Dev);
    }

    [Fact]
    public void RealProfileFlag_IsCaseInsensitive_AndMatchesOnlyTheWholeToken()
    {
        Check.That(Resolve(@"C:\w\EyeCtrl.App.exe", args: ["--REAL-PROFILE"]).Mode)
            .IsEqualTo(ProfileMode.RealByFlag);
        Check.That(Resolve(@"C:\w\EyeCtrl.App.exe", args: ["--real-profiles"]).Mode)
            .IsEqualTo(ProfileMode.Dev);
    }

    // ── Machine-global names ────────────────────────────────────────────────────────────

    [Fact]
    public void InstalledBuild_UsesTheProductionNames_Unchanged()
    {
        var p = Resolve(@"C:\Program Files\EyeCtrl\EyeCtrl.App.exe");

        // The installed build's names are EXACTLY these — the guard must not change them.
        Check.That(p.AppSingletonMutexName).IsEqualTo(@"Local\EyeCtrl.App.Singleton");
        Check.That(p.SupervisorSingletonMutexName).IsEqualTo(@"Local\EyeCtrl.Supervisor.Singleton");
        Check.That(p.SettingsWriterMutexName).IsEqualTo(@"Local\EyeCtrl.Settings.Writer");
        Check.That(p.SetupSessionMutexName).IsEqualTo(@"Local\EyeCtrl.Setup.Session");
        Check.That(p.RestartReadyEventName).IsEqualTo(@"Local\EyeCtrl.App.RestartReady");
        Check.That(p.HeartbeatPipeName).IsEqualTo("eyectrl_watch");
        Check.That(p.SettingsRequestPipeName).IsEqualTo(SettingRequestChannel.PipeName);
    }

    [Fact]
    public void DevBuild_SuffixesEveryName_InstalledNamesStayUntouched()
    {
        var dev = Resolve(@"C:\w\bin\EyeCtrl.App.exe");

        Check.That(dev.AppSingletonMutexName).IsEqualTo(@"Local\EyeCtrl.App.Singleton.Dev");
        Check.That(dev.SupervisorSingletonMutexName).IsEqualTo(@"Local\EyeCtrl.Supervisor.Singleton.Dev");
        Check.That(dev.SettingsWriterMutexName).IsEqualTo(@"Local\EyeCtrl.Settings.Writer.Dev");
        Check.That(dev.SetupSessionMutexName).IsEqualTo(@"Local\EyeCtrl.Setup.Session.Dev");
        Check.That(dev.RestartReadyEventName).IsEqualTo(@"Local\EyeCtrl.App.RestartReady.Dev");
        Check.That(dev.HeartbeatPipeName).IsEqualTo("eyectrl_watch.Dev");
        Check.That(dev.SettingsRequestPipeName).IsEqualTo(SettingRequestChannel.PipeName + ".Dev");
    }

    [Fact]
    public void DevBuild_CannotCollideWithInstalledNames()
    {
        // The guard's core promise, stated directly: no dev name equals an installed name.
        var dev = Resolve(@"C:\w\bin\EyeCtrl.App.exe");
        var installed = Resolve(@"C:\Program Files\EyeCtrl\EyeCtrl.App.exe");

        Check.That(dev.AppSingletonMutexName).Not.IsEqualTo(installed.AppSingletonMutexName);
        Check.That(dev.SupervisorSingletonMutexName).Not.IsEqualTo(installed.SupervisorSingletonMutexName);
        Check.That(dev.SettingsWriterMutexName).Not.IsEqualTo(installed.SettingsWriterMutexName);
        Check.That(dev.SetupSessionMutexName).Not.IsEqualTo(installed.SetupSessionMutexName);
        Check.That(dev.RestartReadyEventName).Not.IsEqualTo(installed.RestartReadyEventName);
        Check.That(dev.HeartbeatPipeName).Not.IsEqualTo(installed.HeartbeatPipeName);
        Check.That(dev.SettingsRequestPipeName).Not.IsEqualTo(installed.SettingsRequestPipeName);
    }

    // ── App / Supervisor / Setup agreement ──────────────────────────────────────────────

    [Fact]
    public void AppAndSupervisor_FromTheSameInputs_ResolveTheSameProfileAndNames()
    {
        var app = Resolve(@"C:\w\bin\EyeCtrl.App.exe", isDebugBuild: true);
        var supervisor = Resolve(@"C:\w\bin\EyeCtrl.Supervisor.exe", isDebugBuild: true);
        var setup = Resolve(@"C:\w\bin\EyeCtrl.Setup.exe", isDebugBuild: true);

        Check.That(supervisor.Directory).IsEqualTo(app.Directory);
        Check.That(setup.Directory).IsEqualTo(app.Directory);
        Check.That(supervisor.AppSingletonMutexName).IsEqualTo(app.AppSingletonMutexName);
        Check.That(supervisor.SettingsWriterMutexName).IsEqualTo(app.SettingsWriterMutexName);
        // The same session-guard NAME from every process (that is what makes it a guard), with
        // this build's suffix — and distinct from the App's own singleton mutex.
        Check.That(setup.SetupSessionMutexName).IsEqualTo(app.SetupSessionMutexName);
        Check.That(setup.SetupSessionMutexName).IsEqualTo(@"Local\EyeCtrl.Setup.Session.Dev");
        Check.That(setup.SetupSessionMutexName).Not.IsEqualTo(app.AppSingletonMutexName);
    }

    [Fact]
    public void AFlaggedSupervisor_ForwardsTheFlag_SoTheAppResolvesTheSameProfile()
    {
        // The Supervisor passes --real-profile through only if it was itself started with it:
        // both sides of that contract, as pure resolution.
        var supervisor = Resolve(@"C:\w\bin\EyeCtrl.Supervisor.exe", args: ["--real-profile"]);
        var forwardedApp = Resolve(@"C:\w\bin\EyeCtrl.App.exe", args: ["--paused", "--real-profile"]);
        var plainApp = Resolve(@"C:\w\bin\EyeCtrl.App.exe", args: ["--paused"]);

        Check.That(forwardedApp.Directory).IsEqualTo(supervisor.Directory);
        Check.That(forwardedApp.NameSuffix).IsEqualTo(supervisor.NameSuffix);
        Check.That(forwardedApp.Mode).IsEqualTo(ProfileMode.RealByFlag);

        // Without the flag the dev pair still agrees — with each other, never with the real one.
        var plainSupervisor = Resolve(@"C:\w\bin\EyeCtrl.Supervisor.exe");
        Check.That(plainApp.Directory).IsEqualTo(plainSupervisor.Directory);
        Check.That(plainApp.Directory).Not.IsEqualTo(supervisor.Directory);
    }

    // ── The in-process default ──────────────────────────────────────────────────────────

    [Fact]
    public void Current_InATestHost_IsNeverTheRealProfile()
    {
        // The test host runs from a test bin (never directly under Program Files\EyeCtrl*), so
        // the cached process resolution must be dev: the guard holds even for defaults.
        var current = ProfileLocation.Current;

        Check.That(current.IsDev).IsTrue();
        Check.That(current.Directory).Not.IsEqualTo(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EyeCtrl"));
        Check.That(current.Directory).IsEqualTo(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EyeCtrl.Dev"));
    }

    [Fact]
    public void Roots_AreExposedUnsplit_ForConsumersThatNeedTheInbox()
    {
        // SetupPaths-style consumers take the raw roots (the update inbox is machine-real for
        // every build); the profile split applies only to the EyeCtrl folder under the roaming root.
        var p = Resolve(@"C:\w\bin\EyeCtrl.Setup.exe");

        Check.That(p.ProgramFilesRoot).IsEqualTo(Pf);
        Check.That(p.RoamingRoot).IsEqualTo(Roaming);
        Check.That(p.LocalAppDataRoot).IsEqualTo(Local);
        Check.That(p.FolderName).IsEqualTo("EyeCtrl.Dev");
    }
}
