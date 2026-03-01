# UI Testing with FlaUI — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add automated UI/E2E tests for the WPF overlay app using FlaUI, so regressions are caught without manual testing.

**Architecture:** FlaUI.UIA3 + xUnit launches the real app with `--test-mode` (uses MouseGazeProvider instead of Tobii, skips splash delay). A shared `AppFixture` manages process lifecycle. Tests simulate dwell by moving the mouse cursor and waiting, then assert on element state via `AutomationProperties.AutomationId`.

**Tech Stack:** FlaUI.UIA3, xUnit, .NET 8.0-windows

**Design doc:** `docs/plans/2026-03-01-ui-testing-design.md`

---

## Task 1: Create the test project and add to solution

**Files:**
- Create: `tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj`
- Modify: `EyeCtrl.slnx`

**Step 1: Create the test project directory**

```bash
mkdir -p tests/EyeCtrl.App.Tests
```

**Step 2: Create the .csproj file**

Create `tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <UseWPF>true</UseWPF>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.0" />
    <PackageReference Include="FlaUI.UIA3" Version="4.0.0" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.8.0" />
    <PackageReference Include="xunit" Version="2.5.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.5.3" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

</Project>
```

> **Why `UseWPF`?** FlaUI tests reference WPF types like `AutomationProperties` when
> checking elements. The test project itself doesn't ship UI, but needs the WPF SDK
> references.

> **No `ProjectReference` to `EyeCtrl.App`** — FlaUI launches the app as a separate
> process via its compiled `.exe`. The test project is deliberately decoupled.

**Step 3: Add the project to the solution**

```bash
cd /c/Users/andre/source/repos/eyectrl
dotnet sln EyeCtrl.slnx add tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --solution-folder tests
```

**Step 4: Verify it builds**

```bash
dotnet build tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

**Step 5: Commit**

```bash
git add tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj EyeCtrl.slnx
git commit -m "chore: add EyeCtrl.App.Tests project with FlaUI"
```

---

## Task 2: Add `--test-mode` CLI flag to the app

The app already falls back to `MouseGazeProvider` when Tobii isn't available (try/catch
in `App.xaml.cs:69-79`). Test mode makes this **explicit** and also skips the splash
screen delay, making test startup faster and deterministic.

**Files:**
- Modify: `src/EyeCtrl.App/App.xaml.cs`

**Step 1: Add a static property to expose test mode**

At the top of the `App` class (after line 50, below the `_keyboardVisible` field), add:

```csharp
/// <summary>
/// When true, the app uses MouseGazeProvider and skips the splash delay.
/// Activated by launching with --test-mode CLI argument.
/// </summary>
public static bool IsTestMode { get; private set; }
```

**Step 2: Parse CLI args at the start of `OnStartup`**

After `base.OnStartup(e);` (line 54), before the splash screen block, add:

```csharp
IsTestMode = e.Args.Contains("--test-mode", StringComparer.OrdinalIgnoreCase);
```

**Step 3: Skip splash screen in test mode**

Replace the splash screen block (lines 57-58) with:

```csharp
SplashWindow? splash = null;
if (!IsTestMode)
{
    splash = new SplashWindow();
    splash.Show();
}
```

And update the splash close block (line 237) to:

```csharp
splash?.Close();
```

**Step 4: Force MouseGazeProvider in test mode**

Replace the gaze provider block (lines 68-79) with:

```csharp
IGazeProvider gazeProvider;
if (IsTestMode)
{
    gazeProvider = new MouseGazeProvider();
}
else
{
    try
    {
        gazeProvider = new TobiiInteractionLibProvider(
            SystemParameters.PrimaryScreenWidth,
            SystemParameters.PrimaryScreenHeight);
    }
    catch
    {
        gazeProvider = new MouseGazeProvider();
    }
}
```

**Step 5: Build and verify**

```bash
dotnet build src/EyeCtrl.App/EyeCtrl.App.csproj
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

**Step 6: Manual smoke test — launch with --test-mode**

```bash
dotnet run --project src/EyeCtrl.App -- --test-mode
```

Expected: App starts instantly (no splash), cursor circle follows mouse. Close via system tray.

**Step 7: Commit**

```bash
git add src/EyeCtrl.App/App.xaml.cs
git commit -m "feat: add --test-mode CLI flag for UI testing"
```

