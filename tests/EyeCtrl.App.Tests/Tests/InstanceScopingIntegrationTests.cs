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
/// and shows windows.
/// <para>
/// <b>It does not move the cursor — but only because every launch passes <c>--paused</c></b>; see
/// <see cref="ScopedTestArgs"/>. An earlier version of this file claimed "does not move the cursor"
/// while launching unpaused in <c>--test-mode</c>, which is precisely the configuration that fights
/// the user's assistive software for the cursor. Do not drop that flag.
/// </para>
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

    /// <summary>
    /// Arguments for every scoped launch here. <c>--paused</c> is a <b>safety</b> requirement on this
    /// machine, not a convenience.
    /// <para>
    /// <c>--test-mode</c> selects <c>MouseGazeProvider</c>, which polls <c>GetCursorPos</c> every
    /// 30 ms and feeds it to <c>GazeEngine.OnGazePoint</c> — which then calls
    /// <c>IInputSimulator.MoveCursor</c>. The app therefore reads the cursor and writes it back in a
    /// closed loop, and its <c>SendInput</c> calls interleave with those of the user's real
    /// assistive software (TD Control), which drives the same cursor from their gaze. The user
    /// controls this PC solely by eye tracking, so an unpaused test launch competes with their only
    /// input channel.
    /// </para>
    /// <para>
    /// <c>--paused</c> starts in <c>InteractionState.Paused</c>, and <c>OnGazePoint</c> returns
    /// before <c>MoveCursor</c> in that state — and before the fixation detector, so no dwell can
    /// fire a click either. Nothing here needs cursor movement: these tests assert a manifest, an
    /// exit code, and pipe silence. The pause happens after phase 2, so the manifest is unaffected.
    /// </para>
    /// </summary>
    private const string ScopedTestArgs = "--test-mode --paused";

    /// <summary>Profile directory a scoped instance resolves to, for setup and teardown.</summary>
    private static string ProfileFor(string instanceId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EyeCtrl", "instances", instanceId);

    private static string NewInstanceId() => "it" + Guid.NewGuid().ToString("N")[..10];

    [Fact]
    public async Task ScopedLaunch_ReportsEveryResourceScoped()
    {
        AppFixture.SkipIfDisabled();

        var instanceId = NewInstanceId();
        var profile = ProfileFor(instanceId);

        var psi = new ProcessStartInfo(FindAppExe()) { Arguments = ScopedTestArgs, UseShellExecute = false };
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

            // The profile must be a real scoped directory, not merely "different from live".
            // Pins the case-normalised comparison in App's phase 2 from the other side.
            var profileDir = root.GetProperty("resources").EnumerateArray()
                .First(r => r.GetProperty("name").GetString() == "ProfileDirectory")
                .GetProperty("effectiveValue").GetString()!;
            Check.That(profileDir).IsEqualTo(profile);

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
    }

    [Fact]
    public async Task ScopedLaunch_LeavesTheLiveRunValueUntouched()
    {
        AppFixture.SkipIfDisabled();

        // The regression this whole design exists for: a launch used to delete the live autostart
        // entry via SetStartOnBoot. Snapshot it and compare byte for byte afterwards.
        object? before;
        using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
            before = key?.GetValue("EyeCtrl");

        // With no live value there is nothing to preserve and the comparison degenerates to
        // null == null — green while proving nothing. Skip loudly rather than bank a false pass.
        // Deliberately NOT "fixed" by seeding a value first: a failed cleanup would leave EyeCtrl
        // autostarting at next logon on a machine whose only input channel is this app itself.
        Assert.SkipWhen(before is null,
            "No live HKCU\\…\\Run\\EyeCtrl value on this machine, so this cannot exercise the "
            + "regression it exists for. Enable start-on-boot and re-run to cover it.");

        var instanceId = NewInstanceId();
        var profile = ProfileFor(instanceId);
        var psi = new ProcessStartInfo(FindAppExe()) { Arguments = ScopedTestArgs, UseShellExecute = false };
        psi.Environment[InstanceScope.EnvVar] = instanceId;

        using var proc = Process.Start(psi)!;
        try
        {
            var manifestPath = Path.Combine(profile, InstanceManifest.FileName);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (!File.Exists(manifestPath) && DateTime.UtcNow < deadline && !proc.HasExited)
                await Task.Delay(200, TestContext.Current.CancellationToken);

            Check.That(File.Exists(manifestPath)).IsTrue();
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            proc.WaitForExit(5_000);
            try { Directory.Delete(profile, recursive: true); } catch { }
        }

        object? after;
        using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
            after = key?.GetValue("EyeCtrl");

        Check.That(after?.ToString()).IsEqualTo(before?.ToString());
    }

    [Fact]
    public void MalformedInstanceId_RefusesWithExitCode78()
    {
        AppFixture.SkipIfDisabled();

        // Replaces ScopedLaunchWithoutTestMode_RefusesWithExitCode78: the --test-mode gate was
        // dropped, so that launch now starts normally. A malformed id is the refusal that is still
        // safe to assert at process level — it exits before Serilog is initialised and before any
        // window exists, so nothing is shown and no input is simulated even for an instant.
        //
        // Deliberately NOT re-tested with a valid id and no --test-mode: that combination is now a
        // full gaze-driven app moving the real cursor, which must never be launched from a test on
        // this machine.
        var psi = new ProcessStartInfo(FindAppExe()) { UseShellExecute = false };
        psi.Environment[InstanceScope.EnvVar] = "bad id";   // space is illegal in an instance id

        using var proc = Process.Start(psi)!;
        Check.That(proc.WaitForExit(20_000)).IsTrue();
        Check.That(proc.ExitCode).IsEqualTo(InstanceStartupPolicy.RefusalExitCode);

        // No profile to clean up: the malformed-id branch refuses before resolving one, which is
        // the whole point of it not being loggable.
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
        var psi = new ProcessStartInfo(FindAppExe()) { Arguments = ScopedTestArgs, UseShellExecute = false };
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

            // ASSERT the preconditions, never assume them. An earlier version called
            // CloseMainWindow() and WaitForExit() and discarded both return values, so it passed
            // whether or not the app ever exited — and it never did: App.xaml sets
            // ShutdownMode="OnExplicitShutdown" and nothing wires a window close to Shutdown(), so
            // OnExit (hence SignalShuttingDown) never ran and "nothing connected" held vacuously.
            //
            // Skip rather than fail when no graceful exit can be provoked: the app currently has no
            // externally reachable clean-exit trigger (the tray Exit item is the only one, and
            // AppFixture Kills the app for the same reason). Skipping states that plainly instead
            // of banking a false pass. Give the app an exit affordance — a test-only
            // --exit-after-manifest switch, or UIA on the tray menu — and this starts covering the
            // spec's "a graceful exit sends nothing to eyectrl_watch" for real.
            //
            // The guarantee itself is NOT uncovered meanwhile: HeartbeatPublisherTests
            // .SignalShuttingDown_SendsNothing_WhenNotPublishing exercises the guard directly.
            Assert.SkipUnless(proc.CloseMainWindow(),
                "No externally reachable graceful-exit trigger: CloseMainWindow() closed nothing, "
                + "and ShutdownMode=OnExplicitShutdown means a window close would not exit anyway.");
            Assert.SkipUnless(proc.WaitForExit(15_000),
                "The app did not exit within 15s of CloseMainWindow(), so OnExit never ran and "
                + "this cannot distinguish a silent exit path from an absent one.");

            // OnExit's first act is Log.Information("Shutdown") (App.xaml.cs). Finding that line in
            // the scoped log directory is what distinguishes "the exit path ran and stayed silent"
            // from "the exit path never ran".
            Check.That(ScopedLogContains(profile, "Shutdown")).IsTrue();

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

    /// <summary>
    /// Whether any Serilog file in the instance's scoped log directory contains <paramref name="text"/>.
    /// Opened with full sharing because the app may still hold the file.
    /// </summary>
    private static bool ScopedLogContains(string profile, string text)
    {
        var logs = Path.Combine(profile, "logs");
        if (!Directory.Exists(logs)) return false;

        foreach (var file in Directory.EnumerateFiles(logs, "*.*"))
        {
            try
            {
                using var fs = new FileStream(
                    file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                if (reader.ReadToEnd().Contains(text, StringComparison.Ordinal)) return true;
            }
            catch (IOException) { /* rolled or locked — try the next */ }
        }
        return false;
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
