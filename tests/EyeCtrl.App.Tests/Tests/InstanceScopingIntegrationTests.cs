namespace EyeCtrl.App.Tests.Tests;

using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using EyeCtrl.App.Services;
using EyeCtrl.App.Startup;
using EyeCtrl.Contracts;
using EyeCtrl.App.Tests.Fixtures;
using Microsoft.Win32;

/// <summary>
/// Launches the real app scoped and asserts the manifest. Category=UI because it starts a process
/// and shows windows; it does not move the cursor.
/// <para>
/// <b>GracefulExit_SendsNothingToTheHeartbeatPipe binds the PRODUCTION <c>eyectrl_watch</c> name</b>,
/// which is unavoidable — the assertion is precisely that the app does not dial <i>that</i> name, so
/// a GUID-suffixed stand-in would prove nothing. Consequences: it cannot run while a real supervisor
/// is active, and it must not run concurrently with a sibling worktree's suite. Confirm the box is
/// quiet first:
/// </para>
/// <code>
/// Get-Process -Name "dotnet","testhost*","EyeCtrl*" -ErrorAction SilentlyContinue
/// </code>
/// </summary>
[Trait("Category", "UI")]
public class InstanceScopingIntegrationTests
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Profile directory a scoped instance resolves to, for setup and teardown.</summary>
    private static string ProfileFor(string instanceId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EyeCtrl", "instances", instanceId);

    private static string NewInstanceId() => "it" + Guid.NewGuid().ToString("N")[..10];

    [Fact]
    public async Task ScopedLaunch_ReportsEveryResourceScoped_AndLeavesTheLiveRunValueUntouched()
    {
        AppFixture.SkipIfDisabled();

        var instanceId = NewInstanceId();
        var profile = ProfileFor(instanceId);

        // The regression this whole design exists for: a launch used to delete the live autostart
        // entry via SetStartOnBoot. Snapshot it and compare byte for byte afterwards.
        object? liveRunValueBefore;
        using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
            liveRunValueBefore = key?.GetValue("EyeCtrl");

        var psi = new ProcessStartInfo(FindAppExe()) { Arguments = "--test-mode", UseShellExecute = false };
        psi.Environment[InstanceScope.EnvVar] = instanceId;

        using var proc = Process.Start(psi)!;
        try
        {
            var manifestPath = Path.Combine(profile, InstanceManifest.FileName);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (!File.Exists(manifestPath) && DateTime.UtcNow < deadline && !proc.HasExited)
                await Task.Delay(200, TestContext.Current.CancellationToken);

            Check.That(proc.HasExited).IsFalse();
            Check.That(File.Exists(manifestPath)).IsTrue();

            using var doc = JsonDocument.Parse(
                await File.ReadAllTextAsync(manifestPath, TestContext.Current.CancellationToken));
            var root = doc.RootElement;

            Check.That(root.GetProperty("instanceId").GetString()).IsEqualTo(instanceId);
            Check.That(root.GetProperty("testMode").GetBoolean()).IsTrue();

            var unscoped = root.GetProperty("resources").EnumerateArray()
                .Where(r => !r.GetProperty("scoped").GetBoolean())
                .Select(r => r.GetProperty("name").GetString()!)
                .ToArray();
            Check.That(unscoped).IsEmpty();

            // All five must be present, not merely "none unscoped" — an empty resources array
            // satisfies the assertion above while proving nothing.
            var names = root.GetProperty("resources").EnumerateArray()
                .Select(r => r.GetProperty("name").GetString()!)
                .ToArray();
            Check.That(names).Contains(
                "ProfileDirectory", "AppSingletonMutex", "SettingsWriterMutex", "DwellPipe", "StartupRunValue");

            var refused = root.GetProperty("refused").EnumerateArray()
                .Select(r => r.GetString()!).ToArray();
            Check.That(refused).Contains("Supervision");
            Check.That(refused).Contains("SelfRestart");
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            proc.WaitForExit(5_000);
            try { Directory.Delete(profile, recursive: true); } catch { }
        }

        object? liveRunValueAfter;
        using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
            liveRunValueAfter = key?.GetValue("EyeCtrl");

        Check.That(liveRunValueAfter?.ToString()).IsEqualTo(liveRunValueBefore?.ToString());
    }

    [Fact]
    public void ScopedLaunchWithoutTestMode_RefusesWithExitCode78()
    {
        AppFixture.SkipIfDisabled();

        var instanceId = NewInstanceId();
        var psi = new ProcessStartInfo(FindAppExe()) { UseShellExecute = false };
        psi.Environment[InstanceScope.EnvVar] = instanceId;

        using var proc = Process.Start(psi)!;
        try
        {
            Check.That(proc.WaitForExit(20_000)).IsTrue();
            Check.That(proc.ExitCode).IsEqualTo(InstanceStartupPolicy.RefusalExitCode);
        }
        finally
        {
            // It refuses before writing a manifest, but InitializeInstanceAndLogging has already
            // created the scoped log directory by then — so there is still a profile to remove.
            try { Directory.Delete(ProfileFor(instanceId), recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task GracefulExit_SendsNothingToTheHeartbeatPipe()
    {
        AppFixture.SkipIfDisabled();

        // Bind the production watch pipe ourselves and assert nothing ever connects. A supervisor in
        // adoption mode would authenticate this exe on path + session, so a stray shutdown frame
        // here is what would get a live app force-killed. See the class remarks: this name cannot be
        // GUID-suffixed, because "does not dial eyectrl_watch" IS the assertion.
        using var server = new NamedPipeServerStream(
            HeartbeatPublisher.DefaultPipeName, PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var connected = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);

        var instanceId = NewInstanceId();
        var profile = ProfileFor(instanceId);
        var psi = new ProcessStartInfo(FindAppExe()) { Arguments = "--test-mode", UseShellExecute = false };
        psi.Environment[InstanceScope.EnvVar] = instanceId;

        using var proc = Process.Start(psi)!;
        try
        {
            // Wait on the manifest rather than a fixed delay: it is written at the end of phase 2,
            // which is the point after which SignalShuttingDown could possibly run.
            var manifestPath = Path.Combine(profile, InstanceManifest.FileName);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (!File.Exists(manifestPath) && DateTime.UtcNow < deadline && !proc.HasExited)
                await Task.Delay(200, TestContext.Current.CancellationToken);

            Check.That(File.Exists(manifestPath)).IsTrue();

            proc.CloseMainWindow();          // graceful: runs OnExit, hence SignalShuttingDown
            proc.WaitForExit(15_000);

            var finished = await Task.WhenAny(connected, Task.Delay(2000));
            Check.That(finished).Not.IsSameReferenceAs(connected);
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            proc.WaitForExit(5_000);
            try { Directory.Delete(profile, recursive: true); } catch { }
        }
    }

    private static string FindAppExe()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "src")))
            dir = Directory.GetParent(dir)?.FullName;

        var exe = Path.Combine(dir!, "src", "EyeCtrl.App", "bin", "Debug", "net10.0-windows",
                               "EyeCtrl.App.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException($"Build EyeCtrl.App first: {exe}");
        return exe;
    }
}