---

## Task 3: Add AutomationIds to overlay elements

FlaUI finds elements by `AutomationProperties.AutomationId`. Since overlays are
100% code-behind (no XAML-bindable elements), we set AutomationIds programmatically.

**Naming convention:** `OverlayName_ElementType_Name`

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/CursorCircleWindow.xaml` (XAML, one line)
- Modify: `src/EyeCtrl.App/Overlays/CursorCircleWindow.xaml.cs`
- Modify: `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs`
- Modify: `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs`
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs`

### Step 1: CursorCircleWindow — set window AutomationId

In `CursorCircleWindow.xaml`, add `x:Name="CursorCircleRoot"` and
`AutomationProperties.AutomationId="CursorCircle_Window"` to the `<Window>` element:

```xml
<Window x:Class="EyeCtrl.App.Overlays.CursorCircleWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        AutomationProperties.AutomationId="CursorCircle_Window"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        Topmost="True" ShowInTaskbar="False"
        Width="250" Height="250">
```

Also add an AutomationId to the Circle ellipse:

```xml
    <Canvas>
        <Ellipse x:Name="Circle"
                 AutomationProperties.AutomationId="CursorCircle_Ellipse_Main"
                 Stroke="Lime" StrokeThickness="2"
                 Width="250" Height="250"
                 Canvas.Left="0" Canvas.Top="0"/>
    </Canvas>
```

### Step 2: ToolbarWindow — set window and button AutomationIds

In `ToolbarWindow.xaml`, add AutomationId to the window:

```xml
<Window x:Class="EyeCtrl.App.Overlays.ToolbarWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        AutomationProperties.AutomationId="Toolbar_Window"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        Topmost="True" ShowInTaskbar="False"
        SizeToContent="WidthAndHeight">
```

In `ToolbarWindow.xaml.cs`, inside the `AddButton` method, after creating the
`container` Border (after line 105), add:

```csharp
System.Windows.Automation.AutomationProperties.SetAutomationId(container, $"Toolbar_Button_{id}");
```

This gives each button a unique id: `Toolbar_Button_Pause`, `Toolbar_Button_Zoom`,
`Toolbar_Button_Keyboard`, `Toolbar_Button_Settings`.

Add the import at the top of the file if not already present — it's already available
via `System.Windows` namespace with WPF projects, but the class is in
`System.Windows.Automation` which is available automatically.

### Step 3: KeyboardWindow — set window AutomationId

In `KeyboardWindow.xaml`, add AutomationId to the window element:

```
AutomationProperties.AutomationId="Keyboard_Window"
```

In `KeyboardWindow.xaml.cs`, in the method that creates key visuals (inside
`RebuildKeys()`), after creating each key's container Border, add:

```csharp
System.Windows.Automation.AutomationProperties.SetAutomationId(
    container, $"Keyboard_Key_{keyDef.Id}");
```

For prediction bar items, after creating each prediction Border, add:

```csharp
System.Windows.Automation.AutomationProperties.SetAutomationId(
    border, $"Keyboard_Prediction_{i}");
```

### Step 4: SettingsWindow — set window and tab AutomationIds

In `SettingsWindow.xaml`, add AutomationId to the window element:

```
AutomationProperties.AutomationId="Settings_Window"
```

In `SettingsWindow.xaml.cs`, where tab buttons are created (the loop over `TabDefs`),
after creating each tab Border, add:

```csharp
System.Windows.Automation.AutomationProperties.SetAutomationId(
    border, $"Settings_Tab_{def.Id}");
```

For the content panel, add:

```csharp
System.Windows.Automation.AutomationProperties.SetAutomationId(
    ContentPanel, "Settings_Panel_Content");
```

### Step 5: Build and verify

