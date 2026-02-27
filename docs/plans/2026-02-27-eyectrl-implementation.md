# EyeCtrl Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Build a WPF application that enables full PC control via a Tobii Dynavox PCEye 5 eye tracker, simulating keyboard and mouse input as a daily-driver accessibility tool.

**Architecture:** Three-layer C# architecture: UI Layer (WPF overlays), Core Logic (framework-agnostic), Platform Layer (Tobii SDK + Win32 SendInput). Core logic is fully unit-testable. An `IGazeProvider` interface decouples gaze data from the tracker hardware.

**Tech Stack:** .NET 8, WPF, xUnit, Tobii Interaction Library, Win32 `user32.dll` SendInput

**Design doc:** `docs/plans/2026-02-27-eyectrl-design.md`

**SDK Note — Tobii Integration (verified from local system):**
The following Tobii assemblies ship with TD Control (Computer Control) on this machine:

| Assembly | Version | Target | Path |
|----------|---------|--------|------|
| `tobii_interaction_lib_cs.dll` | 1.1.0.8 | .NET Standard 2.0 | `C:\Program Files (x86)\Tobii Dynavox\Computer Control\` |
| `tobii_interaction_lib.dll` | native | x86 | same directory |
| `tobii_interaction_lib_c.dll` | native | x86 | same directory |

**`tobii_interaction_lib_cs.dll` is the primary integration target.** It is .NET Standard 2.0 (compatible with .NET 8), provides `GazePointDataEvent` with `(x, y, timestamp_us, validity)` at ~33Hz, and is the same library TD Control uses.

Usage:
```csharp
var intlib = InteractionLibFactory.CreateInteractionLib(FieldOfUse.Interactive);
intlib.GazePointDataEvent += (data) => {
    // data.x, data.y — normalized screen coordinates (0.0–1.0)
    // data.timestamp_us — microseconds
    // data.validity — Validity enum
};
// In a background thread loop:
intlib.WaitAndUpdate(33); // blocks until data available or timeout
```

We reference the DLL directly from the TD Control install path and copy the native DLLs to the output directory. `MouseGazeProvider` remains available as a fallback for development without the tracker.

---

## Task 1: Solution and Project Scaffolding

**Files:**
- Create: `EyeCtrl.sln`
- Create: `src/EyeCtrl.Core/EyeCtrl.Core.csproj`
- Create: `src/EyeCtrl.Platform/EyeCtrl.Platform.csproj`
- Create: `src/EyeCtrl.App/EyeCtrl.App.csproj`
- Create: `tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj`
- Create: `tests/EyeCtrl.Platform.Tests/EyeCtrl.Platform.Tests.csproj`
- Create: `.gitignore`

**Step 1: Create solution and projects**

```bash
cd /c/Users/andre/source/repos/eyectrl
dotnet new sln -n EyeCtrl
dotnet new classlib -n EyeCtrl.Core -o src/EyeCtrl.Core -f net8.0
dotnet new classlib -n EyeCtrl.Platform -o src/EyeCtrl.Platform -f net8.0
dotnet new wpf -n EyeCtrl.App -o src/EyeCtrl.App -f net8.0-windows
dotnet new xunit -n EyeCtrl.Core.Tests -o tests/EyeCtrl.Core.Tests -f net8.0
dotnet new xunit -n EyeCtrl.Platform.Tests -o tests/EyeCtrl.Platform.Tests -f net8.0-windows
```

**Step 2: Add projects to solution and set up references**

```bash
dotnet sln add src/EyeCtrl.Core/EyeCtrl.Core.csproj
dotnet sln add src/EyeCtrl.Platform/EyeCtrl.Platform.csproj
dotnet sln add src/EyeCtrl.App/EyeCtrl.App.csproj
dotnet sln add tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj
dotnet sln add tests/EyeCtrl.Platform.Tests/EyeCtrl.Platform.Tests.csproj

dotnet add src/EyeCtrl.Platform/EyeCtrl.Platform.csproj reference src/EyeCtrl.Core/EyeCtrl.Core.csproj
dotnet add src/EyeCtrl.App/EyeCtrl.App.csproj reference src/EyeCtrl.Core/EyeCtrl.Core.csproj
dotnet add src/EyeCtrl.App/EyeCtrl.App.csproj reference src/EyeCtrl.Platform/EyeCtrl.Platform.csproj
dotnet add tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj reference src/EyeCtrl.Core/EyeCtrl.Core.csproj
dotnet add tests/EyeCtrl.Platform.Tests/EyeCtrl.Platform.Tests.csproj reference src/EyeCtrl.Platform/EyeCtrl.Platform.csproj
```

**Step 3: Add .gitignore**

Use standard .NET gitignore (bin/, obj/, *.user, .vs/, etc.)

**Step 4: Verify build**

Run: `dotnet build EyeCtrl.sln`
Expected: Build succeeded, 0 errors.

**Step 5: Verify tests run**

Run: `dotnet test EyeCtrl.sln`
Expected: All placeholder tests pass.

**Step 6: Clean up template files**

Delete `Class1.cs` from Core and Platform projects. Delete placeholder test files. Remove template `MainWindow` content from App project.

**Step 7: Commit**

```bash
git add -A && git commit -m "feat: scaffold solution with Core, Platform, App, and test projects"
```

---

## Task 2: Core Data Types and Interfaces

**Files:**
- Create: `src/EyeCtrl.Core/Models/GazePoint.cs`
- Create: `src/EyeCtrl.Core/Models/ScreenPoint.cs`
- Create: `src/EyeCtrl.Core/Models/GazeFilterMode.cs`
- Create: `src/EyeCtrl.Core/Models/InteractionAction.cs`
- Create: `src/EyeCtrl.Core/Models/InteractionState.cs`
- Create: `src/EyeCtrl.Core/Interfaces/IGazeProvider.cs`
- Create: `src/EyeCtrl.Core/Interfaces/IInputSimulator.cs`
- Create: `src/EyeCtrl.Core/Interfaces/IGazeFilter.cs`

**Step 1: Create GazePoint record**

```csharp
// src/EyeCtrl.Core/Models/GazePoint.cs
namespace EyeCtrl.Core.Models;

public record GazePoint(double X, double Y, long TimestampMicroseconds, bool IsValid);
```

**Step 2: Create ScreenPoint record**

```csharp
// src/EyeCtrl.Core/Models/ScreenPoint.cs
namespace EyeCtrl.Core.Models;

public record ScreenPoint(double X, double Y);
```

**Step 3: Create enums**

```csharp
// src/EyeCtrl.Core/Models/GazeFilterMode.cs
namespace EyeCtrl.Core.Models;

public enum GazeFilterMode
{
    Direct,
    Smoothed,
    SmoothedWithSnapping
}
```

```csharp
// src/EyeCtrl.Core/Models/InteractionAction.cs
namespace EyeCtrl.Core.Models;

public enum InteractionAction
{
    LeftClick,
    RightClick,
    DoubleClick,
    DragStart,
    DragEnd,
    ScrollUp,
    ScrollDown,
    ScrollLeft,
    ScrollRight
}
```

```csharp
// src/EyeCtrl.Core/Models/InteractionState.cs
namespace EyeCtrl.Core.Models;

public enum InteractionState
{
    FreeLook,
    Fixation,
    ActivationButtonVisible,
    ActionMenuVisible,
    ZoomActive,
    ZoomFixation,
    ZoomActionMenuVisible,
    Paused
}
```

**Step 4: Create interfaces**

```csharp
// src/EyeCtrl.Core/Interfaces/IGazeProvider.cs
namespace EyeCtrl.Core.Interfaces;

using EyeCtrl.Core.Models;

public interface IGazeProvider : IDisposable
{
    event Action<GazePoint>? GazePointReceived;
    void Start();
    void Stop();
    bool IsConnected { get; }
}
```

```csharp
// src/EyeCtrl.Core/Interfaces/IInputSimulator.cs
namespace EyeCtrl.Core.Interfaces;

using EyeCtrl.Core.Models;

public interface IInputSimulator
{
    void MoveCursor(ScreenPoint point);
    void LeftClick(ScreenPoint point);
    void RightClick(ScreenPoint point);
    void DoubleClick(ScreenPoint point);
    void MouseDown(ScreenPoint point);
    void MouseUp(ScreenPoint point);
    void Scroll(ScreenPoint point, int deltaX, int deltaY);
    void SendKeyPress(ushort virtualKeyCode);
    void SendKeyDown(ushort virtualKeyCode);
    void SendKeyUp(ushort virtualKeyCode);
    void SendText(string text);
}
```

```csharp
// src/EyeCtrl.Core/Interfaces/IGazeFilter.cs
namespace EyeCtrl.Core.Interfaces;

using EyeCtrl.Core.Models;

public interface IGazeFilter
{
    ScreenPoint Filter(GazePoint rawGaze);
    void Reset();
}
```

**Step 5: Verify build**

Run: `dotnet build EyeCtrl.sln`
Expected: Build succeeded.

**Step 6: Commit**

```bash
git add -A && git commit -m "feat: add core data types, enums, and interfaces"
```

---

## Task 3: Settings Model

**Files:**
- Create: `src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs`
- Create: `src/EyeCtrl.Core/Settings/CursorSettings.cs`
- Create: `src/EyeCtrl.Core/Settings/DwellSettings.cs`
- Create: `src/EyeCtrl.Core/Settings/ZoomSettings.cs`
- Create: `src/EyeCtrl.Core/Settings/ActionMenuSettings.cs`
- Create: `src/EyeCtrl.Core/Settings/KeyboardSettings.cs`
- Create: `src/EyeCtrl.Core/Settings/ToolbarSettings.cs`
- Create: `src/EyeCtrl.Core/Settings/ThemeSettings.cs`
- Create: `src/EyeCtrl.Core/Settings/GeneralSettings.cs`
- Create: `src/EyeCtrl.Core/Settings/SettingsService.cs`
- Test: `tests/EyeCtrl.Core.Tests/Settings/SettingsServiceTests.cs`

**Step 1: Write failing test for settings save/load round-trip**

```csharp
// tests/EyeCtrl.Core.Tests/Settings/SettingsServiceTests.cs
namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

