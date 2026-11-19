namespace EyeCtrl.App.Tests;

using System;
using System.IO;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Services;
using NFluent;
using Xunit;

/// <summary>
/// Locating the supervisor follows the same shape as the supervisor locating the app: an
/// environment override wins, otherwise it is assumed to sit beside us in the install
/// directory (`Program.cs:597-608`). One install layout then works in both directions.
///
/// The one deliberate difference is what these tests exist to pin: unlike
/// `ResolveAppExePath`, a missing sibling yields null rather than a hopeful path, because
/// `IsInstalled` gates whether Task 13 offers the user a supervisor at all.
/// </summary>
[Collection(SupervisorExeEnvironmentCollection.Name)]
public class SupervisorLauncherTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("eyectrl-sup").FullName;
    private readonly string? _originalEnv = Environment.GetEnvironmentVariable("EYECTRL_SUPERVISOR_EXE");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("EYECTRL_SUPERVISOR_EXE", _originalEnv);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string MakeFile(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "not really an exe");
        return path;
    }

    [Fact]
    public void Resolves_TheSiblingExe_WhenPresent()
    {
        Environment.SetEnvironmentVariable("EYECTRL_SUPERVISOR_EXE", null);
        var expected = MakeFile("EyeCtrl.Supervisor.exe");

        var resolved = SupervisorLauncher.ResolveExePath(baseDirectory: _dir);

        Check.That(resolved).IsEqualTo(Path.GetFullPath(expected));
    }

    [Fact]
    public void ResolvesNull_WhenNoSupervisorIsInstalled()
    {
        // The offer must not be made when there is nothing to launch.
        Environment.SetEnvironmentVariable("EYECTRL_SUPERVISOR_EXE", null);

        Check.That(SupervisorLauncher.ResolveExePath(baseDirectory: _dir)).IsNull();
    }

    [Fact]
    public void EnvironmentOverride_WinsOverTheSibling()
    {
        var sibling = MakeFile("EyeCtrl.Supervisor.exe");
        var elsewhere = MakeFile("Custom.Supervisor.exe");
        Environment.SetEnvironmentVariable("EYECTRL_SUPERVISOR_EXE", elsewhere);

        var resolved = SupervisorLauncher.ResolveExePath(baseDirectory: _dir);

        Check.That(resolved).IsEqualTo(Path.GetFullPath(elsewhere));
        Check.That(resolved).IsNotEqualTo(Path.GetFullPath(sibling));
    }

    [Fact]
    public void EnvironmentOverride_PointingAtNothing_FallsBackToTheSibling()
    {
        // A stale env var must not disable a perfectly good installed supervisor.
        var sibling = MakeFile("EyeCtrl.Supervisor.exe");
        Environment.SetEnvironmentVariable("EYECTRL_SUPERVISOR_EXE", Path.Combine(_dir, "gone.exe"));

        Check.That(SupervisorLauncher.ResolveExePath(baseDirectory: _dir))
             .IsEqualTo(Path.GetFullPath(sibling));
    }

    [Fact]
    public void ANonDevParent_ForwardsTheRealProfileFlag_ToTheSupervisor()
    {
        // D2 (fixup 01): an installed (or real-by-flag) App forwards the flag, so the supervisor it
        // starts resolves the same profile — a supervised pair is dev+dev or real+real.
        var psi = SupervisorLauncher.BuildStartInfo(
            @"C:\Program Files\EyeCtrl\EyeCtrl.Supervisor.exe", forwardRealProfile: true);

        Check.That(psi.FileName).IsEqualTo(@"C:\Program Files\EyeCtrl\EyeCtrl.Supervisor.exe");
        Check.That(psi.ArgumentList).ContainsExactly(ProfileLocation.RealProfileArgument);
        Check.That(psi.UseShellExecute).IsFalse();
    }

    [Fact]
    public void ADevParent_StartsTheSupervisor_WithoutTheFlag()
    {
        var psi = SupervisorLauncher.BuildStartInfo(@"C:\w\bin\EyeCtrl.Supervisor.exe", forwardRealProfile: false);

        Check.That(psi.ArgumentList).IsEmpty();
    }
}