```bash
dotnet build src/EyeCtrl.App/EyeCtrl.App.csproj
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

### Step 6: Commit

```bash
git add src/EyeCtrl.App/Overlays/CursorCircleWindow.xaml
git add src/EyeCtrl.App/Overlays/CursorCircleWindow.xaml.cs
git add src/EyeCtrl.App/Overlays/ToolbarWindow.xaml
git add src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs
git add src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs
git add src/EyeCtrl.App/Views/SettingsWindow.xaml.cs
git commit -m "feat: add AutomationIds to overlays for UI testing"
```

---

## Task 4: Create the AppFixture (test infrastructure)

The `AppFixture` launches the app process in test mode, waits for overlays to appear,
and provides helper methods for tests. It uses xUnit's `IAsyncLifetime` for setup/teardown.

**Files:**
- Create: `tests/EyeCtrl.App.Tests/Fixtures/AppFixture.cs`

### Step 1: Create Fixtures directory

```bash
mkdir -p tests/EyeCtrl.App.Tests/Fixtures
```

### Step 2: Write the AppFixture

Create `tests/EyeCtrl.App.Tests/Fixtures/AppFixture.cs`:

```csharp
namespace EyeCtrl.App.Tests.Fixtures;

using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using System.Diagnostics;
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
    /// Find all elements matching an AutomationId pattern within a window.
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
```

### Step 3: Create the xUnit collection definition

Create `tests/EyeCtrl.App.Tests/Fixtures/AppCollection.cs`:

```csharp
namespace EyeCtrl.App.Tests.Fixtures;

/// <summary>
/// xUnit collection that shares a single AppFixture across all test classes.
/// Only one app instance runs for all UI tests.
/// </summary>
[CollectionDefinition("App")]
public class AppCollection : ICollectionFixture<AppFixture>
{
}
```

### Step 4: Build the test project

```bash
dotnet build tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

### Step 5: Commit

```bash
git add tests/EyeCtrl.App.Tests/Fixtures/AppFixture.cs
git add tests/EyeCtrl.App.Tests/Fixtures/AppCollection.cs
git commit -m "feat: add AppFixture for FlaUI UI test infrastructure"
```

---

## Task 5: Write tests — App launch and overlay visibility

**Files:**
- Create: `tests/EyeCtrl.App.Tests/Tests/AppLaunchTests.cs`

### Step 1: Create Tests directory

```bash
mkdir -p tests/EyeCtrl.App.Tests/Tests
```

### Step 2: Write the test class

Create `tests/EyeCtrl.App.Tests/Tests/AppLaunchTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Tests;

using EyeCtrl.App.Tests.Fixtures;

/// <summary>
/// Verifies the app launches correctly in test mode and shows expected overlays.
/// </summary>
[Collection("App")]
[Trait("Category", "UI")]
public class AppLaunchTests
{
    private readonly AppFixture _fixture;

    public AppLaunchTests(AppFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void App_LaunchesInTestMode_CursorCircleIsVisible()
    {
        var window = _fixture.FindWindow("CursorCircle_Window");

        Assert.NotNull(window);
        Assert.True(window!.Properties.IsOffscreen.Value == false,
            "Cursor circle window should be visible on screen");
    }

    [Fact]
    public void App_LaunchesInTestMode_ToolbarIsVisible()
    {
        var window = _fixture.FindWindow("Toolbar_Window");

        Assert.NotNull(window);
        Assert.True(window!.Properties.IsOffscreen.Value == false,
            "Toolbar window should be visible on screen");
    }

    [Fact]
    public void Toolbar_HasFourButtons()
    {
        var toolbar = _fixture.FindWindow("Toolbar_Window");
        Assert.NotNull(toolbar);

        var buttons = _fixture.FindElements(toolbar!, "Toolbar_Button_");

        Assert.Equal(4, buttons.Length);
    }

    [Fact]
    public void Toolbar_ButtonsHaveCorrectIds()
    {
        var toolbar = _fixture.FindWindow("Toolbar_Window");
        Assert.NotNull(toolbar);

        var expectedIds = new[]
        {
            "Toolbar_Button_Pause",
            "Toolbar_Button_Zoom",
            "Toolbar_Button_Keyboard",
            "Toolbar_Button_Settings"
        };

        foreach (var id in expectedIds)
        {
            var button = _fixture.FindElement(toolbar!, id);
            Assert.NotNull(button);
        }
    }

    [Fact]
    public void CursorCircle_HasMainEllipse()
    {
        var cursorCircle = _fixture.FindWindow("CursorCircle_Window");
        Assert.NotNull(cursorCircle);

        var ellipse = _fixture.FindElement(cursorCircle!, "CursorCircle_Ellipse_Main");
        Assert.NotNull(ellipse);
    }
}
```

### Step 3: Run the tests to verify they pass

```bash
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category=UI" -v normal
```