public class SettingsServiceTests
{
    [Fact]
    public void SaveAndLoad_RoundTrips_AllSettings()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"eyectrl_test_{Guid.NewGuid()}.json");
        try
        {
            var settings = new EyeCtrlSettings();
            settings.Dwell.ActivationButtonDwellMs = 600;
            settings.Cursor.SmoothingFactor = 0.7;

            var service = new SettingsService(tempPath);
            service.Save(settings);

            var loaded = service.Load();

            Assert.Equal(600, loaded.Dwell.ActivationButtonDwellMs);
            Assert.Equal(0.7, loaded.Cursor.SmoothingFactor);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Load_WhenFileDoesNotExist_ReturnsDefaults()
    {
        var service = new SettingsService("/nonexistent/path.json");
        var settings = service.Load();

        Assert.NotNull(settings);
        Assert.Equal(500, settings.Dwell.ActivationButtonDwellMs); // default
    }
}
```

**Step 2: Run tests, verify they fail**

Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: FAIL — classes don't exist yet.

**Step 3: Implement settings classes**

```csharp
// src/EyeCtrl.Core/Settings/CursorSettings.cs
namespace EyeCtrl.Core.Settings;

using EyeCtrl.Core.Models;

public class CursorSettings
{
    public GazeFilterMode FilterMode { get; set; } = GazeFilterMode.Smoothed;
    public double SmoothingFactor { get; set; } = 0.5;
    public int SnapRadiusPixels { get; set; } = 30;
    public string CircleColor { get; set; } = "#00FF00";
    public double CircleOpacity { get; set; } = 0.6;
    public double CircleThickness { get; set; } = 2.0;
    public bool CircleHighContrast { get; set; } = false;
}
```

```csharp
// src/EyeCtrl.Core/Settings/DwellSettings.cs
namespace EyeCtrl.Core.Settings;

public class DwellSettings
{
    public int FixationDetectionMs { get; set; } = 200;
    public int ActivationButtonDwellMs { get; set; } = 500;
    public int ActionMenuDwellMs { get; set; } = 400;
    public int ZoomActionMenuDwellMs { get; set; } = 400;
    public int ToolbarButtonDwellMs { get; set; } = 500;
    public int KeyboardKeyDwellMs { get; set; } = 300;
    public int KeyboardPredictionDwellMs { get; set; } = 350;
    public double FixationRadiusPixels { get; set; } = 50;
}
```

```csharp
// src/EyeCtrl.Core/Settings/ZoomSettings.cs
namespace EyeCtrl.Core.Settings;

public class ZoomSettings
{
    public bool Enabled { get; set; } = false;
    public int CircleRadiusPixels { get; set; } = 150;
    public double MagnificationLevel { get; set; } = 2.0;
}
```

```csharp
// src/EyeCtrl.Core/Settings/ActionMenuSettings.cs
namespace EyeCtrl.Core.Settings;

using EyeCtrl.Core.Models;

public class ActionMenuSettings
{
    public List<InteractionAction> VisibleActions { get; set; } = new()
    {
        InteractionAction.LeftClick,
        InteractionAction.RightClick,
        InteractionAction.DoubleClick,
        InteractionAction.DragStart,
        InteractionAction.ScrollUp,
        InteractionAction.ScrollDown
    };
}
```

```csharp
// src/EyeCtrl.Core/Settings/KeyboardSettings.cs
namespace EyeCtrl.Core.Settings;

public class KeyboardSettings
{
    public string ActiveLayout { get; set; } = "QWERTY";
    public double KeySizeMultiplier { get; set; } = 1.0;
    public bool PredictionEnabled { get; set; } = true;
    public string LanguagePack { get; set; } = "en";
    public string ScreenEdge { get; set; } = "Bottom";
}
```

```csharp
// src/EyeCtrl.Core/Settings/ToolbarSettings.cs
namespace EyeCtrl.Core.Settings;

public class ToolbarSettings
{
    public bool IsDocked { get; set; } = true;
    public string DockedEdge { get; set; } = "Top";
    public double Size { get; set; } = 48;
}
```

```csharp
// src/EyeCtrl.Core/Settings/ThemeSettings.cs
namespace EyeCtrl.Core.Settings;

public class ThemeSettings
{
    public string Mode { get; set; } = "System"; // Light, Dark, System
    public double OverlayOpacity { get; set; } = 0.9;
    public bool HighContrast { get; set; } = false;
}
```

```csharp
// src/EyeCtrl.Core/Settings/GeneralSettings.cs
namespace EyeCtrl.Core.Settings;

public class GeneralSettings
{
    public bool StartOnBoot { get; set; } = false;
    public bool StartPaused { get; set; } = false;
}
```

```csharp
// src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs
namespace EyeCtrl.Core.Settings;

public class EyeCtrlSettings
{
    public CursorSettings Cursor { get; set; } = new();
    public DwellSettings Dwell { get; set; } = new();
    public ZoomSettings Zoom { get; set; } = new();
    public ActionMenuSettings ActionMenu { get; set; } = new();
    public KeyboardSettings Keyboard { get; set; } = new();
    public ToolbarSettings Toolbar { get; set; } = new();
    public ThemeSettings Theme { get; set; } = new();
    public GeneralSettings General { get; set; } = new();
}
```

```csharp
// src/EyeCtrl.Core/Settings/SettingsService.cs
namespace EyeCtrl.Core.Settings;

using System.Text.Json;

public class SettingsService
{
    private readonly string _filePath;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public SettingsService(string filePath)
    {
        _filePath = filePath;
    }

    public EyeCtrlSettings Load()
    {
        if (!File.Exists(_filePath))
            return new EyeCtrlSettings();

        var json = File.ReadAllText(_filePath);
        return JsonSerializer.Deserialize<EyeCtrlSettings>(json, JsonOptions)
               ?? new EyeCtrlSettings();
    }

    public void Save(EyeCtrlSettings settings)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(_filePath, json);
    }
}
```

**Step 4: Run tests, verify they pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: All tests pass.

**Step 5: Commit**

```bash
git add -A && git commit -m "feat: add settings model with JSON persistence"
```

---

## Task 4: Gaze Filters

**Files:**
- Create: `src/EyeCtrl.Core/Processing/DirectFilter.cs`
- Create: `src/EyeCtrl.Core/Processing/SmoothedFilter.cs`
- Create: `src/EyeCtrl.Core/Processing/GazeFilterFactory.cs`
- Test: `tests/EyeCtrl.Core.Tests/Processing/DirectFilterTests.cs`
- Test: `tests/EyeCtrl.Core.Tests/Processing/SmoothedFilterTests.cs`

**Step 1: Write failing tests**

```csharp
// tests/EyeCtrl.Core.Tests/Processing/DirectFilterTests.cs
namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

public class DirectFilterTests
{
    [Fact]
    public void Filter_ReturnsGazePointUnchanged()
    {
        var filter = new DirectFilter();
        var gaze = new GazePoint(100.0, 200.0, 0, true);

        var result = filter.Filter(gaze);

        Assert.Equal(100.0, result.X);
        Assert.Equal(200.0, result.Y);
    }

    [Fact]
    public void Filter_InvalidGaze_ReturnsLastValidPoint()
    {
        var filter = new DirectFilter();
        filter.Filter(new GazePoint(100.0, 200.0, 0, true));
        var result = filter.Filter(new GazePoint(0, 0, 1, false));

        Assert.Equal(100.0, result.X);
        Assert.Equal(200.0, result.Y);
    }
}
```

```csharp
// tests/EyeCtrl.Core.Tests/Processing/SmoothedFilterTests.cs
namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

public class SmoothedFilterTests
{
    [Fact]
    public void Filter_FirstPoint_ReturnedDirectly()
    {
        var filter = new SmoothedFilter(smoothingFactor: 0.5);
        var result = filter.Filter(new GazePoint(100.0, 200.0, 0, true));

        Assert.Equal(100.0, result.X);
        Assert.Equal(200.0, result.Y);
    }

    [Fact]
    public void Filter_SecondPoint_IsSmoothed()
    {
        var filter = new SmoothedFilter(smoothingFactor: 0.5);
        filter.Filter(new GazePoint(100.0, 200.0, 0, true));
        var result = filter.Filter(new GazePoint(200.0, 400.0, 1, true));

        // EMA: new = factor * raw + (1-factor) * prev
        // X: 0.5 * 200 + 0.5 * 100 = 150
        // Y: 0.5 * 400 + 0.5 * 200 = 300
        Assert.Equal(150.0, result.X);
        Assert.Equal(300.0, result.Y);
    }

    [Fact]
    public void Reset_ClearsHistory()
    {
        var filter = new SmoothedFilter(smoothingFactor: 0.5);
        filter.Filter(new GazePoint(100.0, 200.0, 0, true));
        filter.Reset();
        var result = filter.Filter(new GazePoint(500.0, 600.0, 1, true));

        Assert.Equal(500.0, result.X);
        Assert.Equal(600.0, result.Y);
    }
}
```

**Step 2: Run tests to verify failure**

Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: FAIL — filter classes don't exist.

**Step 3: Implement filters**

```csharp
// src/EyeCtrl.Core/Processing/DirectFilter.cs
namespace EyeCtrl.Core.Processing;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

public class DirectFilter : IGazeFilter
{
    private ScreenPoint _lastValid = new(0, 0);

    public ScreenPoint Filter(GazePoint rawGaze)
    {
        if (rawGaze.IsValid)
            _lastValid = new ScreenPoint(rawGaze.X, rawGaze.Y);
        return _lastValid;
    }

    public void Reset() => _lastValid = new(0, 0);
}
```

```csharp
// src/EyeCtrl.Core/Processing/SmoothedFilter.cs
namespace EyeCtrl.Core.Processing;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

public class SmoothedFilter : IGazeFilter
{
    private readonly double _factor;
    private ScreenPoint? _previous;

    public SmoothedFilter(double smoothingFactor)
    {
        _factor = Math.Clamp(smoothingFactor, 0.0, 1.0);
    }

    public ScreenPoint Filter(GazePoint rawGaze)
    {
        if (!rawGaze.IsValid && _previous != null)
            return _previous;

        var raw = new ScreenPoint(rawGaze.X, rawGaze.Y);

        if (_previous == null)
        {
            _previous = raw;
            return raw;
        }

        var smoothed = new ScreenPoint(
            _factor * raw.X + (1 - _factor) * _previous.X,
            _factor * raw.Y + (1 - _factor) * _previous.Y
        );

        _previous = smoothed;
        return smoothed;
    }

    public void Reset() => _previous = null;
}
```

```csharp
// src/EyeCtrl.Core/Processing/GazeFilterFactory.cs
namespace EyeCtrl.Core.Processing;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

