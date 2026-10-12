namespace EyeCtrl.App.Tests.Fixtures;

using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Launches EyeCtrl.App.exe in --test-mode and provides helpers for UI tests.
/// Shared across all tests in a collection via xUnit ICollectionFixture.
/// <para>
/// SETTINGS ISOLATION: the app reads <c>%APPDATA%/EyeCtrl/settings.json</c> once at launch and
/// writes it back (debounced) whenever settings change. When UI tests are enabled,
/// <see cref="InitializeAsync"/> backs the owner's file up to
/// <c>settings.json.ui_test_backup</c> and forces <c>general.simpleSettingsUi=false</c> so the
/// launched app starts in advanced mode; <see cref="DisposeAsync"/> kills the app, waits up to
/// 5&nbsp;s for the process to die (its debounced writer must be dead before the restore, or a
/// late save re-persists the forced value over the restored file), then moves the backup back.
/// A restore that fails with <c>IOException</c> or <c>UnauthorizedAccessException</c> never
/// throws from disposal (it would mask the collection's results): the backup stays on disk for
/// manual restore.
/// CAVEAT: a test-host crash mid-run leaves <c>settings.json.ui_test_backup</c> behind for
/// manual restore — and the next gated run REFUSES to start until it is resolved:
/// <see cref="InitializeAsync"/> throws rather than overwrite it, because the leftover backup
/// holds the owner's ORIGINAL while the live <c>settings.json</c> may already be test-mutated,
/// and copying over the backup would destroy the original (impl review I4/F3).
/// </para>
/// </summary>
public class AppFixture : IAsyncLifetime
{
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    private Application? _app;
    private UIA3Automation? _automation;
    private string? _settingsBackup;
    private bool _hadSettingsFile;

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EyeCtrl", "settings.json");

    private static readonly JsonSerializerOptions SettingsJsonOptions = new()
    {
        WriteIndented = true
    };

    public UIA3Automation Automation => _automation
        ?? throw new InvalidOperationException("Fixture not initialized");

    public Application App => _app
        ?? throw new InvalidOperationException("Fixture not initialized");

    /// <summary>
    /// Default dwell time for toolbar buttons (from default settings: 800ms).
    /// Tests add a margin to account for timer granularity.
    /// </summary>
    public int DefaultDwellMs => 800;
    public int DwellMarginMs => 300;

    /// <summary>
    /// Whether the app-launching UI tests are allowed to run. They are OPT-IN because they launch
    /// the real app and drive the OS cursor via <see cref="SetCursorPos"/>, which seizes control
    /// from a user operating the machine by eye-tracking. Enable with the environment variable
    /// <c>EYECTRL_UI_TESTS=1</c>. Combined with the <c>Category=UI</c> trait, a plain
    /// <c>dotnet test</c> neither launches the app nor moves the cursor.
    /// </summary>
    public static bool UiTestsEnabled =>
        Environment.GetEnvironmentVariable("EYECTRL_UI_TESTS") is "1" or "true" or "TRUE";

    /// <summary>Skips the current UI test (cleanly, no app launch) unless UI tests are opted in.</summary>
    public static void SkipIfDisabled()
    {
        if (!UiTestsEnabled)
            Assert.Skip("UI tests are opt-in: set EYECTRL_UI_TESTS=1 to run them. They launch the app and control the cursor, so they are excluded by default (also tagged Category=UI).");
    }