Expected: 5 tests pass. If any fail, investigate — likely an AutomationId isn't
being found (check that `WS_EX_TOOLWINDOW` windows are discoverable via
`TreeScope.Descendants` on the desktop).

> **Troubleshooting:** If windows aren't found, the `FindWindow` method searches
> the desktop's descendants filtered by process ID. `WS_EX_TOOLWINDOW` windows
> may require this approach rather than `Application.GetAllTopLevelWindows()`.

### Step 4: Commit

```bash
git add tests/EyeCtrl.App.Tests/Tests/AppLaunchTests.cs
git commit -m "test: add app launch and overlay visibility tests"
```

---

## Task 6: Write tests — Pause toggle

**Files:**
- Create: `tests/EyeCtrl.App.Tests/Tests/ToolbarTests.cs`

### Step 1: Write the test class

Create `tests/EyeCtrl.App.Tests/Tests/ToolbarTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Tests;

using EyeCtrl.App.Tests.Fixtures;

/// <summary>
/// Tests toolbar interactions: pause toggle, button state changes.
/// </summary>
[Collection("App")]
[Trait("Category", "UI")]
public class ToolbarTests
{
    private readonly AppFixture _fixture;

    public ToolbarTests(AppFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PauseButton_Dwell_HidesCursorCircle()
    {
        // Arrange — make sure we start in non-paused state
        var cursorCircle = _fixture.FindWindow("CursorCircle_Window");
        Assert.NotNull(cursorCircle);

        // Check initial state: cursor circle is visible
        var wasVisible = cursorCircle!.Properties.IsOffscreen.Value == false;
        Assert.True(wasVisible, "Cursor circle should be visible initially");

        // Act — dwell on pause button
        var toolbar = _fixture.FindWindow("Toolbar_Window");
        Assert.NotNull(toolbar);
        var pauseBtn = _fixture.FindElement(toolbar!, "Toolbar_Button_Pause");
        Assert.NotNull(pauseBtn);

        await _fixture.SimulateDwellOn(pauseBtn!);

        // Assert — cursor circle should now be hidden
        // Need to re-find because properties may be cached
        await Task.Delay(200); // Let UI update propagate
        cursorCircle = _fixture.FindWindow("CursorCircle_Window");

        // In paused state, the circle window is hidden (not visible)
        var isHidden = cursorCircle == null
            || cursorCircle.Properties.IsOffscreen.Value == true;
        Assert.True(isHidden, "Cursor circle should be hidden after pause");

        // Cleanup — dwell on pause again to unpause
        toolbar = _fixture.FindWindow("Toolbar_Window");
        pauseBtn = _fixture.FindElement(toolbar!, "Toolbar_Button_Pause");
        if (pauseBtn != null)
            await _fixture.SimulateDwellOn(pauseBtn);

        // Move cursor away so it doesn't interfere with other tests
        _fixture.MoveCursorAway();
        await Task.Delay(500);
    }

    [Fact]
    public async Task PauseButton_Dwell_TogglesBackToResume()
    {
        // Arrange — pause first
        var toolbar = _fixture.FindWindow("Toolbar_Window");
        Assert.NotNull(toolbar);
        var pauseBtn = _fixture.FindElement(toolbar!, "Toolbar_Button_Pause");
        Assert.NotNull(pauseBtn);

        await _fixture.SimulateDwellOn(pauseBtn!);
        await Task.Delay(200);

        // Act — unpause
        toolbar = _fixture.FindWindow("Toolbar_Window");
        pauseBtn = _fixture.FindElement(toolbar!, "Toolbar_Button_Pause");
        Assert.NotNull(pauseBtn);

        await _fixture.SimulateDwellOn(pauseBtn!);
        await Task.Delay(200);

        // Assert — cursor circle should reappear
        var cursorCircle = _fixture.FindWindow("CursorCircle_Window");
        Assert.NotNull(cursorCircle);
        Assert.True(cursorCircle!.Properties.IsOffscreen.Value == false,
            "Cursor circle should be visible after unpause");

        _fixture.MoveCursorAway();
        await Task.Delay(500);
    }
}
```

### Step 2: Run the tests

```bash
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "FullyQualifiedName~ToolbarTests" -v normal
```

Expected: 2 tests pass.