public static class GazeFilterFactory
{
    public static IGazeFilter Create(CursorSettings settings)
    {
        return settings.FilterMode switch
        {
            GazeFilterMode.Direct => new DirectFilter(),
            GazeFilterMode.Smoothed => new SmoothedFilter(settings.SmoothingFactor),
            GazeFilterMode.SmoothedWithSnapping => new SmoothedFilter(settings.SmoothingFactor),
            // Snapping is handled at a higher level via UI Automation
            _ => new DirectFilter()
        };
    }
}
```

**Step 4: Run tests to verify pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: All tests pass.

**Step 5: Commit**

```bash
git add -A && git commit -m "feat: add gaze filters (direct and smoothed) with TDD"
```

---

## Task 5: Fixation Detector

**Files:**
- Create: `src/EyeCtrl.Core/Processing/FixationDetector.cs`
- Test: `tests/EyeCtrl.Core.Tests/Processing/FixationDetectorTests.cs`

**Step 1: Write failing tests**

```csharp
// tests/EyeCtrl.Core.Tests/Processing/FixationDetectorTests.cs
namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Models;

public class FixationDetectorTests
{
    [Fact]
    public void Update_GazeStableWithinRadius_RaisesFixationStarted()
    {
        var detector = new FixationDetector(
            fixationRadiusPixels: 50,
            fixationTimeMs: 200);
        bool fixationStarted = false;
        detector.FixationStarted += (point) => fixationStarted = true;

        // Simulate stable gaze for 250ms at ~33Hz (every 30ms)
        long time = 0;
        for (int i = 0; i < 9; i++) // 9 * 30ms = 270ms > 200ms threshold
        {
            detector.Update(new ScreenPoint(100 + i % 3, 100 + i % 3), time);
            time += 30_000; // microseconds
        }

        Assert.True(fixationStarted);
    }

    [Fact]
    public void Update_GazeMovesOutsideRadius_RaisesFixationEnded()
    {
        var detector = new FixationDetector(
            fixationRadiusPixels: 50,
            fixationTimeMs: 200);
        bool fixationEnded = false;
        detector.FixationEnded += () => fixationEnded = true;

        // First establish fixation
        long time = 0;
        for (int i = 0; i < 9; i++)
        {
            detector.Update(new ScreenPoint(100, 100), time);
            time += 30_000;
        }

        // Now move far away
        detector.Update(new ScreenPoint(500, 500), time);

        Assert.True(fixationEnded);
    }

    [Fact]
    public void Update_GazeMovesBeforeThreshold_NoFixation()
    {
        var detector = new FixationDetector(
            fixationRadiusPixels: 50,
            fixationTimeMs: 200);
        bool fixationStarted = false;
        detector.FixationStarted += (point) => fixationStarted = true;

        // Only 3 samples = 90ms, below 200ms threshold
        detector.Update(new ScreenPoint(100, 100), 0);
        detector.Update(new ScreenPoint(102, 102), 30_000);
        detector.Update(new ScreenPoint(101, 101), 60_000);
        // Move away
        detector.Update(new ScreenPoint(500, 500), 90_000);

        Assert.False(fixationStarted);
    }
}
```

**Step 2: Run tests to verify failure**

Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: FAIL.

**Step 3: Implement FixationDetector**

```csharp
// src/EyeCtrl.Core/Processing/FixationDetector.cs
namespace EyeCtrl.Core.Processing;

using EyeCtrl.Core.Models;

public class FixationDetector
{
    private readonly double _radiusPixels;
    private readonly long _fixationTimeMicroseconds;

    private ScreenPoint? _fixationCenter;
    private long _fixationStartTime;
    private bool _isFixating;

    public event Action<ScreenPoint>? FixationStarted;
    public event Action? FixationEnded;
    public bool IsFixating => _isFixating;
    public ScreenPoint? FixationPoint => _isFixating ? _fixationCenter : null;

    public FixationDetector(double fixationRadiusPixels, int fixationTimeMs)
    {
        _radiusPixels = fixationRadiusPixels;
        _fixationTimeMicroseconds = fixationTimeMs * 1000L;
    }

    public void Update(ScreenPoint currentPoint, long timestampMicroseconds)
    {
        if (_fixationCenter == null)
        {
            _fixationCenter = currentPoint;
            _fixationStartTime = timestampMicroseconds;
            return;
        }

        double distance = Math.Sqrt(
            Math.Pow(currentPoint.X - _fixationCenter.X, 2) +
            Math.Pow(currentPoint.Y - _fixationCenter.Y, 2));

        if (distance <= _radiusPixels)
        {
            if (!_isFixating &&
                (timestampMicroseconds - _fixationStartTime) >= _fixationTimeMicroseconds)
            {
                _isFixating = true;
                FixationStarted?.Invoke(_fixationCenter);
            }
        }
        else
        {
            if (_isFixating)
            {
                _isFixating = false;
                FixationEnded?.Invoke();
            }
            _fixationCenter = currentPoint;
            _fixationStartTime = timestampMicroseconds;
        }
    }

    public void Reset()
    {
        _fixationCenter = null;
        _isFixating = false;
    }
}
```

**Step 4: Run tests to verify pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: All pass.

**Step 5: Commit**

```bash
git add -A && git commit -m "feat: add fixation detector with radius and time threshold"
```

---

## Task 6: Dwell Detector

**Files:**
- Create: `src/EyeCtrl.Core/Processing/DwellDetector.cs`
- Create: `src/EyeCtrl.Core/Processing/DwellTarget.cs`
- Test: `tests/EyeCtrl.Core.Tests/Processing/DwellDetectorTests.cs`

**Step 1: Write failing tests**

```csharp
// tests/EyeCtrl.Core.Tests/Processing/DwellDetectorTests.cs
namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Models;

public class DwellDetectorTests
{
    [Fact]
    public void Update_DwellOnTarget_RaisesActivated()
    {
        var detector = new DwellDetector();
        var target = new DwellTarget("test", new ScreenPoint(100, 100), radius: 30, dwellTimeMs: 200);
        detector.AddTarget(target);

        bool activated = false;
        string? activatedId = null;
        detector.TargetActivated += (id) => { activated = true; activatedId = id; };

        long time = 0;
        for (int i = 0; i < 9; i++)
        {
            detector.Update(new ScreenPoint(105, 105), time);
            time += 30_000;
        }

        Assert.True(activated);
        Assert.Equal("test", activatedId);
    }

    [Fact]
    public void Update_GazeLeavesTarget_ResetsProgress()
    {
        var detector = new DwellDetector();
        var target = new DwellTarget("test", new ScreenPoint(100, 100), radius: 30, dwellTimeMs: 200);
        detector.AddTarget(target);

        bool activated = false;
        detector.TargetActivated += (id) => activated = true;

        // Gaze on target for 90ms
        detector.Update(new ScreenPoint(100, 100), 0);
        detector.Update(new ScreenPoint(100, 100), 30_000);
        detector.Update(new ScreenPoint(100, 100), 60_000);
        // Gaze leaves
        detector.Update(new ScreenPoint(500, 500), 90_000);
        // Back on target for 90ms — not enough from fresh start
        detector.Update(new ScreenPoint(100, 100), 120_000);
        detector.Update(new ScreenPoint(100, 100), 150_000);
        detector.Update(new ScreenPoint(100, 100), 180_000);

        Assert.False(activated);
    }

    [Fact]
    public void GetProgress_ReturnsDwellFraction()
    {
        var detector = new DwellDetector();
        var target = new DwellTarget("test", new ScreenPoint(100, 100), radius: 30, dwellTimeMs: 300);
        detector.AddTarget(target);

        detector.Update(new ScreenPoint(100, 100), 0);
        detector.Update(new ScreenPoint(100, 100), 150_000); // 150ms of 300ms

        double progress = detector.GetProgress("test");
        Assert.InRange(progress, 0.4, 0.6); // ~50%
    }
}
```

**Step 2: Run tests to verify failure**

Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: FAIL.

**Step 3: Implement DwellTarget and DwellDetector**

```csharp
// src/EyeCtrl.Core/Processing/DwellTarget.cs
namespace EyeCtrl.Core.Processing;

using EyeCtrl.Core.Models;

public class DwellTarget
{
    public string Id { get; }
    public ScreenPoint Center { get; set; }
    public double Radius { get; }
    public long DwellTimeMicroseconds { get; }

    public long? EnteredTimestamp { get; set; }
    public bool IsGazeInside { get; set; }

    public DwellTarget(string id, ScreenPoint center, double radius, int dwellTimeMs)
    {
        Id = id;
        Center = center;
        Radius = radius;
        DwellTimeMicroseconds = dwellTimeMs * 1000L;
    }

    public bool HitTest(ScreenPoint point)
    {
        double dx = point.X - Center.X;
        double dy = point.Y - Center.Y;
        return (dx * dx + dy * dy) <= Radius * Radius;
    }
}
```

```csharp
// src/EyeCtrl.Core/Processing/DwellDetector.cs
namespace EyeCtrl.Core.Processing;

using EyeCtrl.Core.Models;

public class DwellDetector
{
    private readonly Dictionary<string, DwellTarget> _targets = new();

    public event Action<string>? TargetActivated;
    public event Action<string, double>? TargetProgress;

    public void AddTarget(DwellTarget target) => _targets[target.Id] = target;
    public void RemoveTarget(string id) => _targets.Remove(id);
    public void ClearTargets() => _targets.Clear();

    public void Update(ScreenPoint gazePoint, long timestampMicroseconds)
    {
        foreach (var target in _targets.Values)
        {
            bool inside = target.HitTest(gazePoint);

            if (inside)
            {
                if (!target.IsGazeInside)
                {
                    target.IsGazeInside = true;
                    target.EnteredTimestamp = timestampMicroseconds;
                }
                else if (target.EnteredTimestamp.HasValue)
                {
                    long elapsed = timestampMicroseconds - target.EnteredTimestamp.Value;
                    double progress = Math.Min(1.0, (double)elapsed / target.DwellTimeMicroseconds);
                    TargetProgress?.Invoke(target.Id, progress);

                    if (elapsed >= target.DwellTimeMicroseconds)
                    {
                        TargetActivated?.Invoke(target.Id);
                        target.EnteredTimestamp = null;
                        target.IsGazeInside = false;
                    }
                }
            }
            else
            {
                target.IsGazeInside = false;
                target.EnteredTimestamp = null;
            }
        }
    }