    public async ValueTask InitializeAsync()
    {
        // Do not launch the app or touch the cursor unless UI tests are explicitly enabled.
        if (!UiTestsEnabled)
            return;

        // Settings isolation (see class doc comment). The backup + force must happen BEFORE
        // Application.Launch: the app reads settings.json once at launch, so an in-test-method
        // guard cannot influence what the already-running app loaded — and it races the app's
        // debounced save, which can land after a restore and persist simpleSettingsUi=true
        // into the owner's real file.
        //
        // A leftover backup from a crashed run is the owner's ORIGINAL, with a possibly
        // test-mutated settings.json beside it. Copying over it would replace the original
        // with the mutated file (impl review I4/F3) — so its presence fails the whole UI
        // collection loudly instead. InvalidOperationException deliberately escapes the
        // degrade-to-no-isolation catch below: degrading here would run the suite against an
        // unknown settings state with the owner's only backup stranded mid-directory.
        var backupPath = SettingsPath + ".ui_test_backup";
        if (File.Exists(backupPath))
            throw new InvalidOperationException(
                $"Refusing to start UI tests: '{backupPath}' is left over from a crashed "
                + $"UI-test run and holds the owner's original settings.json, while "
                + $"'{SettingsPath}' may already be test-mutated. Restore it by hand (copy it "
                + "over settings.json, or delete it if the live file is already correct), then "
                + "re-run. See the CAVEAT in AppFixture's doc comment.");
        try
        {
            _hadSettingsFile = File.Exists(SettingsPath);
            if (_hadSettingsFile)
            {
                _settingsBackup = backupPath;
                File.Copy(SettingsPath, _settingsBackup, overwrite: false); // never clobber a backup
                ForceSimpleSettingsUi(SettingsPath, simple: false); // the launched app reads advanced
            }
            // No existing file: nothing to force or restore — advanced is the default the app
            // will write.
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A throw here fails the whole UI collection, so every step is guarded: degrade to
            // running WITHOUT isolation rather than aborting. The owner's file is left as-is
            // and the backup is discarded so DisposeAsync will not restore over an unknown state.
            _settingsBackup = null;
            _hadSettingsFile = false;
        }

        _automation = new UIA3Automation();

        // Find the app binary — build it first to ensure it's current
        var appDir = FindAppProjectDir();
        var buildResult = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{appDir}\" -c Debug --no-restore -v quiet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        buildResult?.WaitForExit(60_000);

        // Locate the built exe
        var exePath = FindAppExe(appDir);

        // Launch in test mode
        _app = Application.Launch(new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = "--test-mode"
        });

        // Wait for the toolbar window to appear (the last overlay shown at startup).
        // Kept short so an environment that can't show the window fails fast instead of idling.
        await WaitForWindowAsync("Toolbar_Window", TimeSpan.FromSeconds(6));

