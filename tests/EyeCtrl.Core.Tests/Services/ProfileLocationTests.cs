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
        string? localAppDataRoot = Local,
        bool isSetupProcess = false,
        Func<string, string>? toLongPath = null) =>
        ProfileLocation.Resolve(
            exePath, isDebugBuild, args ?? [], programFilesRoot!, roamingRoot!, localAppDataRoot!,
            isSetupProcess, toLongPath);

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
    public void ReleaseApp_InARunCopy_IsDevByLocation()
    {
        // The location rule for the App and the Supervisor: EyeCtrl.run\<id>\ is TWO levels under
        // Program Files, so "directly under" does not reach it and the exe is dev by location.
        // Setup is the one exception, with its own rule (see the Setup-rule section below): the
        // same run-copy SETUP exe resolves the installed product's real profile, because its
        // pass rule, record and delete target all belong to the installed App.
        var byLocation = Resolve(@"C:\Program Files\EyeCtrl.run\abc123\EyeCtrl.App.exe");
        Check.That(byLocation.Mode).IsEqualTo(ProfileMode.Dev);

        var setup = Resolve(@"C:\Program Files\EyeCtrl.run\abc123\EyeCtrl.Setup.exe", isSetupProcess: true);
        Check.That(setup.Mode).IsEqualTo(ProfileMode.Installed);
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
    public void AForeignFolderUnderProgramFiles_IsNotInstalled_EvenWithTheEyeCtrlExeName()
    {
        // F2b of fixup 01: the "name starts with EyeCtrl" half of the rule, on its own. Without
        // it ANY Release exe one folder under Program Files (here C:\Program Files\Foo) resolves
        // installed and takes the owner's real profile and names.
        var p = Resolve(@"C:\Program Files\Foo\EyeCtrl.App.exe");

        Check.That(p.Mode).IsEqualTo(ProfileMode.Dev);
        Check.That(p.Directory).IsEqualTo(Path.Combine(Roaming, "EyeCtrl.Dev"));
    }

    [Fact]
    public void ShortPathForm_IsExpandedBeforeTheLocationCheck()
    {
        // F4 of fixup 01: C:\PROGRA~1\EyeCtrl\… is the installed folder. The expansion is one
        // Win32 call (GetLongPathNameW) in production; the seam drives it here so the row does
        // not depend on the test volume's 8.3 setting.
        var p = Resolve(
            @"C:\PROGRA~1\EyeCtrl\EyeCtrl.App.exe",
            toLongPath: path => path.Replace(
                @"C:\PROGRA~1", @"C:\Program Files", StringComparison.OrdinalIgnoreCase));

        Check.That(p.Mode).IsEqualTo(ProfileMode.Installed);
        Check.That(p.Directory).IsEqualTo(Path.Combine(Roaming, "EyeCtrl"));
    }

    [Fact]
    public void TheLongPathExpansion_IsWhatDecides_NotTheRawString()
    {
        // The seam is load-bearing in both directions: re-pointing the expansion moves the
        // decision, so a mutant that skips it goes back to the short-path dev answer.
        var p = Resolve(
            @"C:\PROGRA~1\EyeCtrl\EyeCtrl.App.exe",
            toLongPath: _ => @"D:\Elsewhere\EyeCtrl\EyeCtrl.App.exe");

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

    // ── The Setup rule (fixup 01 D1) ────────────────────────────────────────────────────

    [Fact]
    public void ReleaseSetup_InARunCopyResolvesTheInstalledProductsRealProfile()
    {
        // The blocker: the run copy is TWO levels under Program Files (EyeCtrl.run\<id>\), so the
        // location rule says dev — while the App Setup starts lives in EyeCtrl\ and is real. The
        // pass rule then reads EyeCtrl.Dev\logs, never finds the App's startup line and kills it.
        // A Release Setup must resolve the installed product's profile wherever it runs from.
        var p = Resolve(
            @"C:\Program Files\EyeCtrl.run\abc123\EyeCtrl.Setup.exe",
            isSetupProcess: true);

        Check.That(p.Mode).IsEqualTo(ProfileMode.Installed);
        Check.That(p.Directory).IsEqualTo(Path.Combine(Roaming, "EyeCtrl"));
        Check.That(p.NameSuffix).IsEmpty();
        Check.That(p.SetupSessionMutexName).IsEqualTo(@"Local\EyeCtrl.Setup.Session");
    }

    [Fact]
    public void ReleaseSetup_InTheInbox_AlsoResolvesTheInstalledProductsRealProfile()
    {
        // The second owner flow: the OLD App's Settings→Update starts the NEW inbox Setup with no
        // flag it cannot know. A Release Setup resolves real from its rule, not from its parent.
        var p = Resolve(
            @"C:\Users\test\AppData\Local\EyeCtrl\updates\2.0.0\EyeCtrl.Setup.exe",
            isSetupProcess: true);

        Check.That(p.Mode).IsEqualTo(ProfileMode.Installed);
        Check.That(p.Directory).IsEqualTo(Path.Combine(Roaming, "EyeCtrl"));
    }

    [Fact]
    public void DebugSetup_WithoutTheFlag_IsDev()
    {
        // Agents never run Setup, and a Debug Setup is never the owner's: it stays fenced.
        var p = Resolve(
            @"C:\Program Files\EyeCtrl\EyeCtrl.Setup.exe",
            isDebugBuild: true,
            isSetupProcess: true);

        Check.That(p.Mode).IsEqualTo(ProfileMode.Dev);
        Check.That(p.Directory).IsEqualTo(Path.Combine(Roaming, "EyeCtrl.Dev"));
    }

    [Fact]
    public void DebugSetup_WithTheFlag_IsReal()
    {
        // The one documented manual override keeps working for Setup too.
        var p = Resolve(
            @"C:\w\bin\EyeCtrl.Setup.exe",
            isDebugBuild: true,
            args: ["repair", "--real-profile"],
            isSetupProcess: true);

        Check.That(p.Mode).IsEqualTo(ProfileMode.RealByFlag);
        Check.That(p.Directory).IsEqualTo(Path.Combine(Roaming, "EyeCtrl"));
    }

    [Fact]
    public void TheSetupRule_DoesNotLeakIntoTheAppOrSupervisorRule()
    {
        // The same run-copy exe is dev when it is an APP or SUPERVISOR (their location decides),
        // and real when it is Setup. One resolver, two process rules, no shared drift.
        var app = Resolve(@"C:\Program Files\EyeCtrl.run\abc123\EyeCtrl.App.exe");
        var setup = Resolve(@"C:\Program Files\EyeCtrl.run\abc123\EyeCtrl.Setup.exe", isSetupProcess: true);

        Check.That(app.Mode).IsEqualTo(ProfileMode.Dev);
        Check.That(setup.Mode).IsEqualTo(ProfileMode.Installed);
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