    public double GetProgress(string targetId)
    {
        if (!_targets.TryGetValue(targetId, out var target))
            return 0;
        if (!target.IsGazeInside || !target.EnteredTimestamp.HasValue)
            return 0;
        // Use latest known time — caller should pass it, but approximate from entered
        return 0; // This needs timestamp context — see note below
    }
}
```

Note: `GetProgress` needs the current timestamp. Refactor to store `_lastTimestamp` in `Update`:

```csharp
// Add field to DwellDetector:
private long _lastTimestamp;

// Update _lastTimestamp in Update method:
_lastTimestamp = timestampMicroseconds;

// Fix GetProgress:
public double GetProgress(string targetId)
{
    if (!_targets.TryGetValue(targetId, out var target))
        return 0;
    if (!target.IsGazeInside || !target.EnteredTimestamp.HasValue)
        return 0;
    long elapsed = _lastTimestamp - target.EnteredTimestamp.Value;
    return Math.Min(1.0, (double)elapsed / target.DwellTimeMicroseconds);
}
```

**Step 4: Run tests to verify pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: All pass.

**Step 5: Commit**

```bash
git add -A && git commit -m "feat: add dwell detector with hit-testing and progress tracking"
```

---

## Task 7: Interaction State Machine (ModeManager)

**Files:**
- Create: `src/EyeCtrl.Core/Processing/InteractionManager.cs`
- Test: `tests/EyeCtrl.Core.Tests/Processing/InteractionManagerTests.cs`

**Step 1: Write failing tests**

```csharp
// tests/EyeCtrl.Core.Tests/Processing/InteractionManagerTests.cs
namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Models;

public class InteractionManagerTests
{
    [Fact]
    public void InitialState_IsFreeLook()
    {
        var manager = new InteractionManager();
        Assert.Equal(InteractionState.FreeLook, manager.CurrentState);
    }

    [Fact]
    public void OnFixationStarted_TransitionsToFixation()
    {
        var manager = new InteractionManager();
        manager.OnFixationStarted(new ScreenPoint(100, 100));
        Assert.Equal(InteractionState.Fixation, manager.CurrentState);
    }

    [Fact]
    public void OnFixationEnded_DuringFixation_ReturnsToFreeLook()
    {
        var manager = new InteractionManager();
        manager.OnFixationStarted(new ScreenPoint(100, 100));
        manager.OnFixationEnded();
        Assert.Equal(InteractionState.FreeLook, manager.CurrentState);
    }

    [Fact]
    public void OnActivationButtonDwelled_ShowsActionMenu()
    {
        var manager = new InteractionManager();
        manager.OnFixationStarted(new ScreenPoint(100, 100));
        manager.OnActivationButtonDwelled();
        Assert.Equal(InteractionState.ActionMenuVisible, manager.CurrentState);
    }

    [Fact]
    public void OnActivationButtonDwelled_WithZoom_TransitionsToZoom()
    {
        var manager = new InteractionManager { ZoomEnabled = true };
        manager.OnFixationStarted(new ScreenPoint(100, 100));
        manager.OnActivationButtonDwelled();
        Assert.Equal(InteractionState.ZoomActive, manager.CurrentState);
    }

    [Fact]
    public void OnActionSelected_FromMenu_FiresActionAndReturnsToFreeLook()
    {
        var manager = new InteractionManager();
        InteractionAction? firedAction = null;
        ScreenPoint? firedPoint = null;
        manager.ActionRequested += (action, point) => { firedAction = action; firedPoint = point; };

        manager.OnFixationStarted(new ScreenPoint(100, 100));
        manager.OnActivationButtonDwelled();
        manager.OnActionSelected(InteractionAction.LeftClick);

        Assert.Equal(InteractionAction.LeftClick, firedAction);
        Assert.Equal(new ScreenPoint(100, 100), firedPoint);
        Assert.Equal(InteractionState.FreeLook, manager.CurrentState);
    }

    [Fact]
    public void Pause_TransitionsToPaused()
    {
        var manager = new InteractionManager();
        manager.TogglePause();
        Assert.Equal(InteractionState.Paused, manager.CurrentState);
    }

    [Fact]
    public void Unpause_ReturnsToFreeLook()
    {
        var manager = new InteractionManager();
        manager.TogglePause();
        manager.TogglePause();
        Assert.Equal(InteractionState.FreeLook, manager.CurrentState);
    }

    [Fact]
    public void ZoomFlow_FixateActivateFixateSelectAction()
    {
        var manager = new InteractionManager { ZoomEnabled = true };
        InteractionAction? firedAction = null;
        ScreenPoint? firedPoint = null;
        manager.ActionRequested += (action, point) => { firedAction = action; firedPoint = point; };

        // Phase 1: fixate and activate → enters zoom
        manager.OnFixationStarted(new ScreenPoint(100, 100));
        manager.OnActivationButtonDwelled();
        Assert.Equal(InteractionState.ZoomActive, manager.CurrentState);

        // Phase 2: fixate within zoom → shows action menu
        manager.OnZoomFixation(new ScreenPoint(110, 110));
        Assert.Equal(InteractionState.ZoomActionMenuVisible, manager.CurrentState);

        // Select action
        manager.OnActionSelected(InteractionAction.RightClick);
        Assert.Equal(InteractionAction.RightClick, firedAction);
        Assert.Equal(new ScreenPoint(110, 110), firedPoint);
        Assert.Equal(InteractionState.FreeLook, manager.CurrentState);
    }

    [Fact]
    public void ZoomFlow_BackButton_ReturnsToFreeLook()
    {
        var manager = new InteractionManager { ZoomEnabled = true };

        manager.OnFixationStarted(new ScreenPoint(100, 100));
        manager.OnActivationButtonDwelled();
        manager.OnZoomFixation(new ScreenPoint(110, 110));
        manager.OnZoomBack();

        Assert.Equal(InteractionState.FreeLook, manager.CurrentState);
    }
}
```

**Step 2: Run tests to verify failure**

Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: FAIL.

**Step 3: Implement InteractionManager**

```csharp
// src/EyeCtrl.Core/Processing/InteractionManager.cs
namespace EyeCtrl.Core.Processing;

using EyeCtrl.Core.Models;

public class InteractionManager
{
    public InteractionState CurrentState { get; private set; } = InteractionState.FreeLook;
    public bool ZoomEnabled { get; set; }
    public ScreenPoint? FixationPoint { get; private set; }
    public ScreenPoint? ZoomFixationPoint { get; private set; }

    public event Action<InteractionState>? StateChanged;
    public event Action<InteractionAction, ScreenPoint>? ActionRequested;
    public event Action<ScreenPoint>? ShowActivationButton;
    public event Action? HideActivationButton;
    public event Action<ScreenPoint>? ShowActionMenu;
    public event Action? HideActionMenu;
    public event Action<ScreenPoint>? StartZoom;
    public event Action? EndZoom;

    private void SetState(InteractionState newState)
    {
        CurrentState = newState;
        StateChanged?.Invoke(newState);
    }

    public void OnFixationStarted(ScreenPoint point)
    {
        if (CurrentState != InteractionState.FreeLook) return;
        FixationPoint = point;
        SetState(InteractionState.Fixation);
        ShowActivationButton?.Invoke(point);
    }

    public void OnFixationEnded()
    {
        if (CurrentState == InteractionState.Fixation)
        {
            HideActivationButton?.Invoke();
            SetState(InteractionState.FreeLook);
        }
    }

    public void OnActivationButtonDwelled()
    {
        if (CurrentState != InteractionState.Fixation) return;
        HideActivationButton?.Invoke();

        if (ZoomEnabled)
        {
            StartZoom?.Invoke(FixationPoint!);
            SetState(InteractionState.ZoomActive);
        }
        else
        {
            ShowActionMenu?.Invoke(FixationPoint!);
            SetState(InteractionState.ActionMenuVisible);
        }
    }

    public void OnZoomFixation(ScreenPoint precisePoint)
    {
        if (CurrentState != InteractionState.ZoomActive) return;
        ZoomFixationPoint = precisePoint;
        ShowActionMenu?.Invoke(precisePoint);
        SetState(InteractionState.ZoomActionMenuVisible);
    }

    public void OnActionSelected(InteractionAction action)
    {
        ScreenPoint? targetPoint = CurrentState switch
        {
            InteractionState.ActionMenuVisible => FixationPoint,
            InteractionState.ZoomActionMenuVisible => ZoomFixationPoint,
            _ => null
        };

        if (targetPoint == null) return;

        HideActionMenu?.Invoke();
        if (CurrentState == InteractionState.ZoomActionMenuVisible)
            EndZoom?.Invoke();

        ActionRequested?.Invoke(action, targetPoint);
        ResetToFreeLook();
    }

    public void OnZoomBack()
    {
        if (CurrentState is InteractionState.ZoomActive or InteractionState.ZoomActionMenuVisible)
        {
            HideActionMenu?.Invoke();
            EndZoom?.Invoke();
            ResetToFreeLook();
        }
    }

    public void OnActionMenuDismissed()
    {
        if (CurrentState == InteractionState.ActionMenuVisible)
        {
            HideActionMenu?.Invoke();
            ResetToFreeLook();
        }
    }

    public void TogglePause()
    {
        if (CurrentState == InteractionState.Paused)
        {
            SetState(InteractionState.FreeLook);
        }
        else
        {
            HideActivationButton?.Invoke();
            HideActionMenu?.Invoke();
            if (CurrentState is InteractionState.ZoomActive or InteractionState.ZoomActionMenuVisible)
                EndZoom?.Invoke();
            SetState(InteractionState.Paused);
        }
    }

    private void ResetToFreeLook()
    {
        FixationPoint = null;
        ZoomFixationPoint = null;
        SetState(InteractionState.FreeLook);
    }
}
```

**Step 4: Run tests to verify pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: All pass.

**Step 5: Commit**

```bash
git add -A && git commit -m "feat: add interaction state machine with zoom and pause support"
```

---

## Task 8: Input Simulator (Win32 SendInput)

**Files:**
- Create: `src/EyeCtrl.Platform/Input/Win32InputSimulator.cs`
- Create: `src/EyeCtrl.Platform/Input/NativeMethods.cs`

**Step 1: Implement native interop types**

```csharp
// src/EyeCtrl.Platform/Input/NativeMethods.cs
namespace EyeCtrl.Platform.Input;

