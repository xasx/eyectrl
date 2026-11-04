namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Setup;

/// <summary>
/// Review 02 R1: the last hop of review 01 H1. These assertions are on the PRODUCTION launcher's
/// start info, not on what the gateways pass — MUT6 (the exe's folder back as the working
/// directory) lived exactly between those two and survived every gateways-level test.
/// </summary>
public class ShellProcessLauncherTests
{
    private const string Neutral = @"C:\Windows\System32";

    private static readonly SetupPaths Paths =
        new(@"C:\PFakes", @"C:\AppDataFakes", @"C:\LocalFakes");

    [Fact]
    public void TheStartInfo_CarriesTheNeutralDirectory_TheExe_TheArgs_AndShellExecute()
    {
        var exe = Paths.InstalledSetupExePath;
        const string arguments = "rollback";

        var info = ShellProcessLauncher.BuildStartInfo(exe, arguments, Neutral, useShellExecute: true);

        Check.That(info.FileName).IsEqualTo(exe);
        Check.That(info.Arguments).IsEqualTo(arguments);
        Check.That(info.UseShellExecute).IsTrue();
        Check.That(info.WorkingDirectory).IsEqualTo(Neutral);
    }

    [Fact]
    public void TheStartInfo_NeverPinsAFolderInsideProgramFilesEyeCtrl()
    {
        // The rename lock from review 01 H1: a live cwd under the install root blocks the swap
        // and the uninstall's delete.
        var info = ShellProcessLauncher.BuildStartInfo(
            Paths.InstalledSetupExePath, "rollback", Neutral, useShellExecute: true);

        Check.That(SetupPaths.IsUnder(Paths.InstallRoot, info.WorkingDirectory)).IsFalse();
        Check.That(SetupPaths.IsUnder(Paths.PreviousRoot, info.WorkingDirectory)).IsFalse();
    }

    [Fact]
    public void TheStartInfo_DoesNotDeriveTheDirectoryFromTheExeFolder()
    {
        // The regression shape itself: if an edit put `Path.GetDirectoryName(executablePath)`
        // back, the carried directory would differ from the neutral one the caller passed.
        var info = ShellProcessLauncher.BuildStartInfo(
            Paths.InstalledSetupExePath, "rollback", Neutral, useShellExecute: true);

        Check.That(info.WorkingDirectory).IsNotEqualTo(Paths.InstallRoot);
    }
}