> **Note:** These tests are ordered carefully — each test ensures it cleans up
> by unpausing if it paused. Since all tests share the same app instance via
> the collection fixture, state management matters.

### Step 3: Commit

```bash
git add tests/EyeCtrl.App.Tests/Tests/ToolbarTests.cs
git commit -m "test: add toolbar pause toggle tests"
```

---

## Task 7: Write tests — Settings window and tabs

**Files:**
- Create: `tests/EyeCtrl.App.Tests/Tests/SettingsTests.cs`

### Step 1: Write the test class

Create `tests/EyeCtrl.App.Tests/Tests/SettingsTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Tests;

using EyeCtrl.App.Tests.Fixtures;

/// <summary>
/// Tests settings window: opens, tabs are present, tab navigation works.
/// </summary>
[Collection("App")]
[Trait("Category", "UI")]
public class SettingsTests
{
    private readonly AppFixture _fixture;

    public SettingsTests(AppFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SettingsButton_Dwell_OpensSettingsWindow()
    {
        // Arrange
        var toolbar = _fixture.FindWindow("Toolbar_Window");
        Assert.NotNull(toolbar);
        var settingsBtn = _fixture.FindElement(toolbar!, "Toolbar_Button_Settings");
        Assert.NotNull(settingsBtn);

        // Act — dwell on settings button
        await _fixture.SimulateDwellOn(settingsBtn!);
        await Task.Delay(500); // Wait for window to appear

        // Assert — settings window should appear
        var settings = _fixture.FindWindow("Settings_Window");
        Assert.NotNull(settings);

        // Cleanup — close settings (dwell on close button or find alternative)
        // For now, move cursor away — settings will be closed by other tests
        // or stays open (subsequent tests can use it)
        _fixture.MoveCursorAway();
    }

    [Fact]
    public async Task SettingsWindow_HasSevenTabs()
    {
        // Ensure settings is open
        var settings = _fixture.FindWindow("Settings_Window");
        if (settings == null)
        {
            // Open it
            var toolbar = _fixture.FindWindow("Toolbar_Window");
            var settingsBtn = _fixture.FindElement(toolbar!, "Toolbar_Button_Settings");
            await _fixture.SimulateDwellOn(settingsBtn!);
            await Task.Delay(500);
            settings = _fixture.FindWindow("Settings_Window");
        }
        Assert.NotNull(settings);

        // Assert — find all 7 tab buttons
        var expectedTabs = new[]
        {
            "Settings_Tab_Cursor",
            "Settings_Tab_Dwell",
            "Settings_Tab_Zoom",
            "Settings_Tab_Keyboard",
            "Settings_Tab_Toolbar",
            "Settings_Tab_Theme",
            "Settings_Tab_General"
        };

        foreach (var tabId in expectedTabs)
        {
            var tab = _fixture.FindElement(settings!, tabId);
            Assert.NotNull(tab);
        }

        _fixture.MoveCursorAway();
    }

    [Fact]
    public async Task SettingsWindow_HasContentPanel()
    {
        // Ensure settings is open
        var settings = _fixture.FindWindow("Settings_Window");
        if (settings == null)
        {
            var toolbar = _fixture.FindWindow("Toolbar_Window");
            var settingsBtn = _fixture.FindElement(toolbar!, "Toolbar_Button_Settings");
            await _fixture.SimulateDwellOn(settingsBtn!);
            await Task.Delay(500);
            settings = _fixture.FindWindow("Settings_Window");
        }
        Assert.NotNull(settings);

        var contentPanel = _fixture.FindElement(settings!, "Settings_Panel_Content");
        Assert.NotNull(contentPanel);

        _fixture.MoveCursorAway();
    }
}
```

### Step 2: Run the tests

```bash
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "FullyQualifiedName~SettingsTests" -v normal
```

Expected: 3 tests pass.

### Step 3: Commit

```bash
git add tests/EyeCtrl.App.Tests/Tests/SettingsTests.cs
git commit -m "test: add settings window and tab navigation tests"
```

---

## Task 8: Write tests — Keyboard opens

**Files:**
- Create: `tests/EyeCtrl.App.Tests/Tests/KeyboardTests.cs`

### Step 1: Write the test class