using System.Runtime.InteropServices;

internal static partial class NativeMethods
{
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetrics(int nIndex);

    internal const int SM_CXSCREEN = 0;
    internal const int SM_CYSCREEN = 1;

    internal const uint INPUT_MOUSE = 0;
    internal const uint INPUT_KEYBOARD = 1;

    internal const uint MOUSEEVENTF_MOVE = 0x0001;
    internal const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    internal const uint MOUSEEVENTF_LEFTUP = 0x0004;
    internal const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    internal const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    internal const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    internal const uint MOUSEEVENTF_WHEEL = 0x0800;
    internal const uint MOUSEEVENTF_HWHEEL = 0x1000;

    internal const uint KEYEVENTF_KEYUP = 0x0002;
    internal const uint KEYEVENTF_UNICODE = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint Type;
        public INPUTUNION Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public int MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }
}
```

**Step 2: Implement Win32InputSimulator**

```csharp
// src/EyeCtrl.Platform/Input/Win32InputSimulator.cs
namespace EyeCtrl.Platform.Input;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using System.Runtime.InteropServices;

public class Win32InputSimulator : IInputSimulator
{
    private static int ScreenWidth => NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
    private static int ScreenHeight => NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);

    public void MoveCursor(ScreenPoint point)
    {
        var input = CreateMouseInput(point, NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE);
        SendSingleInput(input);
    }

    public void LeftClick(ScreenPoint point)
    {
        var down = CreateMouseInput(point,
            NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_LEFTDOWN);
        var up = CreateMouseInput(point,
            NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_LEFTUP);
        SendInputs(down, up);
    }

    public void RightClick(ScreenPoint point)
    {
        var down = CreateMouseInput(point,
            NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_RIGHTDOWN);
        var up = CreateMouseInput(point,
            NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_RIGHTUP);
        SendInputs(down, up);
    }

    public void DoubleClick(ScreenPoint point)
    {
        LeftClick(point);
        LeftClick(point);
    }

    public void MouseDown(ScreenPoint point)
    {
        var input = CreateMouseInput(point,
            NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_LEFTDOWN);
        SendSingleInput(input);
    }

    public void MouseUp(ScreenPoint point)
    {
        var input = CreateMouseInput(point,
            NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_LEFTUP);
        SendSingleInput(input);
    }

    public void Scroll(ScreenPoint point, int deltaX, int deltaY)
    {
        if (deltaY != 0)
        {
            var input = CreateMouseInput(point, NativeMethods.MOUSEEVENTF_WHEEL);
            input.Union.Mouse.MouseData = deltaY * 120; // WHEEL_DELTA = 120
            SendSingleInput(input);
        }
        if (deltaX != 0)
        {
            var input = CreateMouseInput(point, NativeMethods.MOUSEEVENTF_HWHEEL);
            input.Union.Mouse.MouseData = deltaX * 120;
            SendSingleInput(input);
        }
    }

    public void SendKeyPress(ushort virtualKeyCode)
    {
        SendKeyDown(virtualKeyCode);
        SendKeyUp(virtualKeyCode);
    }

    public void SendKeyDown(ushort virtualKeyCode)
    {
        var input = CreateKeyboardInput(virtualKeyCode, 0);
        SendSingleInput(input);
    }

    public void SendKeyUp(ushort virtualKeyCode)
    {
        var input = CreateKeyboardInput(virtualKeyCode, NativeMethods.KEYEVENTF_KEYUP);
        SendSingleInput(input);
    }

    public void SendText(string text)
    {
        foreach (char c in text)
        {
            var down = new NativeMethods.INPUT
            {
                Type = NativeMethods.INPUT_KEYBOARD,
                Union = new NativeMethods.INPUTUNION
                {
                    Keyboard = new NativeMethods.KEYBDINPUT
                    {
                        VirtualKey = 0,
                        ScanCode = c,
                        Flags = NativeMethods.KEYEVENTF_UNICODE
                    }
                }
            };
            var up = down;
            up.Union.Keyboard.Flags = NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP;
            SendInputs(down, up);
        }
    }

    private static NativeMethods.INPUT CreateMouseInput(ScreenPoint point, uint flags)
    {
        int normalizedX = (int)((point.X * 65535) / ScreenWidth);
        int normalizedY = (int)((point.Y * 65535) / ScreenHeight);

        return new NativeMethods.INPUT
        {
            Type = NativeMethods.INPUT_MOUSE,
            Union = new NativeMethods.INPUTUNION
            {
                Mouse = new NativeMethods.MOUSEINPUT
                {
                    Dx = normalizedX,
                    Dy = normalizedY,
                    Flags = flags
                }
            }
        };
    }

    private static NativeMethods.INPUT CreateKeyboardInput(ushort vk, uint flags)
    {
        return new NativeMethods.INPUT
        {
            Type = NativeMethods.INPUT_KEYBOARD,
            Union = new NativeMethods.INPUTUNION
            {
                Keyboard = new NativeMethods.KEYBDINPUT
                {
                    VirtualKey = vk,
                    Flags = flags
                }
            }
        };
    }

    private static void SendSingleInput(NativeMethods.INPUT input)
    {
        NativeMethods.SendInput(1, [input], Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static void SendInputs(params NativeMethods.INPUT[] inputs)
    {
        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }
}
```

**Step 3: Verify build**

Run: `dotnet build EyeCtrl.sln`
Expected: Build succeeded. (No unit tests for SendInput — it requires an active desktop session.)

**Step 4: Commit**

```bash
git add -A && git commit -m "feat: add Win32 SendInput wrapper for mouse and keyboard simulation"
```

---

## Task 9: Mouse Gaze Provider (Development Mock)

**Files:**
- Create: `src/EyeCtrl.Platform/Gaze/MouseGazeProvider.cs`

**Step 1: Implement MouseGazeProvider**

This reads the current mouse cursor position at a configurable interval and fires `GazePointReceived`, allowing full development and testing without a Tobii tracker.

```csharp
// src/EyeCtrl.Platform/Gaze/MouseGazeProvider.cs
namespace EyeCtrl.Platform.Gaze;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using System.Runtime.InteropServices;

public class MouseGazeProvider : IGazeProvider, IDisposable
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private Timer? _timer;
    private readonly int _intervalMs;

    public event Action<GazePoint>? GazePointReceived;
    public bool IsConnected { get; private set; }

    public MouseGazeProvider(int pollIntervalMs = 30)
    {
        _intervalMs = pollIntervalMs;
    }

    public void Start()
    {
        IsConnected = true;
        _timer = new Timer(OnTick, null, 0, _intervalMs);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        IsConnected = false;
    }

    private void OnTick(object? state)
    {
        if (GetCursorPos(out var point))
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
            GazePointReceived?.Invoke(new GazePoint(point.X, point.Y, timestamp, true));
        }
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
```

**Step 2: Verify build**

Run: `dotnet build EyeCtrl.sln`
Expected: Build succeeded.

**Step 3: Commit**

```bash
git add -A && git commit -m "feat: add mouse-based gaze provider for development"
```

---

## Task 10: Word Prediction Engine

**Files:**
- Create: `src/EyeCtrl.Core/Prediction/WordPredictor.cs`
- Create: `src/EyeCtrl.Core/Prediction/IDictionaryProvider.cs`
- Create: `src/EyeCtrl.Core/Prediction/FileDictionaryProvider.cs`
- Test: `tests/EyeCtrl.Core.Tests/Prediction/WordPredictorTests.cs`

**Step 1: Write failing tests**

```csharp
// tests/EyeCtrl.Core.Tests/Prediction/WordPredictorTests.cs
namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;

public class WordPredictorTests
{
    private static WordPredictor CreatePredictor(Dictionary<string, int> words)
    {
        return new WordPredictor(new InMemoryDictionaryProvider(words));
    }

    [Fact]
    public void GetSuggestions_MatchesPrefix()
    {
        var predictor = CreatePredictor(new()
        {
            { "hello", 100 }, { "help", 80 }, { "world", 50 }, { "heap", 30 }
        });

        var results = predictor.GetSuggestions("hel", maxCount: 3);

        Assert.Equal(2, results.Count);
        Assert.Contains("hello", results);
        Assert.Contains("help", results);
    }

    [Fact]
    public void GetSuggestions_OrderedByFrequency()
    {
        var predictor = CreatePredictor(new()
        {
            { "the", 1000 }, { "they", 500 }, { "there", 300 }, { "then", 200 }
        });

        var results = predictor.GetSuggestions("the", maxCount: 3);

        Assert.Equal("the", results[0]);
        Assert.Equal("they", results[1]);
        Assert.Equal("there", results[2]);
    }

    [Fact]
    public void LearnWord_IncreasesFrequency()
    {
        var predictor = CreatePredictor(new()
        {
            { "test", 10 }, { "team", 100 }
        });

        // "test" starts lower than "team"
        var before = predictor.GetSuggestions("te", maxCount: 2);
        Assert.Equal("team", before[0]);

        // Learn "test" many times
        for (int i = 0; i < 200; i++)
            predictor.LearnWord("test");

        var after = predictor.GetSuggestions("te", maxCount: 2);
        Assert.Equal("test", after[0]);
    }

    [Fact]
    public void GetSuggestions_EmptyPrefix_ReturnsTopWords()
    {
        var predictor = CreatePredictor(new()
        {
            { "the", 1000 }, { "a", 900 }, { "is", 800 }
        });

        var results = predictor.GetSuggestions("", maxCount: 3);
        Assert.Equal(3, results.Count);
    }
}

// Test helper
public class InMemoryDictionaryProvider : IDictionaryProvider
{
    private readonly Dictionary<string, int> _words;
    public InMemoryDictionaryProvider(Dictionary<string, int> words) => _words = words;
    public Dictionary<string, int> Load() => new(_words);
    public void Save(Dictionary<string, int> words) { }
}
```

**Step 2: Run tests to verify failure**

Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: FAIL.

**Step 3: Implement prediction engine**

```csharp
// src/EyeCtrl.Core/Prediction/IDictionaryProvider.cs
namespace EyeCtrl.Core.Prediction;

public interface IDictionaryProvider
{
    Dictionary<string, int> Load();
    void Save(Dictionary<string, int> words);
}
```

```csharp
// src/EyeCtrl.Core/Prediction/WordPredictor.cs
namespace EyeCtrl.Core.Prediction;

public class WordPredictor
{
    private readonly IDictionaryProvider _provider;
    private readonly Dictionary<string, int> _words;

    public WordPredictor(IDictionaryProvider provider)
    {
        _provider = provider;
        _words = provider.Load();
    }

    public List<string> GetSuggestions(string prefix, int maxCount = 5)
    {
        var lowerPrefix = prefix.ToLowerInvariant();

        return _words
            .Where(kv => kv.Key.StartsWith(lowerPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(kv => kv.Value)
            .Take(maxCount)
            .Select(kv => kv.Key)
            .ToList();
    }

    public void LearnWord(string word)
    {
        var lower = word.ToLowerInvariant();
        if (_words.ContainsKey(lower))
            _words[lower]++;
        else
            _words[lower] = 1;
    }

    public void SaveUserDictionary()
    {
        _provider.Save(_words);
    }
}
```

```csharp
// src/EyeCtrl.Core/Prediction/FileDictionaryProvider.cs
namespace EyeCtrl.Core.Prediction;

using System.Text.Json;

public class FileDictionaryProvider : IDictionaryProvider
{
    private readonly string _baseDictionaryPath;
    private readonly string _userDictionaryPath;

    public FileDictionaryProvider(string baseDictionaryPath, string userDictionaryPath)
    {
        _baseDictionaryPath = baseDictionaryPath;
        _userDictionaryPath = userDictionaryPath;
    }

    public Dictionary<string, int> Load()
    {
        var words = new Dictionary<string, int>();

        if (File.Exists(_baseDictionaryPath))
        {
            var json = File.ReadAllText(_baseDictionaryPath);
            var baseWords = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
            if (baseWords != null)
                foreach (var kv in baseWords)
                    words[kv.Key] = kv.Value;
        }

        if (File.Exists(_userDictionaryPath))
        {
            var json = File.ReadAllText(_userDictionaryPath);
            var userWords = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
            if (userWords != null)
                foreach (var kv in userWords)
                    words[kv.Key] = words.GetValueOrDefault(kv.Key) + kv.Value;
        }

        return words;
    }

    public void Save(Dictionary<string, int> words)
    {
        var directory = Path.GetDirectoryName(_userDictionaryPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(words, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_userDictionaryPath, json);
    }
}
```

**Step 4: Run tests to verify pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: All pass.

**Step 5: Commit**

```bash
git add -A && git commit -m "feat: add word prediction engine with frequency ranking and user learning"
```

---

## Task 11: Keyboard Layout Model

**Files:**
- Create: `src/EyeCtrl.Core/Keyboard/KeyDefinition.cs`
- Create: `src/EyeCtrl.Core/Keyboard/KeyboardLayout.cs`
- Create: `src/EyeCtrl.Core/Keyboard/KeyboardLayoutLoader.cs`
- Test: `tests/EyeCtrl.Core.Tests/Keyboard/KeyboardLayoutLoaderTests.cs`

**Step 1: Write failing test**

```csharp
// tests/EyeCtrl.Core.Tests/Keyboard/KeyboardLayoutLoaderTests.cs
namespace EyeCtrl.Core.Tests.Keyboard;

using EyeCtrl.Core.Keyboard;
using System.Text.Json;

public class KeyboardLayoutLoaderTests
{
    [Fact]
    public void Load_ValidJson_ReturnsLayout()
    {
        var json = """
        {
            "name": "QWERTY",
            "rows": [
                [
                    { "label": "Q", "output": "q", "shiftOutput": "Q", "width": 1.0 },
                    { "label": "W", "output": "w", "shiftOutput": "W", "width": 1.0 }
                ],
                [
                    { "label": "A", "output": "a", "shiftOutput": "A", "width": 1.0 }
                ]
            ]
        }
        """;

        var tempPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempPath, json);
            var layout = KeyboardLayoutLoader.Load(tempPath);

            Assert.Equal("QWERTY", layout.Name);
            Assert.Equal(2, layout.Rows.Count);
            Assert.Equal(2, layout.Rows[0].Count);
            Assert.Equal("Q", layout.Rows[0][0].Label);
            Assert.Equal("q", layout.Rows[0][0].Output);
            Assert.Equal("Q", layout.Rows[0][0].ShiftOutput);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}
```

**Step 2: Run test to verify failure**

Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: FAIL.

**Step 3: Implement keyboard layout model**

```csharp
// src/EyeCtrl.Core/Keyboard/KeyDefinition.cs
namespace EyeCtrl.Core.Keyboard;

public class KeyDefinition
{
    public string Label { get; set; } = "";
    public string Output { get; set; } = "";
    public string? ShiftOutput { get; set; }
    public double Width { get; set; } = 1.0;
    public string? SpecialAction { get; set; } // "shift", "backspace", "enter", "tab", "space", "symbols", "layout"
}
```

```csharp
// src/EyeCtrl.Core/Keyboard/KeyboardLayout.cs
namespace EyeCtrl.Core.Keyboard;

public class KeyboardLayout
{
    public string Name { get; set; } = "";
    public List<List<KeyDefinition>> Rows { get; set; } = new();
}
```

```csharp
// src/EyeCtrl.Core/Keyboard/KeyboardLayoutLoader.cs
namespace EyeCtrl.Core.Keyboard;

using System.Text.Json;

public static class KeyboardLayoutLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static KeyboardLayout Load(string filePath)
    {
        var json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<KeyboardLayout>(json, Options)
               ?? throw new InvalidOperationException($"Failed to load keyboard layout from {filePath}");
    }

    public static void Save(KeyboardLayout layout, string filePath)
    {
        var json = JsonSerializer.Serialize(layout, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        File.WriteAllText(filePath, json);
    }
}
```

**Step 4: Run tests to verify pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: All pass.

**Step 5: Commit**

```bash
git add -A && git commit -m "feat: add keyboard layout model with JSON serialization"
```

---

## Task 12: GazeEngine — Core Pipeline Orchestrator

**Files:**
- Create: `src/EyeCtrl.Core/GazeEngine.cs`
- Test: `tests/EyeCtrl.Core.Tests/GazeEngineTests.cs`

This is the central orchestrator that wires GazeProvider → Filter → FixationDetector → InteractionManager → InputSimulator.

**Step 1: Write failing tests**

```csharp
// tests/EyeCtrl.Core.Tests/GazeEngineTests.cs
namespace EyeCtrl.Core.Tests;

using EyeCtrl.Core;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;

public class GazeEngineTests
{
    private class FakeGazeProvider : IGazeProvider
    {
        public event Action<GazePoint>? GazePointReceived;
        public bool IsConnected { get; private set; }
        public void Start() => IsConnected = true;
        public void Stop() => IsConnected = false;
        public void Dispose() => Stop();
        public void SimulateGaze(GazePoint point) => GazePointReceived?.Invoke(point);
    }

    private class FakeInputSimulator : IInputSimulator
    {
        public List<(string Action, ScreenPoint Point)> Actions { get; } = new();
        public void MoveCursor(ScreenPoint p) => Actions.Add(("Move", p));
        public void LeftClick(ScreenPoint p) => Actions.Add(("LeftClick", p));
        public void RightClick(ScreenPoint p) => Actions.Add(("RightClick", p));
        public void DoubleClick(ScreenPoint p) => Actions.Add(("DoubleClick", p));
        public void MouseDown(ScreenPoint p) => Actions.Add(("MouseDown", p));
        public void MouseUp(ScreenPoint p) => Actions.Add(("MouseUp", p));
        public void Scroll(ScreenPoint p, int dx, int dy) => Actions.Add(("Scroll", p));
        public void SendKeyPress(ushort vk) { }
        public void SendKeyDown(ushort vk) { }
        public void SendKeyUp(ushort vk) { }
        public void SendText(string text) { }
    }

    [Fact]
    public void ProcessGaze_MovesCursor()
    {
        var provider = new FakeGazeProvider();
        var simulator = new FakeInputSimulator();
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;

        var engine = new GazeEngine(provider, simulator, settings);
        engine.Start();

        provider.SimulateGaze(new GazePoint(500, 300, 0, true));

        Assert.Contains(simulator.Actions, a => a.Action == "Move");
    }

    [Fact]
    public void ActionRequested_LeftClick_SimulatesClick()
    {
        var provider = new FakeGazeProvider();
        var simulator = new FakeInputSimulator();
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;

        var engine = new GazeEngine(provider, simulator, settings);
        engine.Start();

        // Directly trigger action through the interaction manager
        engine.InteractionManager.OnFixationStarted(new ScreenPoint(200, 200));
        engine.InteractionManager.OnActivationButtonDwelled();
        engine.InteractionManager.OnActionSelected(InteractionAction.LeftClick);

        Assert.Contains(simulator.Actions, a => a.Action == "LeftClick");
    }
}
```

**Step 2: Run tests to verify failure**

Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: FAIL.

**Step 3: Implement GazeEngine**

```csharp
// src/EyeCtrl.Core/GazeEngine.cs
namespace EyeCtrl.Core;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;

public class GazeEngine : IDisposable
{
    private readonly IGazeProvider _gazeProvider;
    private readonly IInputSimulator _inputSimulator;
    private readonly EyeCtrlSettings _settings;
    private readonly IGazeFilter _filter;
    private readonly FixationDetector _fixationDetector;

    public InteractionManager InteractionManager { get; }
    public event Action<ScreenPoint>? CursorMoved;

    public GazeEngine(
        IGazeProvider gazeProvider,
        IInputSimulator inputSimulator,
        EyeCtrlSettings settings)
    {
        _gazeProvider = gazeProvider;
        _inputSimulator = inputSimulator;
        _settings = settings;

        _filter = GazeFilterFactory.Create(settings.Cursor);
        _fixationDetector = new FixationDetector(
            settings.Dwell.FixationRadiusPixels,
            settings.Dwell.FixationDetectionMs);

        InteractionManager = new InteractionManager
        {
            ZoomEnabled = settings.Zoom.Enabled
        };

        _gazeProvider.GazePointReceived += OnGazePoint;
        _fixationDetector.FixationStarted += InteractionManager.OnFixationStarted;
        _fixationDetector.FixationEnded += InteractionManager.OnFixationEnded;
        InteractionManager.ActionRequested += OnActionRequested;
    }

    public void Start()
    {
        _gazeProvider.Start();
    }

    public void Stop()
    {
        _gazeProvider.Stop();
    }

    private void OnGazePoint(GazePoint gaze)
    {
        if (InteractionManager.CurrentState == InteractionState.Paused)
            return;

        var filtered = _filter.Filter(gaze);

        if (InteractionManager.CurrentState == InteractionState.FreeLook)
        {
            _inputSimulator.MoveCursor(filtered);
        }

        CursorMoved?.Invoke(filtered);
        _fixationDetector.Update(filtered, gaze.TimestampMicroseconds);
    }

    private void OnActionRequested(InteractionAction action, ScreenPoint point)
    {
        switch (action)
        {
            case InteractionAction.LeftClick:
                _inputSimulator.LeftClick(point);
                break;
            case InteractionAction.RightClick:
                _inputSimulator.RightClick(point);
                break;
            case InteractionAction.DoubleClick:
                _inputSimulator.DoubleClick(point);
                break;
            case InteractionAction.DragStart:
                _inputSimulator.MouseDown(point);
                break;
            case InteractionAction.DragEnd:
                _inputSimulator.MouseUp(point);
                break;
            case InteractionAction.ScrollUp:
                _inputSimulator.Scroll(point, 0, 3);
                break;
            case InteractionAction.ScrollDown:
                _inputSimulator.Scroll(point, 0, -3);
                break;
            case InteractionAction.ScrollLeft:
                _inputSimulator.Scroll(point, -3, 0);
                break;
            case InteractionAction.ScrollRight:
                _inputSimulator.Scroll(point, 3, 0);
                break;
        }
    }

    public void Dispose()
    {
        _gazeProvider.GazePointReceived -= OnGazePoint;
        _gazeProvider.Dispose();
        GC.SuppressFinalize(this);
    }
}
```

**Step 4: Run tests to verify pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: All pass.

**Step 5: Commit**

```bash
git add -A && git commit -m "feat: add GazeEngine orchestrator wiring pipeline together"
```

---

## Task 13: WPF Cursor Circle Overlay

**Files:**
- Create: `src/EyeCtrl.App/Overlays/CursorCircleWindow.xaml`
- Create: `src/EyeCtrl.App/Overlays/CursorCircleWindow.xaml.cs`

**Step 1: Implement CursorCircleWindow**

A borderless, transparent, always-on-top, click-through WPF window that draws a circle centered on the cursor.

XAML:
```xml
<Window x:Class="EyeCtrl.App.Overlays.CursorCircleWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        Topmost="True" ShowInTaskbar="False"
        Width="300" Height="300">
    <Canvas>
        <Ellipse x:Name="Circle"
                 Stroke="Lime" StrokeThickness="2"
                 Width="300" Height="300"
                 Canvas.Left="0" Canvas.Top="0"/>
    </Canvas>
</Window>
```

Code-behind:
- Override `OnSourceInitialized` to set `WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW` via `SetWindowLong` so clicks pass through
- Expose `UpdatePosition(ScreenPoint point)` method that moves the window centered on the point
- Expose `UpdateAppearance(CursorSettings settings)` that updates circle color, thickness, opacity

**Step 2: Verify build and visual**

Run: `dotnet build src/EyeCtrl.App`
Expected: Build succeeded.

**Step 3: Commit**

```bash
git add -A && git commit -m "feat: add cursor circle overlay window (click-through, always-on-top)"
```

---

## Task 14: WPF Activation Button Overlay

**Files:**
- Create: `src/EyeCtrl.App/Overlays/ActivationButtonWindow.xaml`
- Create: `src/EyeCtrl.App/Overlays/ActivationButtonWindow.xaml.cs`

**Step 1: Implement ActivationButtonWindow**

A small circular button that fades in near the cursor when fixation is detected. Shows dwell progress as a shrinking ring animation.

XAML: Small circular window with an `Ellipse` and a `ProgressRing`-style arc showing dwell progress.

Code-behind:
- `Show(ScreenPoint position)` — fade in at the given position
- `Hide()` — fade out
- `UpdateDwellProgress(double progress)` — update arc angle (0.0 to 1.0)
- Click-through except for the button area itself (so dwell detection works on it)

**Step 2: Verify build**

Run: `dotnet build src/EyeCtrl.App`
Expected: Build succeeded.

**Step 3: Commit**

```bash
git add -A && git commit -m "feat: add activation button overlay with dwell progress animation"
```

---

## Task 15: WPF Radial Action Menu

**Files:**
- Create: `src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml`
- Create: `src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs`

**Step 1: Implement ActionMenuWindow**

A radial menu window showing action buttons arranged in a circle around the fixation point. Each button shows dwell progress.

XAML: Canvas-based layout with `ItemsControl` bound to a list of actions. Each item is a circle with icon and label positioned radially.

Code-behind:
- `Show(ScreenPoint center, List<InteractionAction> actions, bool showBackButton)` — positions buttons in a ring
- `Hide()` — dismiss
- Emits event when dwell completes on a button: `ActionSelected(InteractionAction action)`
- Emits event for back button: `BackSelected`
- Each button has its own dwell progress indicator
- Receives gaze updates to track dwell on individual buttons

**Step 2: Verify build**

Run: `dotnet build src/EyeCtrl.App`
Expected: Build succeeded.

**Step 3: Commit**

```bash
git add -A && git commit -m "feat: add radial action menu overlay with dwellable buttons"
```

---

## Task 16: WPF Toolbar

**Files:**
- Create: `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml`
- Create: `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs`

**Step 1: Implement ToolbarWindow**

A thin panel with 4 dwellable buttons: Pause, Zoom, Keyboard, Settings. Supports docked (screen edge) and floating modes.

XAML: `StackPanel` with styled buttons. Each button has a dwell progress indicator.

Code-behind:
- Buttons are dwellable using the `DwellDetector` (toolbar dwell time from settings)
- Events: `PauseToggled`, `ZoomToggled`, `KeyboardToggled`, `SettingsRequested`
- `SetMode(bool isDocked, string edge)` — switches between docked and floating
- Pause button changes icon/text based on paused state
- Zoom button shows on/off state

**Step 2: Verify build**

Run: `dotnet build src/EyeCtrl.App`
Expected: Build succeeded.

**Step 3: Commit**

```bash
git add -A && git commit -m "feat: add toolbar overlay with dwellable utility buttons"
```

---

## Task 17: WPF Zoom Overlay

**Files:**
- Create: `src/EyeCtrl.App/Overlays/ZoomOverlayWindow.xaml`
- Create: `src/EyeCtrl.App/Overlays/ZoomOverlayWindow.xaml.cs`

**Step 1: Implement ZoomOverlayWindow**

Captures a screenshot of the circle region around the fixation point and displays it magnified.

Code-behind:
- `Show(ScreenPoint center, int radius, double magnification)` — captures screen region using `CopyFromScreen`, displays magnified in a window
- Maps gaze coordinates within the zoomed view back to real screen coordinates
- `GetRealScreenPoint(ScreenPoint gazeInZoom)` — transforms zoomed coordinates to original
- `Hide()` — dismiss

**Step 2: Verify build**

Run: `dotnet build src/EyeCtrl.App`
Expected: Build succeeded.

**Step 3: Commit**

```bash
git add -A && git commit -m "feat: add zoom overlay with screen region magnification"
```

---

## Task 18: WPF On-Screen Keyboard

**Files:**
- Create: `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml`
- Create: `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs`

**Step 1: Implement KeyboardWindow**

A large overlay window with dwellable keys arranged per the active `KeyboardLayout`. Includes prediction bar at the top.

XAML: `ItemsControl` with `ItemsPanel` as `WrapPanel` or grid. Each key is a styled `Border` with `TextBlock`. Prediction bar is a `StackPanel` of suggestion buttons above the keys.

Code-behind:
- `LoadLayout(KeyboardLayout layout)` — generates key UI from layout model
- Each key has dwell detection (keyboard key dwell time)
- Shift/Caps toggle switches displayed labels between `Output` and `ShiftOutput`
- Special keys: Backspace, Enter, Tab, Space, Symbols, Layout-switch
- `KeyActivated(string output)` event — fires when a key is dwelled
- Prediction bar: displays suggestions from `WordPredictor`, each suggestion is dwellable (prediction dwell time)
- `PredictionSelected(string word)` event

**Step 2: Verify build**

Run: `dotnet build src/EyeCtrl.App`
Expected: Build succeeded.

**Step 3: Commit**

```bash
git add -A && git commit -m "feat: add on-screen keyboard with prediction bar and layout support"
```

---

## Task 19: Built-In Keyboard Layouts

**Files:**
- Create: `src/EyeCtrl.App/Resources/Layouts/qwerty.json`
- Create: `src/EyeCtrl.App/Resources/Layouts/qwertz.json`
- Create: `src/EyeCtrl.App/Resources/Layouts/azerty.json`

**Step 1: Create QWERTY layout JSON**

Full layout with 4 rows: number row, top letter row (QWERTY), home row (ASDF), bottom row (ZXCV), plus special keys row (Shift, Space, Backspace, Enter, Symbols, Layout-switch).

**Step 2: Create QWERTZ layout JSON**

Same structure, German layout (Y/Z swapped, umlauts).

**Step 3: Create AZERTY layout JSON**

Same structure, French layout.

**Step 4: Add files as embedded resources or copy-to-output in .csproj**

**Step 5: Commit**

```bash
git add -A && git commit -m "feat: add built-in keyboard layouts (QWERTY, QWERTZ, AZERTY)"
```

---

## Task 20: English Word Frequency Dictionary

**Files:**
- Create: `src/EyeCtrl.App/Resources/Dictionaries/en.json`

**Step 1: Create English dictionary**

A JSON file with the top ~10,000 English words and their relative frequencies. Source from public domain word frequency lists.

Format: `{ "the": 69971, "of": 36411, "and": 28852, ... }`

**Step 2: Add as resource in .csproj**

**Step 3: Commit**

```bash
git add -A && git commit -m "feat: add English word frequency dictionary for prediction"
```

---

## Task 21: Settings Window

**Files:**
- Create: `src/EyeCtrl.App/Views/SettingsWindow.xaml`
- Create: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs`

**Step 1: Implement SettingsWindow**

A standard WPF window (not overlay) with tabbed sections matching the settings model: Cursor, Dwell Times, Zoom, Action Menu, Keyboard, Toolbar, Theme, General.

Each tab uses sliders, dropdowns, color pickers, and toggles bound to `EyeCtrlSettings` properties. Changes are applied on save and persisted via `SettingsService`.

Includes profile management: Save Profile, Load Profile, Import, Export.

This window is dwellable (opened from toolbar), so all its controls must be large enough for eye-tracking interaction.

**Step 2: Verify build**

Run: `dotnet build src/EyeCtrl.App`
Expected: Build succeeded.

**Step 3: Commit**

```bash
git add -A && git commit -m "feat: add settings window with all configuration tabs"
```

---

## Task 22: App Integration — Wire Everything Together

**Files:**
- Modify: `src/EyeCtrl.App/App.xaml.cs`
- Modify: `src/EyeCtrl.App/MainWindow.xaml` (remove or make invisible — app is overlay-only)

**Step 1: Wire up in App.xaml.cs OnStartup**

```csharp
// Pseudocode for App.xaml.cs
protected override void OnStartup(StartupEventArgs e)
{
    base.OnStartup(e);

    // Load settings
    var settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EyeCtrl", "settings.json");
    var settingsService = new SettingsService(settingsPath);
    var settings = settingsService.Load();

    // Create platform layer
    var gazeProvider = new MouseGazeProvider(); // Swap to TobiiGazeProvider later
    var inputSimulator = new Win32InputSimulator();

    // Create core engine
    var engine = new GazeEngine(gazeProvider, inputSimulator, settings);

    // Create UI overlays
    var cursorCircle = new CursorCircleWindow();
    var activationButton = new ActivationButtonWindow();
    var actionMenu = new ActionMenuWindow();
    var toolbar = new ToolbarWindow();
    var keyboard = new KeyboardWindow();
    var zoomOverlay = new ZoomOverlayWindow();

    // Wire events between engine and UI
    engine.CursorMoved += cursorCircle.UpdatePosition;
    engine.InteractionManager.ShowActivationButton += activationButton.Show;
    engine.InteractionManager.HideActivationButton += activationButton.Hide;
    engine.InteractionManager.ShowActionMenu += (point) =>
        actionMenu.Show(point, settings.ActionMenu.VisibleActions, /* showBack based on state */);
    engine.InteractionManager.HideActionMenu += actionMenu.Hide;
    engine.InteractionManager.StartZoom += (point) =>
        zoomOverlay.Show(point, settings.Zoom.CircleRadiusPixels, settings.Zoom.MagnificationLevel);
    engine.InteractionManager.EndZoom += zoomOverlay.Hide;

    actionMenu.ActionSelected += engine.InteractionManager.OnActionSelected;
    actionMenu.BackSelected += engine.InteractionManager.OnZoomBack;

    toolbar.PauseToggled += engine.InteractionManager.TogglePause;
    toolbar.ZoomToggled += () => { settings.Zoom.Enabled = !settings.Zoom.Enabled; engine.InteractionManager.ZoomEnabled = settings.Zoom.Enabled; };
    toolbar.KeyboardToggled += () => { if (keyboard.IsVisible) keyboard.Hide(); else keyboard.Show(); };

    // Show overlays and start
    cursorCircle.Show();
    toolbar.Show();
    engine.Start();
}
```

**Step 2: Suppress MainWindow (make App overlay-only)**

Set `ShutdownMode="OnExplicitShutdown"` in `App.xaml`. No MainWindow needed.

**Step 3: Build and manual test**

Run: `dotnet build src/EyeCtrl.App && dotnet run --project src/EyeCtrl.App`
Expected: Cursor circle follows mouse. Toolbar visible. Fixation triggers activation button. Action menu appears. Clicking an action simulates the input.

**Step 4: Commit**

```bash
git add -A && git commit -m "feat: wire all components together in App startup"
```

---

## Task 23: System Tray Integration

**Files:**
- Create: `src/EyeCtrl.App/SystemTray/TrayIconManager.cs`
- Modify: `src/EyeCtrl.App/App.xaml.cs`

**Step 1: Add system tray icon**

Since EyeCtrl has no main window, add a system tray icon with a context menu: Pause/Resume, Settings, Exit.

Use `System.Windows.Forms.NotifyIcon` (add `UseWindowsForms` to the App .csproj).

**Step 2: Wire to App.xaml.cs**

Exit menu item calls `Application.Current.Shutdown()`.

**Step 3: Commit**

```bash
git add -A && git commit -m "feat: add system tray icon with context menu"
```

---

## Task 24: Tobii Interaction Library Integration

**Files:**
- Create: `src/EyeCtrl.Platform/Gaze/TobiiInteractionLibProvider.cs`
- Modify: `src/EyeCtrl.Platform/EyeCtrl.Platform.csproj` (add DLL reference)
- Modify: `src/EyeCtrl.App/EyeCtrl.App.csproj` (copy native DLLs)

**Step 1: Add assembly reference**

Reference `tobii_interaction_lib_cs.dll` from `C:\Program Files (x86)\Tobii Dynavox\Computer Control\`.
Add to `EyeCtrl.Platform.csproj`:
```xml
<ItemGroup>
  <Reference Include="tobii_interaction_lib_cs">
    <HintPath>C:\Program Files (x86)\Tobii Dynavox\Computer Control\tobii_interaction_lib_cs.dll</HintPath>
  </Reference>
</ItemGroup>
```

Copy native DLLs to output. Add to `EyeCtrl.App.csproj`:
```xml
<ItemGroup>
  <None Include="C:\Program Files (x86)\Tobii Dynavox\Computer Control\tobii_interaction_lib.dll">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
  <None Include="C:\Program Files (x86)\Tobii Dynavox\Computer Control\tobii_interaction_lib_c.dll">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
</ItemGroup>
```

**Step 2: Implement TobiiInteractionLibProvider**

```csharp
// src/EyeCtrl.Platform/Gaze/TobiiInteractionLibProvider.cs
namespace EyeCtrl.Platform.Gaze;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using Tobii.InteractionLib;

public class TobiiInteractionLibProvider : IGazeProvider, IDisposable
{
    private IInteractionLib? _interactionLib;
    private Thread? _pollThread;
    private volatile bool _running;
    private readonly int _screenWidth;
    private readonly int _screenHeight;

    public event Action<GazePoint>? GazePointReceived;
    public bool IsConnected { get; private set; }

    public TobiiInteractionLibProvider(int screenWidth, int screenHeight)
    {
        _screenWidth = screenWidth;
        _screenHeight = screenHeight;
    }

    public void Start()
    {
        _interactionLib = InteractionLibFactory.CreateInteractionLib(FieldOfUse.Interactive);

        _interactionLib.CoordinateTransformAddOrUpdateDisplayArea(
            _screenWidth, _screenHeight,
            _screenWidth, _screenHeight,
            0, 0, "main");

        _interactionLib.GazePointDataEvent += OnGazePointData;

        _running = true;
        _pollThread = new Thread(PollLoop) { IsBackground = true, Name = "TobiiPoll" };
        _pollThread.Start();
        IsConnected = true;
    }

    public void Stop()
    {
        _running = false;
        _pollThread?.Join(1000);
        if (_interactionLib != null)
        {
            _interactionLib.GazePointDataEvent -= OnGazePointData;
            (_interactionLib as IDisposable)?.Dispose();
            _interactionLib = null;
        }
        IsConnected = false;
    }

    private void PollLoop()
    {
        while (_running)
        {
            _interactionLib?.WaitAndUpdate(33);
        }
    }

    private void OnGazePointData(GazePointData data)
    {
        bool valid = data.validity == Validity.Valid;
        // Coordinates from InteractionLib are in the coordinate space
        // set by CoordinateTransformAddOrUpdateDisplayArea (screen pixels)
        GazePointReceived?.Invoke(new GazePoint(data.x, data.y, data.timestamp_us, valid));
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
```

**Step 3: Update App.xaml.cs to prefer TobiiInteractionLibProvider**

Try Tobii first, fall back to MouseGazeProvider if DLL not found or device not connected.

```csharp
IGazeProvider gazeProvider;
try
{
    var tobii = new TobiiInteractionLibProvider(screenWidth, screenHeight);
    tobii.Start();
    gazeProvider = tobii;
}
catch (Exception)
{
    gazeProvider = new MouseGazeProvider();
}
```

**Step 4: Build and test with actual tracker**

Run: `dotnet build src/EyeCtrl.App && dotnet run --project src/EyeCtrl.App`
Expected: Cursor circle follows real gaze from PCEye 5.

**Step 5: Commit**

```bash
git add -A && git commit -m "feat: add Tobii Interaction Library gaze provider"
```

---

## Task 25: Run Full Test Suite and Polish

**Step 1: Run all tests**

Run: `dotnet test EyeCtrl.sln --verbosity normal`
Expected: All tests pass.

**Step 2: Run the app and manually verify**

1. Cursor circle follows gaze/mouse
2. Fixation shows activation button
3. Dwell on activation button shows action menu
4. Dwell on action fires it
5. Zoom mode works (activate → zoom → fixate → action menu with back button)
6. Toolbar: pause/resume, zoom toggle, keyboard toggle
7. Keyboard: dwell on keys types, prediction bar works
8. Settings: all tabs, save/load

**Step 3: Fix any issues found**

**Step 4: Final commit**

```bash
git add -A && git commit -m "chore: polish and fix issues from manual testing"
```

---

## Summary

| Task | Component | Type |
|------|-----------|------|
| 1 | Solution scaffolding | Setup |
| 2 | Core data types & interfaces | Core |
| 3 | Settings model & persistence | Core |
| 4 | Gaze filters (direct, smoothed) | Core + TDD |
| 5 | Fixation detector | Core + TDD |
| 6 | Dwell detector | Core + TDD |
| 7 | Interaction state machine | Core + TDD |
| 8 | Win32 input simulator | Platform |
| 9 | Mouse gaze provider | Platform |
| 10 | Word prediction engine | Core + TDD |
| 11 | Keyboard layout model | Core + TDD |
| 12 | GazeEngine orchestrator | Core + TDD |
| 13 | Cursor circle overlay | UI |
| 14 | Activation button overlay | UI |
| 15 | Radial action menu | UI |
| 16 | Toolbar | UI |
| 17 | Zoom overlay | UI |
| 18 | On-screen keyboard | UI |
| 19 | Built-in keyboard layouts | Resources |
| 20 | English dictionary | Resources |
| 21 | Settings window | UI |
| 22 | App integration | Integration |
| 23 | System tray | UI |
| 24 | Tobii Stream Engine | Platform |
| 25 | Full test & polish | QA |
