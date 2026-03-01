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
    /// Wait for a window with the given AutomationId to appear.
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
    /// Find an element by AutomationId within a specific window.
    /// Uses TreeScope.Descendants to find nested elements.
    /// </summary>
    public AutomationElement? FindElement(AutomationElement parent, string automationId)
    {
        var cf = _automation!.ConditionFactory;
        return parent.FindFirstDescendant(cf.ByAutomationId(automationId));
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
    /// Moves the mouse cursor to the position and waits for the dwell time.
    /// In test mode, MouseGazeProvider reads cursor position as gaze input.
    /// </summary>
    public async Task SimulateDwellAt(int screenX, int screenY, int? dwellMs = null)
    {
        var totalWait = (dwellMs ?? DefaultDwellMs) + DwellMarginMs;
        SetCursorPos(screenX, screenY);
        await Task.Delay(totalWait);
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
        var binDir = Path.Combine(projectDir, "bin", "Debug", "net8.0-windows");
        var exe = Path.Combine(binDir, "EyeCtrl.App.exe");
        if (File.Exists(exe))
            return exe;

        throw new FileNotFoundException(
            $"App executable not found at {exe}. Did the build succeed?");
    }
}