Create `tests/EyeCtrl.App.Tests/Tests/KeyboardTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Tests;

using EyeCtrl.App.Tests.Fixtures;

/// <summary>
/// Tests keyboard overlay: opens via toolbar, shows keys.
/// </summary>
[Collection("App")]
[Trait("Category", "UI")]
public class KeyboardTests
{
    private readonly AppFixture _fixture;

    public KeyboardTests(AppFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task KeyboardButton_Dwell_OpensKeyboard()
    {
        // Arrange
        var toolbar = _fixture.FindWindow("Toolbar_Window");
        Assert.NotNull(toolbar);
        var kbBtn = _fixture.FindElement(toolbar!, "Toolbar_Button_Keyboard");
        Assert.NotNull(kbBtn);

        // Act — dwell on keyboard button
        await _fixture.SimulateDwellOn(kbBtn!);
        await Task.Delay(500);

        // Assert — keyboard window should appear
        var keyboard = _fixture.FindWindow("Keyboard_Window");
        Assert.NotNull(keyboard);
        Assert.True(keyboard!.Properties.IsOffscreen.Value == false,
            "Keyboard window should be visible");

        // Cleanup — toggle keyboard off
        toolbar = _fixture.FindWindow("Toolbar_Window");
        kbBtn = _fixture.FindElement(toolbar!, "Toolbar_Button_Keyboard");
        if (kbBtn != null)
            await _fixture.SimulateDwellOn(kbBtn);

        _fixture.MoveCursorAway();
        await Task.Delay(500);
    }

    [Fact]
    public async Task Keyboard_HasKeys()
    {
        // Open keyboard
        var toolbar = _fixture.FindWindow("Toolbar_Window");
        Assert.NotNull(toolbar);
        var kbBtn = _fixture.FindElement(toolbar!, "Toolbar_Button_Keyboard");
        Assert.NotNull(kbBtn);
        await _fixture.SimulateDwellOn(kbBtn!);
        await Task.Delay(500);

        // Assert — should have key elements
        var keyboard = _fixture.FindWindow("Keyboard_Window");
        Assert.NotNull(keyboard);

        var keys = _fixture.FindElements(keyboard!, "Keyboard_Key_");
        Assert.True(keys.Length > 0, "Keyboard should have at least one key");

        // Cleanup — toggle keyboard off
        toolbar = _fixture.FindWindow("Toolbar_Window");
        kbBtn = _fixture.FindElement(toolbar!, "Toolbar_Button_Keyboard");
        if (kbBtn != null)
            await _fixture.SimulateDwellOn(kbBtn);

        _fixture.MoveCursorAway();
        await Task.Delay(500);
    }
}
```

### Step 2: Run the tests

```bash
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "FullyQualifiedName~KeyboardTests" -v normal
```

Expected: 2 tests pass.

### Step 3: Commit

```bash
git add tests/EyeCtrl.App.Tests/Tests/KeyboardTests.cs
git commit -m "test: add keyboard open and keys visibility tests"
```

---

## Task 9: Run all tests (unit + UI), build, verify

This task ensures everything works together — existing unit tests still pass
and the new UI tests all pass.

### Step 1: Run all existing unit tests

```bash
dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj -v normal
```

Expected: All existing tests pass (currently 46 tests).

### Step 2: Run all UI tests

```bash
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "Category=UI" -v normal
```

Expected: All UI tests pass (12 tests).

### Step 3: Build the entire solution

```bash
cd /c/Users/andre/source/repos/eyectrl && dotnet build EyeCtrl.slnx
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

### Step 4: Commit (if any fixes were needed)

If any test adjustments or fixes were made, commit them:

```bash
git add -A
git commit -m "fix: adjustments to make all tests pass"
```

---

## Summary

| Task | What | Tests |
|------|------|-------|
| 1 | Create test project + FlaUI dep | — |
| 2 | `--test-mode` CLI flag | Manual smoke test |
| 3 | AutomationIds on all overlays | — |
| 4 | AppFixture + collection | Build verification |
| 5 | App launch + overlay tests | 5 tests |
| 6 | Pause toggle tests | 2 tests |
| 7 | Settings window tests | 3 tests |
| 8 | Keyboard tests | 2 tests |
| 9 | Final verification | All tests green |

**Total new UI tests: 12**

After this plan is complete, the project has a solid UI testing foundation. Phase 2
(design doc section) can add gaze flow tests, zoom mode tests, keyboard typing,
and visual regression testing.