        // Give WPF time to complete layout passes and register UIA AutomationPeers for
        // child elements. WPF registers peers asynchronously after Window.Show() returns,
        // so the window appears in UIA before its children do.
        await Task.Delay(1000);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            _app?.Kill();
        }
        catch
        {
            // Process may have already exited
        }

        // Restore the owner's settings AFTER the kill: the app's debounced writer may still
        // hold a pending save, and the process must be DEAD before the restore or that save
        // lands on the restored file and re-persists the forced value.
        if (UiTestsEnabled && _settingsBackup is not null)
        {
            if (_app is not null)
            {
                try
                {
                    Process.GetProcessById(_app.ProcessId).WaitForExit(5000);
                }
                catch (ArgumentException) { /* already gone */ }
                catch (InvalidOperationException) { /* process object no longer usable */ }
            }

            try
            {
                if (_hadSettingsFile) File.Move(_settingsBackup, SettingsPath, overwrite: true);
                else if (File.Exists(SettingsPath)) File.Delete(SettingsPath); // unreachable while
                // _settingsBackup is gated on _hadSettingsFile — kept defensive
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Never throw from disposal (it would mask the collection's test results);
                // the backup file stays on disk for manual restore — see the class doc comment.
                // UnauthorizedAccessException joins IOException (impl review F3, ledger T13):
                // an ACL-locked settings directory must not take the results down either.
            }
        }

        _automation?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Find a window by its AutomationId in the app's process.
    /// FlaUI searches all top-level windows owned by the process.
    /// </summary>
    public AutomationElement? FindWindow(string automationId)
    {
        var cf = _automation!.ConditionFactory;
        var desktop = _automation.GetDesktop();
        var windows = desktop.FindAllDescendants(cf.ByAutomationId(automationId));

        // Filter to our process only
        foreach (var w in windows)
        {
            try
            {
                if (w.Properties.ProcessId.Value == _app!.ProcessId)
                    return w;
            }
            catch
            {
                // Element may be stale
            }
        }

        return null;
    }

    /// <summary>
    /// Wait for a window with the given AutomationId to appear in the UIA tree.
    /// Returns as soon as the element is non-null (may still be offscreen).
    /// Use WaitForWindowVisibleAsync when you also need the window to be on-screen.
    /// </summary>
    public async Task<AutomationElement> WaitForWindowAsync(
        string automationId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var window = FindWindow(automationId);
            if (window != null)
                return window;

            await Task.Delay(250);
        }

        throw new TimeoutException(
            $"Window '{automationId}' did not appear within {timeout.TotalSeconds}s");
    }

    /// <summary>
    /// Wait for a window to be visible on screen (found AND not offscreen).
    /// After WPF Window.Show(), the automation peer may be briefly stale (IsOffscreen=true)
    /// before the next layout pass completes — this retries until the window is truly visible.
    /// </summary>
    public async Task<AutomationElement> WaitForWindowVisibleAsync(
        string automationId, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(5);
        var deadline = DateTime.UtcNow + effectiveTimeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var window = FindWindow(automationId);
                if (window != null && window.Properties.IsOffscreen.Value == false)
                    return window;
            }
            catch
            {
                // Element may be stale during transition; retry
            }

            await Task.Delay(250);
        }

        throw new TimeoutException(
            $"Window '{automationId}' did not become visible within {effectiveTimeout.TotalSeconds}s");
    }

    /// <summary>
    /// Find an element by AutomationId within a specific window.
    /// Uses TreeScope.Descendants to find nested elements.
    /// </summary>
    public AutomationElement? FindElement(AutomationElement parent, string automationId)
    {
        var cf = _automation!.ConditionFactory;
        return parent.FindFirstDescendant(cf.ByAutomationId(automationId));
    }

    /// <summary>
    /// Wait for a child element to appear within a parent, retrying until timeout.
    /// Useful because WPF registers AutomationPeers for children asynchronously
    /// after the parent window is first visible in the UIA tree.
    /// </summary>
    public async Task<AutomationElement> WaitForElementAsync(
        AutomationElement parent, string automationId, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            var element = FindElement(parent, automationId);
            if (element != null)
                return element;

            await Task.Delay(100);
        }

        throw new TimeoutException(
            $"Element '{automationId}' did not appear within {(timeout ?? TimeSpan.FromSeconds(5)).TotalSeconds}s");
    }

    /// <summary>
    /// Find all elements matching an AutomationId prefix within a window.
    /// Useful for finding all toolbar buttons (Toolbar_Button_*).
    /// </summary>
    public AutomationElement[] FindElements(AutomationElement parent, string automationIdPrefix)
    {
        var cf = _automation!.ConditionFactory;
        var all = parent.FindAllDescendants();
        return all.Where(e =>
        {
            try
            {
                return e.Properties.AutomationId.Value?.StartsWith(automationIdPrefix) == true;
            }
            catch { return false; }
        }).ToArray();
    }

    /// <summary>
    /// Simulate a dwell on a specific screen position.
    /// Moves the mouse cursor to the position, waits for the dwell time, then
    /// moves the cursor to the safe corner (1,1).  The automatic cursor-away
    /// step is critical: after a dwell fires, the DwellDetector resets to 0 but
    /// immediately starts accumulating again while the cursor stays on the target.
    /// If the cursor is not moved, a second unintended dwell fires ~800ms later,
    /// undoing the intended action (e.g. re-pausing after an unpause).
    /// </summary>
    public async Task SimulateDwellAt(int screenX, int screenY, int? dwellMs = null)
    {
        var totalWait = (dwellMs ?? DefaultDwellMs) + DwellMarginMs;
        SetCursorPos(screenX, screenY);
        await Task.Delay(totalWait);
        SetCursorPos(1, 1); // move away so next dwell starts with zero accumulation
    }

    /// <summary>
    /// Simulate a dwell on the center of an element.
    /// </summary>
    public async Task SimulateDwellOn(AutomationElement element, int? dwellMs = null)
    {
        var rect = element.BoundingRectangle;
        var centerX = (int)(rect.X + rect.Width / 2);
        var centerY = (int)(rect.Y + rect.Height / 2);
        await SimulateDwellAt(centerX, centerY, dwellMs);
    }

    /// <summary>
    /// Move cursor away from all interactive elements (to a safe corner).
    /// </summary>
    public void MoveCursorAway()
    {
        SetCursorPos(1, 1);
    }

    /// <summary>
    /// Returns true if the app is currently paused. Pause is signalled by the PAUSE INDICATOR,
    /// not the cursor circle: the ring also hides while the settings modal is open, which used
    /// to make this check false-positive and strand every later test in a bogus recovery path.
    /// </summary>
    public bool IsAppPaused()
    {
        var indicator = FindWindow("PauseIndicator_Window");
        return indicator is not null && indicator.Properties.IsOffscreen.Value == false;
    }

    /// <summary>
    /// Full inter-test hygiene: closes the settings modal, closes the keyboard, unpauses if
    /// paused, and summons the toolbar band. The band AUTO-HIDES after ~5s of idle gaze and the
    /// settings/keyboard modals hide the ring — tests written before auto-hide shipped assumed
    /// everything stayed visible, which is how this suite rotted. Any test that asserts on the
    /// ring, the band, or dwell behavior should start with this.
    /// </summary>
    public async Task EnsureGazeUiVisibleAsync()
    {
        await CloseSettingsIfOpenAsync();
        if (IsAppPaused())
        {
            await SummonToolbarFromEdgeAsync();
            var toolbar = await WaitForWindowVisibleAsync("Toolbar_Window", TimeSpan.FromSeconds(5));
            var pauseBtn = await WaitForElementAsync(toolbar, "Toolbar_Button_Pause", TimeSpan.FromSeconds(5));
            await SimulateDwellOn(pauseBtn);
            await Task.Delay(300); // Allow state change to propagate
        }
        await SummonToolbarFromEdgeAsync();
        await EnsureKeyboardClosedAsync();
    }

    /// <summary>
    /// Waits until the pause indicator matches <paramref name="visible"/>. It appears only after
    /// the toolbar's slide-out animation completes on pause, so a fixed delay after the dwell
    /// races the animation.
    /// </summary>
    public async Task WaitForPauseIndicatorAsync(bool visible, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(5);
        var deadline = DateTime.UtcNow + effectiveTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (IsPauseIndicatorVisible() == visible) return;
            await Task.Delay(150);
        }

        throw new TimeoutException(
            $"Pause indicator did not become {(visible ? "visible" : "hidden")} within {effectiveTimeout.TotalSeconds}s");
    }

    /// <summary>Whether the pause indicator is currently on screen — the product's pause signal.</summary>
    public bool IsPauseIndicatorVisible()
    {
        var indicator = FindWindow("PauseIndicator_Window");
        return indicator is not null && indicator.Properties.IsOffscreen.Value == false;
    }

    /// <summary>
    /// Ensures the app is in unpaused state. While paused the toolbar band is slid out and HIDDEN
    /// (the click-through <c>PauseIndicator_Window</c> marks the docked edge); the unpause path
    /// is the product's own: hold gaze at the edge until the auto-hide controller summons the
    /// band back, then dwell the Pause/Play button. The settings modal is closed first because
    /// it HIDES the cursor circle — tests asserting on the ring would otherwise misread an open
    /// settings window as "paused".
    /// </summary>
    public async Task EnsureAppUnpausedAsync()
    {
        await CloseSettingsIfOpenAsync();
        if (!IsAppPaused()) return;

        await SummonToolbarFromEdgeAsync();
        var toolbar = await WaitForWindowVisibleAsync("Toolbar_Window", TimeSpan.FromSeconds(5));
        var pauseBtn = await WaitForElementAsync(toolbar, "Toolbar_Button_Pause", TimeSpan.FromSeconds(5));
        await SimulateDwellOn(pauseBtn);
        await Task.Delay(300); // Allow state change to propagate
    }

    /// <summary>Closes the settings window via the UIA window pattern if it is open.</summary>
    public async Task CloseSettingsIfOpenAsync()
    {
        var settings = FindWindow("Settings_Window");
        if (settings is null || settings.Properties.IsOffscreen.Value) return;
        try { settings.AsWindow().Close(); }
        catch { /* already closing */ }
        await Task.Delay(400); // let OnSettingsWindowClosed restore the ring
    }

    /// <summary>
    /// Holds the cursor (test-mode gaze) at the docked-edge summon strip long enough for the
    /// auto-hide controller's summon delay, then waits for the toolbar band to slide back in.
    /// Uses the pause indicator's bounds when paused; otherwise the bottom edge of the desktop
    /// (the docked edge, in physical pixels).
    /// </summary>
    public async Task SummonToolbarFromEdgeAsync()
    {
        int x, y;
        var indicator = FindWindow("PauseIndicator_Window");
        if (indicator is not null && indicator.Properties.IsOffscreen.Value == false)
        {
            var rect = indicator.BoundingRectangle;
            x = (int)(rect.X + rect.Width / 2);
            y = (int)(rect.Y + rect.Height / 2);
        }
        else
        {
            var screen = _automation!.GetDesktop().BoundingRectangle;
            x = (int)(screen.X + screen.Width / 2);
            y = (int)(screen.Y + screen.Height - 5);
        }

        SetCursorPos(x, y);
        // Summon delay (default 400ms) + slide animation; generous margin for loaded runs.
        await Task.Delay(1500);
        await WaitForWindowVisibleAsync("Toolbar_Window", TimeSpan.FromSeconds(5));
        MoveCursorAway();
    }

    /// <summary>
    /// Returns true if the keyboard window is currently visible on screen.
    /// </summary>
    public bool IsKeyboardVisible()
    {
        var keyboard = FindWindow("Keyboard_Window");
        return keyboard != null && keyboard.Properties.IsOffscreen.Value == false;
    }

    /// <summary>
    /// Ensures the keyboard is closed. If open, dwells on the keyboard button to close it.
    /// Call at the start of tests that assume the keyboard starts closed.
    /// </summary>
    public async Task EnsureKeyboardClosedAsync()
    {
        if (!IsKeyboardVisible()) return;

        // The band may be auto-hidden; summon it so the keyboard button is where we dwell.
        await SummonToolbarFromEdgeAsync();
        var toolbar = await WaitForWindowVisibleAsync("Toolbar_Window", TimeSpan.FromSeconds(5));
        var kbBtn = await WaitForElementAsync(toolbar, "Toolbar_Button_Keyboard", TimeSpan.FromSeconds(5));

        await SimulateDwellOn(kbBtn);
        await Task.Delay(500); // Allow keyboard to close and UI to settle
    }

    // ── Private helpers ──

    /// <summary>
    /// Rewrites <paramref name="path"/> with <c>general.simpleSettingsUi</c> set to
    /// <paramref name="simple"/>, creating the <c>general</c> node when missing. Only the
    /// fixture-level settings guard calls this (see the class doc comment); the rest of the
    /// file round-trips through <see cref="JsonNode"/> unchanged.
    /// </summary>
    private static void ForceSimpleSettingsUi(string path, bool simple)
    {
        if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
            throw new JsonException($"Settings file '{path}' is not a JSON object.");

        var general = root["general"] as JsonObject ?? new JsonObject();
        general["simpleSettingsUi"] = simple;
        root["general"] = general;
        File.WriteAllText(path, root.ToJsonString(SettingsJsonOptions));
    }

    private static string FindAppProjectDir()
    {
        // Walk up from test assembly to find the repo root, then into src/EyeCtrl.App
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        while (dir != null)
        {
            var candidate = Path.Combine(dir, "src", "EyeCtrl.App");
            if (Directory.Exists(candidate))
                return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new DirectoryNotFoundException(
            "Could not find src/EyeCtrl.App from test assembly location");
    }

    private static string FindAppExe(string projectDir)
    {
        var binDir = Path.Combine(projectDir, "bin", "Debug", "net10.0-windows");
        var exe = Path.Combine(binDir, "EyeCtrl.App.exe");
        if (File.Exists(exe))
            return exe;

        throw new FileNotFoundException(
            $"App executable not found at {exe}. Did the build succeed?");
    }
}
