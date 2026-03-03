namespace EyeCtrl.App.Tests.Fixtures;

using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

/// <summary>
/// Launches EyeCtrl.App.exe in --test-mode and provides helpers for UI tests.
/// Shared across all tests in a collection via xUnit ICollectionFixture.
/// </summary>
public class AppFixture : IAsyncLifetime
{
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    private Application? _app;
    private UIA3Automation? _automation;

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

    public async Task InitializeAsync()
    {
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

        // Wait for the toolbar window to appear (the last overlay shown at startup)
        await WaitForWindowAsync("Toolbar_Window", TimeSpan.FromSeconds(15));

        // Give WPF time to complete layout passes and register UIA AutomationPeers for
        // child elements. WPF registers peers asynchronously after Window.Show() returns,
        // so the window appears in UIA before its children do.
        await Task.Delay(1000);
    }

    public Task DisposeAsync()
    {
        try
        {
            _app?.Kill();
        }
        catch
        {
            // Process may have already exited
        }

        _automation?.Dispose();
        return Task.CompletedTask;
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
    /// Returns true if the app is currently paused (cursor circle is offscreen or not found).
    /// </summary>
    public bool IsAppPaused()
    {
        var cursorCircle = FindWindow("CursorCircle_Window");
        return cursorCircle == null || cursorCircle.Properties.IsOffscreen.Value;
    }

    /// <summary>
    /// Ensures the app is in unpaused state. If paused, dwells on the pause button to unpause.
    /// Call at the start of tests that require the app to be unpaused.
    /// </summary>
    public async Task EnsureAppUnpausedAsync()
    {
        if (!IsAppPaused()) return;

        var toolbar = FindWindow("Toolbar_Window");
        if (toolbar == null) return;
        var pauseBtn = FindElement(toolbar, "Toolbar_Button_Pause");
        if (pauseBtn == null) return;

        await SimulateDwellOn(pauseBtn);
        await Task.Delay(300); // Allow state change to propagate
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

        var toolbar = FindWindow("Toolbar_Window");
        if (toolbar == null) return;
        var kbBtn = FindElement(toolbar, "Toolbar_Button_Keyboard");
        if (kbBtn == null) return;

        await SimulateDwellOn(kbBtn);
        await Task.Delay(500); // Allow keyboard to close and UI to settle
    }

    // ── Private helpers ──

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
