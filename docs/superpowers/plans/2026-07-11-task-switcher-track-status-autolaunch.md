# Task Switcher, Track Status Box, and Auto-Launch Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement three Tier-1 features from the competitive analysis: (1) make the `StartOnBoot` setting actually register the app for Windows startup, (2) add a gaze-dwellable fullscreen Task Switcher overlay triggered from the toolbar, and (3) add a Track Status box overlay showing eye position, presence, and distance-from-screen.

**Architecture:** Three independent checkpoints (A → B → C). Feature 1 adds an `IStartupRegistrar` seam in Core implemented in Platform with `Microsoft.Win32.Registry`. Feature 3 extends `IGazeProvider` with default interface members for track-status data, adds a `TrackStatusSample` payload + Tobii stream subscriptions, and a click-through corner overlay. Feature 2 adds an `IWindowManager` platform service + a fullscreen `TaskSwitcherWindow` overlay toggled from a 6th toolbar button. All overlays subclass `OverlayWindow`; all new ViewModels are registered as DI singletons in `AppBootstrapper`.

**Tech Stack:** WPF (.NET 10), CommunityToolkit.Mvvm v8.4.0 (`[ObservableProperty]` source generators), Win32 P/Invoke via `[LibraryImport]`/`[DllImport]`, Tobii Stream Engine (behind `#if TOBII_AVAILABLE`), xUnit + NFluent 3.1.0 (no mocking library — private nested fakes).

**Spec:** [`docs/superpowers/specs/2026-07-11-task-switcher-track-status-autolaunch-design.md`](../specs/2026-07-11-task-switcher-track-status-autolaunch-design.md)

**Critical ordering constraint:** Checkpoint B must not reference Feature 2 types (`IWindowManager`, `Win32WindowManager`, `IAppIconProvider`, `Win32AppIconProvider`, `TaskSwitcherViewModel`). The `EventCoordinationContext` positional record and its `App.xaml.cs:122` construction site change in **both** B and C — B adds `TrackStatusVM`, C adds `TaskSwitcherVM` + `IWindowManager`.

---

## Checkpoint A — Auto-Launch (Feature 1)

### Task A1: `IStartupRegistrar` interface + `StartupCommandValue` pure helper

**Files:**
- Create: `src/EyeCtrl.Core/Interfaces/IStartupRegistrar.cs`
- Create: `src/EyeCtrl.Platform/Services/StartupCommandValue.cs`
- Create: `tests/EyeCtrl.Platform.Tests/Services/StartupCommandValueTests.cs`

- [ ] **Step 1: Write the failing test for `StartupCommandValue`**

```csharp
// tests/EyeCtrl.Platform.Tests/Services/StartupCommandValueTests.cs
namespace EyeCtrl.Platform.Tests.Services;

using EyeCtrl.Platform.Services;

public class StartupCommandValueTests
{
    [Fact]
    public void BuildCommandValue_WrapsPathInQuotes()
    {
        var result = StartupCommandValue.BuildCommandValue(@"C:\Apps\EyeCtrl.exe");
        Check.That(result).IsEqualTo(@"""C:\Apps\EyeCtrl.exe""");
    }

    [Fact]
    public void BuildCommandValue_HandlesPathWithSpaces()
    {
        var result = StartupCommandValue.BuildCommandValue(@"C:\Program Files\Eye Ctrl\EyeCtrl.exe");
        Check.That(result).IsEqualTo(@"""C:\Program Files\Eye Ctrl\EyeCtrl.exe""");
    }

    [Fact]
    public void BuildCommandValue_AlreadyQuoted_DoesNotDoubleQuote()
    {
        var result = StartupCommandValue.BuildCommandValue(@"""C:\Apps\EyeCtrl.exe""");
        Check.That(result).IsEqualTo(@"""C:\Apps\EyeCtrl.exe""");
    }

    [Fact]
    public void BuildCommandValue_EmptyOrWhitespace_ReturnsEmpty()
    {
        Check.That(StartupCommandValue.BuildCommandValue("")).IsEmpty();
        Check.That(StartupCommandValue.BuildCommandValue("   ")).IsEmpty();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.Platform.Tests --filter "FullyQualifiedName~StartupCommandValueTests"`
Expected: FAIL (type `StartupCommandValue` not found)

- [ ] **Step 3: Write `IStartupRegistrar` interface**

```csharp
// src/EyeCtrl.Core/Interfaces/IStartupRegistrar.cs
namespace EyeCtrl.Core.Interfaces;

/// <summary>
/// Registers/unregisters the application for Windows startup.
/// Implementations write/remove a startup entry (e.g. registry Run key)
/// using the current process path.
/// </summary>
public interface IStartupRegistrar
{
    /// <summary>Sets whether the app starts on Windows boot.</summary>
    /// <param name="enabled">True to register; false to unregister.</param>
    void SetStartOnBoot(bool enabled);

    /// <summary>True if a startup entry currently exists for the app.</summary>
    bool IsRegistered { get; }
}
```

- [ ] **Step 4: Write `StartupCommandValue` pure helper**

```csharp
// src/EyeCtrl.Platform/Services/StartupCommandValue.cs
namespace EyeCtrl.Platform.Services;

/// <summary>
/// Pure helper that builds the registry Run-key value string for the app's exe path.
/// Extracted from <c>WindowsStartupRegistrar</c> so the quoting logic is unit-testable
/// without touching the real HKCU hive (mirrors the IMouseCoordinateMapper pattern).
/// </summary>
public static class StartupCommandValue
{
    /// <summary>
    /// Wraps the exe path in double quotes if not already quoted.
    /// Returns empty string for null/empty/whitespace input.
    /// </summary>
    public static string BuildCommandValue(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            return string.Empty;

        var trimmed = exePath.Trim();
        if (trimmed.StartsWith('"') && trimmed.EndsWith('"'))
            return trimmed;

        return $"""{trimmed}""";
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/EyeCtrl.Platform.Tests --filter "FullyQualifiedName~StartupCommandValueTests"`
Expected: PASS (4 tests)

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Core/Interfaces/IStartupRegistrar.cs src/EyeCtrl.Platform/Services/StartupCommandValue.cs tests/EyeCtrl.Platform.Tests/Services/StartupCommandValueTests.cs
git commit -m "feat(startup): add IStartupRegistrar interface + StartupCommandValue pure helper"
```

---

### Task A2: `WindowsStartupRegistrar` implementation

**Files:**
- Create: `src/EyeCtrl.Platform/Services/WindowsStartupRegistrar.cs`

- [ ] **Step 1: Write `WindowsStartupRegistrar`**

```csharp
// src/EyeCtrl.Platform/Services/WindowsStartupRegistrar.cs
namespace EyeCtrl.Platform.Services;

using EyeCtrl.Core.Interfaces;
using Microsoft.Win32;

/// <summary>
/// Registers/unregisters the app for Windows startup via the HKCU Run key.
/// Uses <see cref="Environment.ProcessPath"/> (the stable single-file bundle apphost path),
/// never <c>Assembly.Location</c> (which points into the varying temp extraction dir).
/// </summary>
public class WindowsStartupRegistrar : IStartupRegistrar
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "EyeCtrl";

    private readonly string _exePath;

    public WindowsStartupRegistrar()
    {
        // Environment.ProcessPath is the bundle apphost the user launched — stable across
        // single-file launches. Captured at construction (DI singleton), re-ensured on each
        // app startup by calling SetStartOnBoot again.
        _exePath = Environment.ProcessPath ?? string.Empty;
    }

    /// <inheritdoc />
    public void SetStartOnBoot(bool enabled)
    {
        // CreateSubKey is strictly safer than OpenSubKey(writable:true) — creates the key
        // if it doesn't exist (the Run key effectively always does per-user, but this
        // removes the null-check no-op risk entirely).
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (key == null) return;

        if (enabled)
        {
            var commandValue = StartupCommandValue.BuildCommandValue(_exePath);
            if (!string.IsNullOrEmpty(commandValue))
                key.SetValue(ValueName, commandValue);
        }
        else
        {
            // DeleteSubKey returns false if the value doesn't exist (no error)
            if (key.GetValue(ValueName) != null)
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <inheritdoc />
    public bool IsRegistered
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) != null;
        }
    }
}
```

- [ ] **Step 2: Verify the project builds (registry type is transitively available via UseWPF)**

Run: `dotnet build src/EyeCtrl.Platform/EyeCtrl.Platform.csproj`
Expected: BUILD SUCCEEDED (no `Microsoft.Win32.Registry` package reference needed — it's in the WindowsDesktop shared framework via `<UseWPF>true</UseWPF>` on `net10.0-windows`)

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.Platform/Services/WindowsStartupRegistrar.cs
git commit -m "feat(startup): add WindowsStartupRegistrar using HKCU Run key"
```

---

### Task A3: Register `IStartupRegistrar` in DI + reconcile on startup

**Files:**
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs` (line ~66, platform services section)
- Modify: `src/EyeCtrl.App/App.xaml.cs` `OnStartup` (after line 84, the `gazeProvider` resolve)

- [ ] **Step 1: Add DI registration in `AppBootstrapper.BuildServiceProvider`**

In `src/EyeCtrl.App/Services/AppBootstrapper.cs`, after the `IInputSimulator` registration (line 66), add:

```csharp
        // Startup registration
        services.AddSingleton<IStartupRegistrar, WindowsStartupRegistrar>();
```

Add the necessary `using` at the top of the file (if not already present):

```csharp
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Platform.Services;
```

- [ ] **Step 2: Add reconcile-on-startup in `App.OnStartup`**

In `src/EyeCtrl.App/App.xaml.cs`, after the `gazeProvider` resolve (line 84, `var gazeProvider = _serviceProvider.GetRequiredService<IGazeProvider>();`), add:

```csharp
            // Reconcile auto-launch registration with the persisted setting.
            // Idempotent: overwrites with current Environment.ProcessPath if enabled,
            // removes stale entry if disabled. Handles exe moving between versions/installs.
            var startupRegistrar = _serviceProvider.GetRequiredService<IStartupRegistrar>();
            startupRegistrar.SetStartOnBoot(settings.General.StartOnBoot);
```

- [ ] **Step 3: Add live-toggle subscription (mid-session setting change)**

In `src/EyeCtrl.App/App.xaml.cs`, after the reconcile call added above, add:

```csharp
            // Live-toggle: if the user changes StartOnBoot in settings, apply immediately.
            settings.General.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(settings.General.StartOnBoot))
                    startupRegistrar.SetStartOnBoot(settings.General.StartOnBoot);
            };
```

Add the `using EyeCtrl.Core.Interfaces;` import to `App.xaml.cs` if not already present.

- [ ] **Step 4: Build and verify**

Run: `dotnet build EyeCtrl.slnx`
Expected: BUILD SUCCEEDED

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Services/AppBootstrapper.cs src/EyeCtrl.App/App.xaml.cs
git commit -m "feat(startup): wire IStartupRegistrar in DI + reconcile on startup + live toggle"
```

---

### Task A4: `GeneralSettings` defaults test

**Files:**
- Create: `tests/EyeCtrl.Core.Tests/Settings/GeneralSettingsDefaultsTests.cs`

- [ ] **Step 1: Write the test**

```csharp
// tests/EyeCtrl.Core.Tests/Settings/GeneralSettingsDefaultsTests.cs
namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Settings;

public class GeneralSettingsDefaultsTests
{
    [Fact]
    public void Defaults_StartOnBootFalse_StartPausedFalse_GazeProviderAuto()
    {
        var g = new GeneralSettings();
        Check.That(g.StartOnBoot).IsFalse();
        Check.That(g.StartPaused).IsFalse();
        Check.That(g.GazeProviderType).IsEqualTo(GazeProviderType.Auto);
    }

    [Fact]
    public void Validate_DefaultInstance_HasNoErrors()
    {
        Check.That(new GeneralSettings().Validate()).IsEmpty();
    }
}
```

- [ ] **Step 2: Run the test**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~GeneralSettingsDefaultsTests"`
Expected: PASS (2 tests)

- [ ] **Step 3: Commit**

```bash
git add tests/EyeCtrl.Core.Tests/Settings/GeneralSettingsDefaultsTests.cs
git commit -m "test(startup): add GeneralSettings defaults tests"
```

---

### Task A5: Full build + test checkpoint

- [ ] **Step 1: Build the full solution**

Run: `dotnet build EyeCtrl.slnx`
Expected: BUILD SUCCEEDED, 0 errors

- [ ] **Step 2: Run all unit tests (excluding UI)**

Run: `dotnet test --filter "Category!=UI"`
Expected: ALL PASS

- [ ] **Step 3: Commit checkpoint (if any cleanup needed)**

If the build and tests pass with no changes needed, skip this step. Otherwise commit any fixes.

---

## Checkpoint B — Track Status Box (Feature 3)

### Task B1: `GazeProviderCapabilities` enum + `TrackStatusSample` / `EyeSample` models

**Files:**
- Create: `src/EyeCtrl.Core/Enums/GazeProviderCapabilities.cs`
- Create: `src/EyeCtrl.Core/Models/TrackStatusSample.cs`

- [ ] **Step 1: Write `GazeProviderCapabilities` flags enum**

```csharp
// src/EyeCtrl.Core/Enums/GazeProviderCapabilities.cs
namespace EyeCtrl.Core.Enums;

/// <summary>
/// Flags describing which optional data streams a gaze provider can supply.
/// Used by the Track Status box to decide what to show and whether to appear at all.
/// </summary>
[Flags]
public enum GazeProviderCapabilities
{
    None = 0,
    EyePosition = 1,
    HeadPose = 2,
    UserPresence = 4
}
```

- [ ] **Step 2: Write `TrackStatusSample` + `EyeSample` record structs**

```csharp
// src/EyeCtrl.Core/Models/TrackStatusSample.cs
namespace EyeCtrl.Core.Models;

/// <summary>
/// One eye-position / distance / presence sample from the tracker, for the Track Status box.
/// Separate from <see cref="GazePoint"/> so the hot gaze path is not widened.
/// </summary>
/// <param name="Left">Left eye position normalized in the track box (0..1 XYZ) + validity.</param>
/// <param name="Right">Right eye position normalized in the track box (0..1 XYZ) + validity.</param>
/// <param name="HeadDistanceMm">Head distance from the tracker in millimeters (from head_pose position z).</param>
/// <param name="HeadDistanceValid">Whether <paramref name="HeadDistanceMm"/> is valid.</param>
/// <param name="UserPresent">Whether the tracker reports a user present.</param>
/// <param name="TimestampMicroseconds">Tracker timestamp in microseconds.</param>
public readonly record struct TrackStatusSample(
    EyeSample Left,
    EyeSample Right,
    double HeadDistanceMm,
    bool HeadDistanceValid,
    bool UserPresent,
    long TimestampMicroseconds);

/// <summary>
/// One eye's position normalized in the track box (0..1 on each axis) + validity.
/// </summary>
/// <param name="X">Normalized horizontal position (0=left edge, 1=right edge of track box).</param>
/// <param name="Y">Normalized vertical position (0=top, 1=bottom).</param>
/// <param name="Z">Normalized depth (0=too close, 1=too far; ~0.5 is optimal).</param>
/// <param name="IsValid">True only when ALL three coordinates are valid (Tobii reports per-coordinate validity).</param>
public readonly record struct EyeSample(double X, double Y, double Z, bool IsValid);
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/EyeCtrl.Core/EyeCtrl.Core.csproj`
Expected: BUILD SUCCEEDED

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.Core/Enums/GazeProviderCapabilities.cs src/EyeCtrl.Core/Models/TrackStatusSample.cs
git commit -m "feat(track-status): add GazeProviderCapabilities enum + TrackStatusSample/EyeSample models"
```

---

### Task B2: Extend `IGazeProvider` with default interface members

**Files:**
- Modify: `src/EyeCtrl.Core/Interfaces/IGazeProvider.cs`

- [ ] **Step 1: Add DIM members to `IGazeProvider`**

In `src/EyeCtrl.Core/Interfaces/IGazeProvider.cs`, add `using EyeCtrl.Core.Enums;` and `using EyeCtrl.Core.Models;` (the latter is likely already present). After the existing `IsConnected` property (line 17), add:

```csharp
    /// <summary>
    /// Capabilities describing which optional data streams this provider can supply.
    /// Defaults to <see cref="GazeProviderCapabilities.None"/> — providers that support
    /// track-status data override this.
    /// </summary>
    GazeProviderCapabilities Capabilities => GazeProviderCapabilities.None;

    /// <summary>
    /// Raised when a new track-status sample (eye position, distance, presence) is available.
    /// Only raised by providers with the corresponding <see cref="Capabilities"/>.
    /// Default implementation is a no-op event so test fakes compile unchanged
    /// (mirrors the IInputSimulator DIM pattern at IInputSimulator.cs:53,58).
    /// </summary>
    /// <remarks>
    /// H4 note: this event is currently UNUSED — the sole data path is
    /// <see cref="TryGetLatestStatus"/>, polled by the window's DispatcherTimer.
    /// The event is retained as part of the interface for a future push-based
    /// model (e.g. a provider that fires samples on a non-poll thread without
    /// caching). The Tobii provider does NOT fire it. If YAGNI is preferred,
    /// remove this member entirely — the DIM default and TryGetLatestStatus
    /// are sufficient. Left in for now to avoid a second interface churn if
    /// the push model is needed later.
    /// </remarks>
    event Action<TrackStatusSample>? TrackStatusReceived { add { } remove { } }

    /// <summary>
    /// Thread-safe read of the latest cached track-status sample.
    /// Default returns false (no track-status data). The Tobii provider overrides this.
    /// This is the sole data path used by the Track Status box (the window's DispatcherTimer
    /// polls it at ~15 Hz). The <see cref="TrackStatusReceived"/> event above is currently unused.
    /// </summary>
    bool TryGetLatestStatus(out TrackStatusSample sample)
    {
        sample = default;
        return false;
    }
```

- [ ] **Step 2: Build Core to verify the DIMs compile**

Run: `dotnet build src/EyeCtrl.Core/EyeCtrl.Core.csproj`
Expected: BUILD SUCCEEDED

- [ ] **Step 3: Build the full solution to verify test fakes compile unchanged**

Run: `dotnet build EyeCtrl.slnx`
Expected: BUILD SUCCEEDED — all 8 test/benchmark fakes compile via the DIM defaults (no edits needed to `FakeGazeProvider`, `TestGazeProvider`, `CountingGazeProvider`, `FakeGazeProviderForTests`, `StubGazeProvider`, or the benchmark `FakeGazeProvider`).

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.Core/Interfaces/IGazeProvider.cs
git commit -m "feat(track-status): add Capabilities + TrackStatusReceived + TryGetLatestStatus as IGazeProvider DIMs"
```

---

### Task B3: `TrackStatusSettings` + register in `EyeCtrlSettings`

**Files:**
- Create: `src/EyeCtrl.Core/Settings/TrackStatusSettings.cs`
- Modify: `src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs` (add field + enumeration entry)
- Modify: `tests/EyeCtrl.Core.Tests/Settings/EyeCtrlSettingsEnumerationTests.cs` (update count 15 → 16)

- [ ] **Step 1: Write `TrackStatusSettings`**

```csharp
// src/EyeCtrl.Core/Settings/TrackStatusSettings.cs
namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>Settings for the Track Status overlay box.</summary>
public partial class TrackStatusSettings : ObservableObject, IValidatable
{
    /// <summary>Whether the track status overlay is shown.</summary>
    [ObservableProperty] private bool _enabled = false;

    /// <inheritdoc />
    public IReadOnlyList<string> Validate()
    {
        // TrackStatusSettings has no numeric ranges to validate
        return new List<string>();
    }
}
```

- [ ] **Step 2: Add to `EyeCtrlSettings`**

In `src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs`, after the `_drag` field (line 42), add:

```csharp
    /// <summary>Track status overlay box settings.</summary>
    [ObservableProperty] private TrackStatusSettings _trackStatus = new();
```

In the `EnumerateSubSettings()` method (after `yield return (nameof(Drag), Drag);`, line 66), add:

```csharp
        yield return (nameof(TrackStatus), TrackStatus);
```

- [ ] **Step 3: Update the enumeration drift-guard test**

In `tests/EyeCtrl.Core.Tests/Settings/EyeCtrlSettingsEnumerationTests.cs`, change the `HasSize(15)` assertion to `HasSize(16)`:

```csharp
    [Fact]
    public void EnumerateSubSettings_Yields16Entries_AllValidatable()
    {
        var subs = new EyeCtrlSettings().EnumerateSubSettings().ToList();

        Check.That(subs).HasSize(16);
        Check.That(subs.All(t => t.Settings is IValidatable)).IsTrue();
    }
```

- [ ] **Step 4: Run tests to verify**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~EyeCtrlSettingsEnumerationTests"`
Expected: PASS (3 tests, including the updated count)

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Settings/TrackStatusSettings.cs src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs tests/EyeCtrl.Core.Tests/Settings/EyeCtrlSettingsEnumerationTests.cs
git commit -m "feat(track-status): add TrackStatusSettings + register in EyeCtrlSettings"
```

---

### Task B4: Extend `TobiiStreamEngineProvider` with track-status streams + cache

**Files:**
- Modify: `src/EyeCtrl.Platform/Gaze/TobiiStreamEngineProvider.cs` (behind `#if TOBII_AVAILABLE`)

> **⚠ Highest-uncertainty item — pre-flight check (L1):** before writing this task, verify that the referenced `Tobii.StreamEngine.Net.dll` exposes `tobii_eye_position_normalized_subscribe`, `tobii_head_pose_subscribe`, `tobii_user_presence_subscribe` and their struct types (`tobii_eye_position_normalized_t`, `tobii_head_pose_t`, `tobii_user_presence_t`) at compile time. Run this check first:
>
> ```bash
> # List all subscribe methods in the Tobii Stream Engine interop DLL
> dotnet ildasm "C:\Program Files\Tobii\Tobii EyeX\Tobii.StreamEngine.Net.dll" 2>/dev/null | grep -i subscribe
> # Or if ildasm isn't available, use reflection:
> dotnet run --project samples/StreamEngineGazeTracker -- --list-streams 2>/dev/null || true
> ```
>
> The SDK DLLs are at `C:\Program Files\Tobii\Tobii EyeX\`. If the wrapper lacks these methods, fall back to `[LibraryImport]` P-Invoke signatures against the stream-engine C API (`tobii_eye_position_normalized_subscribe` etc. in `tobii_stream_engine.dll`). **Run the impact analysis on `TobiiStreamEngineProvider` before editing.** If the streams are entirely unavailable, skip the Tobii provider changes (leave `Capabilities = None` via the DIM default) and the Track Status box will simply not appear for Tobii users — the mouse-provider degradation path handles this gracefully.

- [ ] **Step 1: Run impact analysis on `TobiiStreamEngineProvider`**

Use GitNexus `impact({target: "TobiiStreamEngineProvider", direction: "upstream"})` and report the blast radius. Proceed only if risk is LOW/MEDIUM.

- [ ] **Step 2: Add track-status callback fields + cache to the provider**

In `src/EyeCtrl.Platform/Gaze/TobiiStreamEngineProvider.cs`, add the following fields (after the existing `_gazeCallback` field, line 31):

```csharp
    // Track-status callbacks (rooted to prevent GC, same pattern as _gazeCallback)
    private tobii_eye_position_normalized_callback_t? _eyePositionCallback;
    private tobii_head_pose_callback_t? _headPoseCallback;
    private tobii_user_presence_callback_t? _presenceCallback;

    // Latest track-status sample cache (written on poll thread, read by UI timer)
    private readonly object _statusLock = new();
    private TrackStatusSample _latestStatus;
    private bool _hasStatus;  // not reset-on-read, so "has any" not "has new" — idempotent at 15 Hz
```

Add `using EyeCtrl.Core.Enums;` and `using EyeCtrl.Core.Models;` to the file's usings (inside the `#if TOBII_AVAILABLE` block).

- [ ] **Step 3: Override `Capabilities` and add `TryGetLatestStatus` accessor**

Add these members to the class:

```csharp
    public GazeProviderCapabilities Capabilities => GazeProviderCapabilities.EyePosition | GazeProviderCapabilities.HeadPose | GazeProviderCapabilities.UserPresence;

    /// <summary>
    /// Thread-safe read of the latest cached track-status sample.
    /// Called by the UI-thread DispatcherTimer (~15 Hz); never called from the poll thread.
    /// </summary>
    public bool TryGetLatestStatus(out TrackStatusSample sample)
    {
        lock (_statusLock)
        {
            sample = _latestStatus;
            return _hasStatus;
        }
    }
```

- [ ] **Step 4: Subscribe to the three new streams in `SubscribeGaze`**

In the `SubscribeGaze()` method (after the existing `tobii_gaze_point_subscribe` call, line 167), add the three subscriptions. Each callback writes into the `_latestStatus` cache under `_statusLock`:

```csharp
        // Eye position in track box (normalized 0..1 XYZ per eye)
        _eyePositionCallback = (ref tobii_eye_position_normalized_t eyePos, IntPtr userData) =>
        {
            // IsValid = ALL three coordinate validities are TOBII_VALIDITY_VALID
            // (Tobii reports per-coordinate validity; flattening to one bool for dot rendering)
            lock (_statusLock)
            {
                _latestStatus = new TrackStatusSample(
                    Left: new EyeSample(
                        eyePos.left.x, eyePos.left.y, eyePos.left.z,
                        eyePos.left_validity == tobii_validity_t.TOBII_VALIDITY_VALID),
                    Right: new EyeSample(
                        eyePos.right.x, eyePos.right.y, eyePos.right.z,
                        eyePos.right_validity == tobii_validity_t.TOBII_VALIDITY_VALID),
                    HeadDistanceMm: _latestStatus.HeadDistanceMm,
                    HeadDistanceValid: _latestStatus.HeadDistanceValid,
                    UserPresent: _latestStatus.UserPresent,
                    TimestampMicroseconds: eyePos.timestamp_us);
                _hasStatus = true;
            }
        };
        Interop.tobii_eye_position_normalized_subscribe(_deviceContext, _eyePositionCallback, IntPtr.Zero);

        // Head pose (distance = position z in mm)
        _headPoseCallback = (ref tobii_head_pose_t headPose, IntPtr userData) =>
        {
            lock (_statusLock)
            {
                _latestStatus = new TrackStatusSample(
                    Left: _latestStatus.Left,
                    Right: _latestStatus.Right,
                    HeadDistanceMm: headPose.position.z,
                    HeadDistanceValid: headPose.position_validity == tobii_validity_t.TOBII_VALIDITY_VALID,
                    UserPresent: _latestStatus.UserPresent,
                    TimestampMicroseconds: headPose.timestamp_us);
                _hasStatus = true;
            }
        };
        Interop.tobii_head_pose_subscribe(_deviceContext, _headPoseCallback, IntPtr.Zero);

        // User presence
        _presenceCallback = (ref tobii_user_presence_t presence, IntPtr userData) =>
        {
            lock (_statusLock)
            {
                _latestStatus = new TrackStatusSample(
                    Left: _latestStatus.Left,
                    Right: _latestStatus.Right,
                    HeadDistanceMm: _latestStatus.HeadDistanceMm,
                    HeadDistanceValid: _latestStatus.HeadDistanceValid,
                    UserPresent: presence.is_user_present,
                    TimestampMicroseconds: presence.timestamp_us);
                _hasStatus = true;
            }
        };
        Interop.tobii_user_presence_subscribe(_deviceContext, _presenceCallback, IntPtr.Zero);
```

> **Note:** the exact struct field names (`eyePos.left.x`, `headPose.position.z`, `presence.is_user_present`) depend on the `Tobii.StreamEngine.Net.dll` wrapper. Verify against the actual interop types at impl time — the field names may differ slightly (e.g. `position_xyz` vs `position`). Adjust to match the real struct layout.

- [ ] **Step 5: Unsubscribe in `CleanupDevice` (drain callbacks before clearing device context)**

In `CleanupDevice()` (after the existing `tobii_gaze_point_unsubscribe` call, line 249), add the three unsubscribes. **Critical:** the unsubscribes must run while `_deviceContext` is still valid (not yet zeroed), and the `_statusLock` must be taken AFTER the unsubscribes complete (not during) to avoid a deadlock where a callback is mid-flight waiting on `_statusLock` while `CleanupDevice` holds `_lock`. The existing `CleanupDevice` is already called under `_lock` (from `Stop()` line 75), so the poll loop is blocked — but callbacks may still be in-flight from the last `tobii_device_process_callbacks`. The order is: unsubscribe (stops new callbacks) → clear cache under `_statusLock` → null `_deviceContext`.

```csharp
            // Unsubscribe track-status streams BEFORE nulling _deviceContext.
            // These calls are safe only while _deviceContext is valid.
            Interop.tobii_eye_position_normalized_unsubscribe(_deviceContext);
            Interop.tobii_head_pose_unsubscribe(_deviceContext);
            Interop.tobii_user_presence_unsubscribe(_deviceContext);

            // Clear the status cache so stale data isn't served after shutdown.
            // _statusLock is separate from _lock (device lifecycle); take it only briefly
            // to avoid contention with any in-flight callback that hasn't yet returned.
            lock (_statusLock)
            {
                _latestStatus = default;
                _hasStatus = false;
            }
```

> **M1 mitigation (cleanup race):** the existing `Stop()` (line 63–81) sets `_running = false`, signals `_stopSignal`, joins the poll thread (2s timeout), then calls `CleanupDevice` under `_lock`. If the join times out (poll thread still running), `CleanupDevice` nulls `_deviceContext` while callbacks could still fire. The three new unsubscribes run first (before the null), which stops the Stream Engine from invoking the callbacks — but an already-in-flight callback could still write to `_latestStatus` after the unsubscribe returns. The `lock (_statusLock)` cache-clear above ensures the final state is `default`/`false` regardless. If a stale write lands after the clear, it's harmless (the UI timer has stopped by then since `TrackStatusViewModel.Stop()` was called).

- [ ] **Step 6: Build (only compiles if TOBII_AVAILABLE is defined — SDK present on this machine)**

Run: `dotnet build src/EyeCtrl.Platform/EyeCtrl.Platform.csproj`
Expected: BUILD SUCCEEDED. If the Tobii wrapper lacks the subscribe methods/structs, add `[LibraryImport]` P-Invoke signatures against the C API and adjust the struct definitions.

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.Platform/Gaze/TobiiStreamEngineProvider.cs
git commit -m "feat(track-status): subscribe Tobii eye-position/head-pose/presence streams + cache"
```

---

### Task B5: `TrackStatusViewModel` (WPF-free — no DispatcherTimer)

**Files:**
- Create: `src/EyeCtrl.ViewModels/TrackStatusViewModel.cs`

> **H1 fix:** `EyeCtrl.ViewModels` targets plain `net10.0` (verified: `EyeCtrl.ViewModels.csproj:4`), with zero existing WPF type references. `DispatcherTimer` lives in `System.Windows.Threading` (WPF-only). **The VM must NOT own the timer.** Instead, the VM is a pure data holder; the `TrackStatusWindow` (in `EyeCtrl.App`, `net10.0-windows`) owns the `DispatcherTimer` and calls `vm.PollLatest()` on each tick. This keeps the VM `net10.0`-clean and testable without a live WPF dispatcher.

- [ ] **Step 1: Write `TrackStatusViewModel` (no WPF types)**

```csharp
// src/EyeCtrl.ViewModels/TrackStatusViewModel.cs
namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

/// <summary>
/// ViewModel for the Track Status box. Pure data holder — no DispatcherTimer
/// (that lives in <c>TrackStatusWindow</c>, since ViewModels targets plain net10.0).
/// The window calls <see cref="PollLatest"/> on each timer tick (~15 Hz).
/// </summary>
public partial class TrackStatusViewModel : ObservableObject, IDisposable
{
    private readonly IGazeProvider _gazeProvider;

    [ObservableProperty] private double _leftEyeX;
    [ObservableProperty] private double _leftEyeY;
    [ObservableProperty] private bool _leftEyeValid;
    [ObservableProperty] private double _rightEyeX;
    [ObservableProperty] private double _rightEyeY;
    [ObservableProperty] private bool _rightEyeValid;
    [ObservableProperty] private bool _userPresent;
    [ObservableProperty] private double _headDistanceMm;
    [ObservableProperty] private bool _headDistanceValid;
    [ObservableProperty] private string _distanceQuality = "Unknown";
    [ObservableProperty] private bool _isVisible;

    public TrackStatusViewModel(IGazeProvider gazeProvider)
    {
        _gazeProvider = gazeProvider;
    }

    /// <summary>
    /// Returns true if the provider can supply track-status data.
    /// The window checks this before starting its timer.
    /// </summary>
    public bool HasCapability => _gazeProvider.Capabilities != GazeProviderCapabilities.None;

    /// <summary>Shows the overlay. The window starts its timer after this returns true.</summary>
    public void Start()
    {
        IsVisible = HasCapability;
    }

    /// <summary>Hides the overlay. The window stops its timer.</summary>
    public void Stop()
    {
        IsVisible = false;
    }

    /// <summary>
    /// Called by the window's DispatcherTimer (~15 Hz). Reads the latest cached
    /// sample from the provider (thread-safe) and updates observable properties.
    /// </summary>
    public void PollLatest()
    {
        if (_gazeProvider.TryGetLatestStatus(out var sample))
            UpdateFromSample(sample);
    }

    private void UpdateFromSample(TrackStatusSample sample)
    {
        LeftEyeX = sample.Left.X;
        LeftEyeY = sample.Left.Y;
        LeftEyeValid = sample.Left.IsValid;
        RightEyeX = sample.Right.X;
        RightEyeY = sample.Right.Y;
        RightEyeValid = sample.Right.IsValid;
        UserPresent = sample.UserPresent;
        HeadDistanceMm = sample.HeadDistanceMm;
        HeadDistanceValid = sample.HeadDistanceValid;
        DistanceQuality = ComputeDistanceQuality(sample.HeadDistanceMm, sample.HeadDistanceValid);
    }

    /// <summary>
    /// Maps head distance (mm) to a quality label.
    /// PCEye 5 optimal range ~450–750 mm. Thresholds TBD against real data at impl time.
    /// </summary>
    private static string ComputeDistanceQuality(double distanceMm, bool valid)
    {
        if (!valid) return "Unknown";
        if (distanceMm >= 450 && distanceMm <= 750) return "Good";
        if (distanceMm >= 350 && distanceMm <= 900) return "Fair";
        return "Poor";
    }

    public void Dispose()
    {
        // No timer to dispose — the window owns it.
    }
}
```

- [ ] **Step 2: Confirm `TryGetLatestStatus` DIM is on `IGazeProvider` (declared in Task B2)**

All three DIMs (`Capabilities`, `TrackStatusReceived`, `TryGetLatestStatus`) were declared together in Task B2 step 1. No additional interface change needed here — the VM calls `_gazeProvider.TryGetLatestStatus(out var sample)` which resolves to the DIM default (`return false`) for the mouse provider and test fakes, and to the Tobii override for the real tracker.

- [ ] **Step 3: Build ViewModels (must compile on plain net10.0 — no WPF types)**

Run: `dotnet build src/EyeCtrl.ViewModels/EyeCtrl.ViewModels.csproj`
Expected: BUILD SUCCEEDED. Verify no `System.Windows.*` usings leaked in — `TrackStatusViewModel` uses only `CommunityToolkit.Mvvm`, `EyeCtrl.Core.*`, and BCL types.

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.ViewModels/TrackStatusViewModel.cs src/EyeCtrl.Core/Interfaces/IGazeProvider.cs
git commit -m "feat(track-status): add WPF-free TrackStatusViewModel (timer in window)"
```

---

### Task B6: `TrackStatusWindow` overlay (XAML + code-behind)

**Files:**
- Create: `src/EyeCtrl.App/Overlays/TrackStatusWindow.xaml`
- Create: `src/EyeCtrl.App/Overlays/TrackStatusWindow.xaml.cs`

- [ ] **Step 1: Write the XAML**

```xml
<!-- src/EyeCtrl.App/Overlays/TrackStatusWindow.xaml -->
<overlays:OverlayWindow x:Class="EyeCtrl.App.Overlays.TrackStatusWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:overlays="clr-namespace:EyeCtrl.App.Overlays"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        Topmost="True" ShowInTaskbar="False" IsClickThrough="True" ResizeMode="NoResize"
        Width="160" Height="110">
    <Border Background="#80000000" CornerRadius="8" Padding="8">
        <StackPanel>
            <TextBlock Text="Track Status" Foreground="White" FontSize="10" FontWeight="Bold" Margin="0,0,0,4"/>
            <!-- Track box with eye dots -->
            <Canvas Width="60" Height="40" HorizontalAlignment="Left" Margin="0,0,8,0">
                <Border Width="60" Height="40" BorderBrush="White" BorderThickness="1" Background="#20FFFFFF"/>
                <Ellipse x:Name="LeftEyeDot" Width="6" Height="6" Fill="LimeGreen"/>
                <Ellipse x:Name="RightEyeDot" Width="6" Height="6" Fill="LimeGreen"/>
            </Canvas>
            <StackPanel Orientation="Horizontal" Margin="0,4,0,0">
                <TextBlock Text="Distance: " Foreground="White" FontSize="10"/>
                <TextBlock x:Name="DistanceText" Text="—" Foreground="White" FontSize="10"/>
            </StackPanel>
            <StackPanel Orientation="Horizontal">
                <TextBlock Text="Quality: " Foreground="White" FontSize="10"/>
                <TextBlock x:Name="QualityText" Text="Unknown" Foreground="Yellow" FontSize="10"/>
            </StackPanel>
        </StackPanel>
    </Border>
</overlays:OverlayWindow>
```

- [ ] **Step 2: Write the code-behind (owns the DispatcherTimer — H1 fix)**

> **H1 fix:** the `DispatcherTimer` lives here in the window (App, `net10.0-windows`), NOT in the VM (ViewModels, `net10.0`). The window's timer calls `vm.PollLatest()` on each tick (~15 Hz), which reads the provider's cache and updates the VM's observable properties. The VM stays WPF-free and testable.

```csharp
// src/EyeCtrl.App/Overlays/TrackStatusWindow.xaml.cs
namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EyeCtrl.ViewModels;

public partial class TrackStatusWindow : OverlayWindow
{
    private TrackStatusViewModel? _viewModel;
    private readonly DispatcherTimer _timer;

    public TrackStatusWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(66) // ~15 Hz
        };
        _timer.Tick += OnTimerTick;
    }

    public void Configure(TrackStatusViewModel viewModel)
    {
        _viewModel = viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    /// <summary>Called by EventCoordinator when TrackStatusSettings.Enabled is toggled on.</summary>
    public void StartTracking()
    {
        if (_viewModel == null) return;
        _viewModel.Start();
        if (_viewModel.IsVisible)
        {
            PositionTopRight();
            Show();
            _timer.Start();
        }
    }

    /// <summary>Called by EventCoordinator when TrackStatusSettings.Enabled is toggled off.</summary>
    public void StopTracking()
    {
        _timer.Stop();
        _viewModel?.Stop();
        Hide();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        // Poll the provider's cache (thread-safe) and update VM properties.
        _viewModel?.PollLatest();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_viewModel == null) return;

        // Position eye dots in the track box canvas (0..1 → canvas coords)
        if (e.PropertyName is nameof(_viewModel.LeftEyeX) or nameof(_viewModel.LeftEyeY))
        {
            if (_viewModel.LeftEyeValid)
            {
                Canvas.SetLeft(LeftEyeDot, _viewModel.LeftEyeX * 54);
                Canvas.SetTop(LeftEyeDot, _viewModel.LeftEyeY * 34);
                LeftEyeDot.Fill = Brushes.LimeGreen;
                LeftEyeDot.Visibility = Visibility.Visible;
            }
            else
                LeftEyeDot.Visibility = Visibility.Collapsed;
        }
        if (e.PropertyName is nameof(_viewModel.RightEyeX) or nameof(_viewModel.RightEyeY))
        {
            if (_viewModel.RightEyeValid)
            {
                Canvas.SetLeft(RightEyeDot, _viewModel.RightEyeX * 54);
                Canvas.SetTop(RightEyeDot, _viewModel.RightEyeY * 34);
                RightEyeDot.Fill = Brushes.LimeGreen;
                RightEyeDot.Visibility = Visibility.Visible;
            }
            else
                RightEyeDot.Visibility = Visibility.Collapsed;
        }
        if (e.PropertyName == nameof(_viewModel.HeadDistanceMm))
            DistanceText.Text = _viewModel.HeadDistanceValid
                ? $"{_viewModel.HeadDistanceMm:F0} mm"
                : "—";
        if (e.PropertyName == nameof(_viewModel.DistanceQuality))
        {
            QualityText.Text = _viewModel.DistanceQuality;
            QualityText.Foreground = _viewModel.DistanceQuality switch
            {
                "Good" => Brushes.LimeGreen,
                "Fair" => Brushes.Orange,
                "Poor" => Brushes.Red,
                _ => Brushes.Gray
            };
        }
    }

    /// <summary>Positions the window at the top-right corner of the working area.</summary>
    public void PositionTopRight()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 10;
        Top = workArea.Top + 10;
    }
}
```

- [ ] **Step 3: Build App**

Run: `dotnet build src/EyeCtrl.App/EyeCtrl.App.csproj`
Expected: BUILD SUCCEEDED

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.App/Overlays/TrackStatusWindow.xaml src/EyeCtrl.App/Overlays/TrackStatusWindow.xaml.cs
git commit -m "feat(track-status): add TrackStatusWindow click-through overlay"
```

---

### Task B7: Wire Track Status into `OverlayManager`, `OverlayViewModels`, `AppBootstrapper`, `EventCoordinationContext`

**Files:**
- Modify: `src/EyeCtrl.App/Services/IOverlayManager.cs` (add `TrackStatusWindow` property + `TrackStatusViewModel` to `OverlayViewModels` record)
- Modify: `src/EyeCtrl.App/Services/OverlayManager.cs` (instantiate in `Initialize`)
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs` (register `TrackStatusViewModel` + resolve in `ResolveViewModels`)
- Modify: `src/EyeCtrl.App/Services/IEventCoordinator.cs` (add `TrackStatusVM` to `EventCoordinationContext`)
- Modify: `src/EyeCtrl.App/App.xaml.cs` (add to `ResolveViewModels` + `EventCoordinationContext` construction + `OverlayViewModels`)

> **Run impact analysis on `EventCoordinationContext` and `OverlayViewModels` before editing — they are positional records used at multiple sites.**

- [ ] **Step 1: Run impact analysis on `EventCoordinationContext`**

Use GitNexus `impact({target: "EventCoordinationContext", direction: "upstream"})`. Report blast radius. The construction site at `App.xaml.cs:122` and the record definition at `IEventCoordinator.cs:51` are the two sites to edit.

- [ ] **Step 2: Add `TrackStatusWindow` to `IOverlayManager`**

In `src/EyeCtrl.App/Services/IOverlayManager.cs`, add the property (after `EdgeButtonBarWindow`, ~line 69):

```csharp
    TrackStatusWindow TrackStatus { get; }
```

Add `TrackStatusViewModel` to the `OverlayViewModels` record (after `EdgeButtonBarViewModel`, ~line 93):

```csharp
    TrackStatusViewModel TrackStatus);
```

- [ ] **Step 3: Instantiate in `OverlayManager.Initialize`**

In `src/EyeCtrl.App/Services/OverlayManager.cs`, add (after the `EdgeButtonBar` instantiation, ~line 100):

```csharp
        TrackStatus = new TrackStatusWindow();
        TrackStatus.PositionTopRight();
        TrackStatus.Configure(viewModels.TrackStatus);
```

And add the property declaration (next to the other overlay properties, ~line 33):

```csharp
    public TrackStatusWindow TrackStatus { get; private set; } = null!;
```

- [ ] **Step 4: Register `TrackStatusViewModel` in `AppBootstrapper`**

In `src/EyeCtrl.App/Services/AppBootstrapper.cs`, after `services.AddSingleton<EdgeButtonBarViewModel>();` (line 152), add:

```csharp
        services.AddSingleton<TrackStatusViewModel>();
```

In `ResolveViewModels` (after `EdgeButtonBar: sp.GetRequiredService<EdgeButtonBarViewModel>()`, ~line 231), add:

```csharp
            TrackStatus: sp.GetRequiredService<TrackStatusViewModel>());
```

(Remove the trailing `)` from the `EdgeButtonBar` line and move it to this new last line.)

- [ ] **Step 5: Add `TrackStatusVM` to `EventCoordinationContext`**

In `src/EyeCtrl.App/Services/IEventCoordinator.cs`, add after `INoteManager NoteManager` (line 80) — this is the **last** positional parameter:

```csharp
    INoteManager NoteManager,
    TrackStatusViewModel TrackStatusVM);
```

(Remove the `)` from the `NoteManager` line.)

- [ ] **Step 6: Update `App.xaml.cs` construction site**

In `src/EyeCtrl.App/App.xaml.cs`, update the `EventCoordinationContext` construction (lines 122–126) to add the new `TrackStatusVM` argument:

```csharp
            var trackStatusVm = _serviceProvider.GetRequiredService<TrackStatusViewModel>();
            var coordinationContext = new EventCoordinationContext(
                cursorVm, activationVm, manualSelectionVm, manualSelectionIndicatorVm, actionMenuVm, toolbarVm, keyboardVm, backdropVm, dragTrailVm,
                overlayManager, gazeEngine, im, messenger, Dispatcher, settingsService, settings, targetHighlightVm, inputSimulator,
                _serviceProvider.GetRequiredService<CalibrationService>(),
                noteManager,
                trackStatusVm);
```

Also update `ResolveViewModels` in `App.xaml.cs` (the deconstruct at line 94) to include `trackStatusVm`, and the `OverlayViewModels` construction (line 105) to add `TrackStatus: trackStatusVm`.

- [ ] **Step 7: Build the full solution**

Run: `dotnet build EyeCtrl.slnx`
Expected: BUILD SUCCEEDED — all positional-record sites updated in lockstep.

- [ ] **Step 8: Commit**

```bash
git add src/EyeCtrl.App/Services/IOverlayManager.cs src/EyeCtrl.App/Services/OverlayManager.cs src/EyeCtrl.App/Services/AppBootstrapper.cs src/EyeCtrl.App/Services/IEventCoordinator.cs src/EyeCtrl.App/App.xaml.cs
git commit -m "feat(track-status): wire TrackStatusVM into OverlayManager + EventCoordinationContext"
```

---

### Task B8: Gate show/hide in `EventCoordinator` + settings toggle

**Files:**
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs` (gate on `settings.TrackStatus.Enabled`)
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` (add toggle in appearance/settings page)

- [ ] **Step 1: Add show/hide gating in `EventCoordinator.Initialize`**

In `src/EyeCtrl.App/Services/EventCoordinator.cs`, in the `Initialize` method (where other settings-driven show/hide gates live, near the `ShowBackdropIfEnabled` call site), add:

```csharp
            // Track status overlay
            UpdateTrackStatusVisibility();
            _context.Settings.TrackStatus.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(_context.Settings.TrackStatus.Enabled))
                    _context.Dispatcher.BeginInvoke(UpdateTrackStatusVisibility);
            };
```

- [ ] **Step 2: Add the `UpdateTrackStatusVisibility` method**

```csharp
    private void UpdateTrackStatusVisibility()
    {
        if (_context == null) return;
        var trackStatusWindow = _context.OverlayManager.TrackStatus;
        if (_context.Settings.TrackStatus.Enabled)
            trackStatusWindow.StartTracking();  // window starts its DispatcherTimer + calls vm.Start()
        else
            trackStatusWindow.StopTracking();   // window stops timer + calls vm.Stop() + hides
    }
```

- [ ] **Step 3: Add the settings toggle in `SettingsWindow`**

In `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs`, add the toggle to `BuildGeneralPages` (line 864) — the same method that contains the "Start on Windows Boot" and "Start Paused" toggles. Add it after the existing general toggles (around line 879):

```csharp
                () => Add(new DwellToggle(_viewModel, "g_track", "Show Track Status",
                    () => _settings.TrackStatus.Enabled,
                    v => { _settings.TrackStatus.Enabled = v; ApplyAndSave(); },
                    cat, r, _dwellMs)),
```

> **Note:** `BuildAppearancePages` does not exist — the page builders are `BuildCursorPages` (line 371, contains the high-contrast toggle at line 410), `BuildDwellPages`, `BuildSelectionPages`, `BuildActionMenuPages`, `BuildLayoutPages`, `BuildKeyboardPages`, and `BuildGeneralPages` (line 864). The Track Status toggle belongs in `BuildGeneralPages` alongside the other app-level toggles.

- [ ] **Step 4: Build + run tests**

Run: `dotnet build EyeCtrl.slnx && dotnet test --filter "Category!=UI"`
Expected: BUILD SUCCEEDED + ALL TESTS PASS

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Services/EventCoordinator.cs src/EyeCtrl.App/Views/SettingsWindow.xaml.cs
git commit -m "feat(track-status): gate show/hide on TrackStatusSettings.Enabled + settings toggle"
```

---

### Task B9: Track Status tests + full checkpoint

**Files:**
- Create: `tests/EyeCtrl.Core.Tests/Settings/TrackStatusSettingsDefaultsTests.cs`
- Create: `tests/EyeCtrl.ViewModels.Tests/TrackStatusViewModelTests.cs`

- [ ] **Step 1: Write `TrackStatusSettings` defaults test**

```csharp
// tests/EyeCtrl.Core.Tests/Settings/TrackStatusSettingsDefaultsTests.cs
namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;

public class TrackStatusSettingsDefaultsTests
{
    [Fact]
    public void Default_DisabledAndValidates()
    {
        var s = new TrackStatusSettings();
        Check.That(s.Enabled).IsFalse();
        Check.That(s.Validate()).IsEmpty();
    }
}
```

- [ ] **Step 2: Write `TrackStatusViewModel` test with a fake provider (no WPF types — ViewModels.Tests targets net10.0)**

```csharp
// tests/EyeCtrl.ViewModels.Tests/TrackStatusViewModelTests.cs
namespace EyeCtrl.ViewModels.Tests;

using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

public class TrackStatusViewModelTests
{
    [Fact]
    public void Start_WithCapabilityNone_DoesNotShow()
    {
        var provider = new FakeTrackStatusProvider { Caps = GazeProviderCapabilities.None };
        using var vm = new TrackStatusViewModel(provider);

        vm.Start();

        Check.That(vm.IsVisible).IsFalse();
    }

    [Fact]
    public void Start_WithEyePositionCapability_ShowsOverlay()
    {
        var provider = new FakeTrackStatusProvider { Caps = GazeProviderCapabilities.EyePosition | GazeProviderCapabilities.HeadPose };
        using var vm = new TrackStatusViewModel(provider);

        vm.Start();

        Check.That(vm.IsVisible).IsTrue();
    }

    [Fact]
    public void Stop_HidesOverlay()
    {
        var provider = new FakeTrackStatusProvider { Caps = GazeProviderCapabilities.EyePosition };
        using var vm = new TrackStatusViewModel(provider);
        vm.Start();

        vm.Stop();

        Check.That(vm.IsVisible).IsFalse();
    }

    [Fact]
    public void PollLatest_WithSample_UpdatesProperties()
    {
        var provider = new FakeTrackStatusProvider
        {
            Caps = GazeProviderCapabilities.EyePosition | GazeProviderCapabilities.HeadPose,
            Sample = new TrackStatusSample(
                Left: new EyeSample(0.3, 0.4, 0.5, true),
                Right: new EyeSample(0.7, 0.4, 0.5, true),
                HeadDistanceMm: 600, HeadDistanceValid: true,
                UserPresent: true, TimestampMicroseconds: 1000)
        };
        using var vm = new TrackStatusViewModel(provider);

        vm.PollLatest();

        Check.That(vm.LeftEyeX).IsEqualTo(0.3);
        Check.That(vm.RightEyeX).IsEqualTo(0.7);
        Check.That(vm.HeadDistanceMm).IsEqualTo(600.0);
        Check.That(vm.DistanceQuality).IsEqualTo("Good");
        Check.That(vm.UserPresent).IsTrue();
    }

    private sealed class FakeTrackStatusProvider : IGazeProvider
    {
        public GazeProviderCapabilities Caps { get; set; } = GazeProviderCapabilities.None;
        public TrackStatusSample Sample { get; set; }
        public GazeProviderCapabilities Capabilities => Caps;
        public bool IsConnected => true;
        public event Action<GazePoint>? GazePointReceived;
        public event Action<TrackStatusSample>? TrackStatusReceived { add { } remove { } }
        public bool TryGetLatestStatus(out TrackStatusSample sample)
        {
            sample = Sample;
            return Caps != GazeProviderCapabilities.None;
        }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }
}
```

> **H1 note:** `EyeCtrl.ViewModels.Tests` also targets plain `net10.0` (verified). This test uses only BCL + Core + ViewModels types — no `System.Windows.Media.ImageSource` or `DispatcherTimer`. The fake provider implements `IGazeProvider` with the DIM defaults.

- [ ] **Step 3: Run tests**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~TrackStatusSettingsDefaultsTests" && dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~TrackStatusViewModelTests"`
Expected: ALL PASS

- [ ] **Step 4: Full build + test checkpoint**

Run: `dotnet build EyeCtrl.slnx && dotnet test --filter "Category!=UI"`
Expected: BUILD SUCCEEDED + ALL PASS

- [ ] **Step 5: Commit**

```bash
git add tests/EyeCtrl.Core.Tests/Settings/TrackStatusSettingsDefaultsTests.cs tests/EyeCtrl.ViewModels.Tests/TrackStatusViewModelTests.cs
git commit -m "test(track-status): add settings defaults + VM capability-gating tests"
```

---

## Checkpoint C — Task Switcher (Feature 2)

### Task C1: `IWindowManager` interface + `AppWindow` model

**Files:**
- Create: `src/EyeCtrl.Core/Interfaces/IWindowManager.cs`
- Create: `src/EyeCtrl.Core/Models/AppWindow.cs`

- [ ] **Step 1: Write `IWindowManager`**

```csharp
// src/EyeCtrl.Core/Interfaces/IWindowManager.cs
namespace EyeCtrl.Core.Interfaces;

using EyeCtrl.Core.Models;

/// <summary>
/// Enumerates open windows and brings them to the front.
/// Implementations use Win32 EnumWindows + SetForegroundWindow.
/// </summary>
public interface IWindowManager
{
    /// <summary>Enumerates visible top-level non-tool windows with titles.</summary>
    IReadOnlyList<AppWindow> EnumerateOpenWindows();

    /// <summary>Brings the specified window to the foreground.</summary>
    void BringToFront(IntPtr hwnd);
}
```

- [ ] **Step 2: Write `AppWindow` record**

```csharp
// src/EyeCtrl.Core/Models/AppWindow.cs
namespace EyeCtrl.Core.Models;

/// <summary>
/// One open application window, for the Task Switcher overlay.
/// </summary>
/// <param name="Hwnd">Window handle.</param>
/// <param name="Title">Window title (from GetWindowText).</param>
/// <param name="ProcessId">Owning process ID.</param>
/// <param name="ProcessName">Owning process name (for icon lookup).</param>
public sealed record AppWindow(IntPtr Hwnd, string Title, uint ProcessId, string ProcessName);
```

- [ ] **Step 3: Build Core**

Run: `dotnet build src/EyeCtrl.Core/EyeCtrl.Core.csproj`
Expected: BUILD SUCCEEDED

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.Core/Interfaces/IWindowManager.cs src/EyeCtrl.Core/Models/AppWindow.cs
git commit -m "feat(task-switcher): add IWindowManager interface + AppWindow model"
```

---

### Task C2: Win32 P/Invoke declarations for window enumeration

**Files:**
- Modify: `src/EyeCtrl.Platform/Input/NativeMethods.cs`

- [ ] **Step 1: Add window-management P/Invokes to `NativeMethods`**

In `src/EyeCtrl.Platform/Input/NativeMethods.cs`, add the following declarations (inside the existing `internal static partial class NativeMethods`). **Match the existing pattern exactly: `[LibraryImport]` + `internal` access** (verified: `SendInput`, `GetSystemMetrics`, `VkKeyScanW` are all `[LibraryImport]` + `internal`).

> **⚠ `[LibraryImport]` + `StringBuilder` is illegal (SYSLIB1051).** The `[LibraryImport]` source generator does not support `StringBuilder` — use `[Out] char[]` instead. Note: `[return: MarshalAs(UnmanagedType.Bool)]` IS still required under `[LibraryImport]` for Win32 BOOL returns — the generator does not marshal `bool` by default.

```csharp
    // Window enumeration
    [return: MarshalAs(UnmanagedType.Bool)]
    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int GetWindowTextLength(IntPtr hWnd);

    // NOTE: [LibraryImport] does not support StringBuilder (SYSLIB1051). Use [Out] char[].
    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int GetWindowText(IntPtr hWnd, [Out] char[] lpString, int nMaxCount);

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [LibraryImport("user32.dll")]
    internal static partial int GetWindowLong(IntPtr hWnd, int nIndex);

    // Window activation
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AllowSetForegroundWindow(uint dwProcessId);

    // Constants
    internal const int GWL_EXSTYLE = -20;
    internal const uint GW_OWNER = 4;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const uint ASFW_ANY = 0xFFFFFFFF;
```

> **Access modifier:** all existing members are `internal` (verified: `SendInput` at line 8, `GetSystemMetrics` at line 11, constants at lines 16+). The new members match. `Win32WindowManager` is in the same assembly (`EyeCtrl.Platform`), so `internal` access works.

- [ ] **Step 2: Build Platform**

Run: `dotnet build src/EyeCtrl.Platform/EyeCtrl.Platform.csproj`
Expected: BUILD SUCCEEDED

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.Platform/Input/NativeMethods.cs
git commit -m "feat(task-switcher): add Win32 window-enumeration + activation P/Invokes"
```

---

### Task C3: `Win32WindowManager` implementation

**Files:**
- Create: `src/EyeCtrl.Platform/Services/Win32WindowManager.cs`

- [ ] **Step 1: Write `Win32WindowManager`**

```csharp
// src/EyeCtrl.Platform/Services/Win32WindowManager.cs
namespace EyeCtrl.Platform.Services;

using System.Diagnostics;
using System.Runtime.InteropServices;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Platform.Input;

/// <summary>
/// Enumerates open windows via Win32 EnumWindows and brings them to front via
/// SetForegroundWindow. Filters on WS_EX_TOOLWINDOW (unconditional on all EyeCtrl
/// overlays via OverlayWindow.cs:42) to exclude the app's own overlay windows.
/// </summary>
public class Win32WindowManager : IWindowManager
{
    /// <inheritdoc />
    public IReadOnlyList<AppWindow> EnumerateOpenWindows()
    {
        var windows = new List<AppWindow>();
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hWnd))
                return true;

            // Exclude tool windows (all EyeCtrl overlays have WS_EX_TOOLWINDOW)
            var exStyle = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE);
            if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0)
                return true;

            // Exclude child windows (only top-level windows with no owner)
            if (NativeMethods.GetWindow(hWnd, NativeMethods.GW_OWNER) != IntPtr.Zero)
                return true;

            // Get title — use char[] buffer (not StringBuilder; [LibraryImport] doesn't support it)
            var length = NativeMethods.GetWindowTextLength(hWnd);
            if (length == 0)
                return true;
            var buffer = new char[length + 1];
            NativeMethods.GetWindowText(hWnd, buffer, buffer.Length);
            var title = new string(buffer, 0, length);
            if (string.IsNullOrWhiteSpace(title))
                return true;

            // Get process
            NativeMethods.GetWindowThreadProcessId(hWnd, out var processId);
            string processName = "";
            try
            {
                using var proc = Process.GetProcessById((int)processId);
                processName = proc.ProcessName;
            }
            catch
            {
                // Process may have exited or be inaccessible
            }

            windows.Add(new AppWindow(hWnd, title, processId, processName));
            return true;
        }, IntPtr.Zero);

        return windows;
    }

    /// <inheritdoc />
    public void BringToFront(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        // AllowSetForegroundWindow(ASFW_ANY) permits focus steal from other processes
        NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
        NativeMethods.SetForegroundWindow(hwnd);
    }
}
```

- [ ] **Step 2: Build Platform**

Run: `dotnet build src/EyeCtrl.Platform/EyeCtrl.Platform.csproj`
Expected: BUILD SUCCEEDED

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.Platform/Services/Win32WindowManager.cs
git commit -m "feat(task-switcher): add Win32WindowManager with EnumWindows + SetForegroundWindow"
```

---

### Task C4: `IAppIconProvider` + `Win32AppIconProvider` (both in `EyeCtrl.App`)

**Files:**
- Create: `src/EyeCtrl.App/Services/IAppIconProvider.cs`
- Create: `src/EyeCtrl.App/Services/Win32AppIconProvider.cs`

> **H1 + M3 fix:** `ImageSource` (WPF) cannot live in Core (targets `net10.0`) or ViewModels (also `net10.0`, zero WPF refs — verified). Putting the interface in ViewModels and the impl in Platform would create a forbidden `Platform → ViewModels` dependency edge (the allowed directions are `ViewModels → Core` and `Platform → Core`). **Both files go in `EyeCtrl.App`** (targets `net10.0-windows`, has WPF + WinForms, already references both Platform and ViewModels). This is the composition root — the right home for a WPF-typed concern that crosses Platform/ViewModels.

- [ ] **Step 1: Write `IAppIconProvider`**

```csharp
// src/EyeCtrl.App/Services/IAppIconProvider.cs
namespace EyeCtrl.App.Services;

using System.Windows.Media;

/// <summary>
/// Resolves an icon for a process, for Task Switcher tiles.
/// Returns null if no icon is available (e.g. elevated/inaccessible process).
/// </summary>
public interface IAppIconProvider
{
    ImageSource? GetIcon(uint processId);
}
```

- [ ] **Step 2: Write `Win32AppIconProvider`**

```csharp
// src/EyeCtrl.App/Services/Win32AppIconProvider.cs
namespace EyeCtrl.App.Services;

using System.Diagnostics;
using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

/// <summary>
/// Resolves a process icon via Icon.ExtractAssociatedIcon.
/// Wraps in try/catch — Process.MainModule throws Win32Exception (Access Denied)
/// for elevated or other-user processes; falls back to null.
/// </summary>
public class Win32AppIconProvider : IAppIconProvider
{
    /// <inheritdoc />
    public ImageSource? GetIcon(uint processId)
    {
        try
        {
            using var proc = Process.GetProcessById((int)processId);
            var path = proc.MainModule?.FileName;
            if (string.IsNullOrEmpty(path))
                return null;

            using var icon = Icon.ExtractAssociatedIcon(path);
            if (icon == null)
                return null;

            return Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
        }
        catch
        {
            // Win32Exception (Access Denied) for elevated/other-user processes,
            // or the process has exited. Fall back to null (no icon).
            return null;
        }
    }
}
```

- [ ] **Step 3: Build App**

Run: `dotnet build src/EyeCtrl.App/EyeCtrl.App.csproj`
Expected: BUILD SUCCEEDED (App has WPF + WinForms + references Core for `AppWindow`)

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.App/Services/IAppIconProvider.cs src/EyeCtrl.App/Services/Win32AppIconProvider.cs
git commit -m "feat(task-switcher): add IAppIconProvider + Win32AppIconProvider in App (WPF-typed, no new deps)"
```

---

### Task C5: Task Switcher messages

**Files:**
- Modify: `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs`

- [ ] **Step 1: Add the two messages**

In `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs`, add:

```csharp
/// <summary>Sent by the toolbar Task Switcher button to toggle the overlay.</summary>
public record TaskSwitcherToggledMessage;

/// <summary>Sent by a Task Switcher tile when the user selects a window.</summary>
/// <param name="Hwnd">Handle of the window to bring to front.</param>
public record TaskSwitcherSelectionMessage(IntPtr Hwnd);
```

- [ ] **Step 2: Build ViewModels**

Run: `dotnet build src/EyeCtrl.ViewModels/EyeCtrl.ViewModels.csproj`
Expected: BUILD SUCCEEDED

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs
git commit -m "feat(task-switcher): add TaskSwitcherToggledMessage + TaskSwitcherSelectionMessage"
```

---

### Task C6: `TaskSwitcherTileViewModel` + `TaskSwitcherViewModel`

**Files:**
- Create: `src/EyeCtrl.ViewModels/TaskSwitcherTileViewModel.cs`
- Create: `src/EyeCtrl.ViewModels/TaskSwitcherViewModel.cs`

- [ ] **Step 1: Write `TaskSwitcherTileViewModel` (no WPF types — H1 fix)**

> **H1 fix:** `ImageSource` is WPF-only; ViewModels targets `net10.0`. Store `ProcessId` instead — the View (`TaskSwitcherWindow`, in App) resolves the icon via `IAppIconProvider` using this ID. This keeps the tile VM WPF-free.

```csharp
// src/EyeCtrl.ViewModels/TaskSwitcherTileViewModel.cs
namespace EyeCtrl.ViewModels;

using EyeCtrl.Core.Models;

public partial class TaskSwitcherTileViewModel : ObservableObject
{
    public IntPtr Hwnd { get; }
    public string Title { get; }
    public uint ProcessId { get; }
    public string ProcessName { get; }

    /// <summary>
    /// Process icon as a boxed object. Set by the View (TaskSwitcherWindow) via
    /// IAppIconProvider after the tile is created. Typed as `object?` (not `ImageSource`)
    /// so this VM stays WPF-free (ViewModels targets plain net10.0). WPF's Image.Source
    /// binding accepts any object that is an ImageSource at runtime.
    /// </summary>
    [ObservableProperty] private object? _icon;

    [ObservableProperty] private PhysicalPoint _position;
    [ObservableProperty] private double _dwellProgress;

    public TaskSwitcherTileViewModel(IntPtr hwnd, string title, uint processId, string processName, PhysicalPoint position)
    {
        Hwnd = hwnd;
        Title = title;
        ProcessId = processId;
        ProcessName = processName;
        _position = position;
    }
}
```

- [ ] **Step 2: Write `TaskSwitcherViewModel` (fixes H2 `_settings` field + drops `IAppIconProvider` dependency)**

> **H2 fix:** the `_settings` field is now declared and assigned. **H1/M3 fix:** the VM no longer depends on `IAppIconProvider` (which lives in App) — the View resolves icons. The VM stores only `ProcessId` on each tile.

```csharp
// src/EyeCtrl.ViewModels/TaskSwitcherViewModel.cs
namespace EyeCtrl.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class TaskSwitcherViewModel : DwellOverlayViewModelBase,
    IRecipient<TaskSwitcherToggledMessage>
{
    private readonly IWindowManager _windowManager;
    private readonly IMessenger _messenger;
    private readonly EyeCtrlSettings _settings;  // H2 fix: declared + assigned

    public ObservableCollection<TaskSwitcherTileViewModel> Tiles { get; } = new();

    public TaskSwitcherViewModel(
        IWindowManager windowManager,
        EyeCtrlSettings settings,
        IMessenger messenger)
        : base(messenger, settings.Dwell.DwellCooldownMs)
    {
        _windowManager = windowManager;
        _messenger = messenger;
        _settings = settings;  // H2 fix: assigned
    }

    public void Receive(TaskSwitcherToggledMessage message)
    {
        if (IsVisible)
            HideOverlay();
        else
            ShowOverlay();
    }

    private void ShowOverlay()
    {
        // Populate tiles from the window manager
        Tiles.Clear();
        DwellDetector.ClearTargets();

        var windows = _windowManager.EnumerateOpenWindows();
        // M2 note: reuses ToolbarButtonDwellMs (700ms default). Task-switcher tiles are larger
        // (200×120) than toolbar buttons (~50px), so 700ms feels easier to hit — which is
        // intentional for a fullscreen picker. If user testing finds it too sensitive,
        // add a dedicated TaskSwitcherTileDwellMs field to DwellSettings (range 200–2000, default ~900).
        var dwellMs = _settings.Dwell.ToolbarButtonDwellMs;

        for (int i = 0; i < windows.Count; i++)
        {
            var window = windows[i];
            // Position will be set by the View after layout; use placeholder for now.
            // H3 note: all tiles start at (0,0) — the top-left corner. IsVisible is set to
            // true below, which enables DwellDetector.Update via GazeCursorMovedMessage.
            // There's a brief window (until UpdateTilePositions runs at DispatcherPriority.Loaded)
            // H3 fix: positions are (0,0) placeholder until the View calls UpdateTilePositions
            // after layout. IsVisible is NOT set here — it's set by ActivateAfterLayout() once
            // the View has positioned all tiles, so the DwellDetector never processes targets
            // at the overlapping (0,0) origin.
            var position = new PhysicalPoint(0, 0);
            var tile = new TaskSwitcherTileViewModel(window.Hwnd, window.Title, window.ProcessId, window.ProcessName, position);
            Tiles.Add(tile);

            DwellDetector.AddTarget(new DwellTarget(
                window.Hwnd.ToString(),
                position,
                DpiHelper.LogicalToPhysicalX(UIConstants.ButtonRadius),
                dwellMs));
        }

        // IsVisible is set by ActivateAfterLayout() — NOT here — to avoid the (0,0) race.
    }

    /// <summary>
    /// Called by the View after <c>UpdateTilePositions</c> has set real screen positions
    /// on all dwell targets. Activates the overlay (sets IsVisible) so dwell processing
    /// begins only when targets are correctly positioned, not at the (0,0) placeholder.
    /// </summary>
    public void ActivateAfterLayout()
    {
        IsVisible = true;
    }

    private void HideOverlay()
    {
        IsVisible = false;
        DwellDetector.ClearTargets();
        Tiles.Clear();
    }

    protected override void OnTargetActivated(string targetId)
    {
        // targetId is the hwnd string
        if (IntPtr.TryParse(targetId, out var hwnd))
        {
            _messenger.Send(new TaskSwitcherSelectionMessage(hwnd));
        }
        HideOverlay();
    }

    protected override void OnTargetProgress(string targetId, double progress)
    {
        var tile = Tiles.FirstOrDefault(t => t.Hwnd.ToString() == targetId);
        if (tile != null)
            tile.DwellProgress = progress;
    }

    /// <summary>Called by the View after layout to set tile screen positions for dwell hit-testing.</summary>
    public void UpdateTilePosition(IntPtr hwnd, PhysicalPoint screenCenter)
    {
        DwellDetector.UpdateTargetCenter(hwnd.ToString(), screenCenter);
    }
}
```

- [ ] **Step 3: Build ViewModels (must compile on plain net10.0 — no WPF types)**

Run: `dotnet build src/EyeCtrl.ViewModels/EyeCtrl.ViewModels.csproj`
Expected: BUILD SUCCEEDED. Verify no `System.Windows.*` usings leaked in — the tile VM stores `uint ProcessId` (not `ImageSource`), and the VM constructor takes `(IWindowManager, EyeCtrlSettings, IMessenger)` (no `IAppIconProvider`).

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.ViewModels/TaskSwitcherTileViewModel.cs src/EyeCtrl.ViewModels/TaskSwitcherViewModel.cs
git commit -m "feat(task-switcher): add TaskSwitcherViewModel + TileViewModel"
```

---

### Task C7: Add Task Switcher button to `ToolbarViewModel`

**Files:**
- Modify: `src/EyeCtrl.ViewModels/ToolbarViewModel.cs` (button array line 38 + switch line 98)
- Modify: `tests/EyeCtrl.ViewModels.Tests/ToolbarViewModelTests.cs` (line 14 + line 17 — update button count)

> **Run impact analysis on `ToolbarViewModel` before editing.**

- [ ] **Step 1: Run impact analysis on `ToolbarViewModel`**

Use GitNexus `impact({target: "ToolbarViewModel", direction: "upstream"})`. Report blast radius. Proceed if LOW/MEDIUM.

- [ ] **Step 2: Add the button to the array**

In `src/EyeCtrl.ViewModels/ToolbarViewModel.cs`, line 38–45, add a 6th entry to the `buttons` array:

```csharp
        var buttons = new (string Id, string Symbol)[]
        {
            ("Pause", "\u23F8"),
            ("SelectionMode", "\u2295"),
            ("Keyboard", "\u2328"),
            ("Calibrate", "\u2316"),
            ("Settings", "\u2699"),
            ("TaskSwitcher", "\uD83E\uDE9F"),  // 🪟 (surrogate pair, same as PinNote's non-BMP pattern)
        };
```

- [ ] **Step 3: Add the dispatch case**

In `OnTargetActivated` (line 98–108), add:

```csharp
            case "TaskSwitcher": Messenger.Send(new TaskSwitcherToggledMessage()); break;
```

Add the necessary `using EyeCtrl.ViewModels.Messages;` (likely already present).

- [ ] **Step 4: Update the toolbar button-count test**

`tests/EyeCtrl.ViewModels.Tests/ToolbarViewModelTests.cs` line 14–17 asserts `Buttons.Count == 5` in a method named `InitializesWith4Buttons` (a pre-existing naming inconsistency — it already asserts 5, not 4). Update both the method name and the assertion to reflect the new 6th button:

```csharp
    [Fact]
    public void InitializesWith6Buttons()
    {
        var vm = new ToolbarViewModel(_settings, _messenger);
        Check.That(vm.Buttons.Count).IsEqualTo(6);
    }
```

- [ ] **Step 5: Build + run toolbar tests**

Run: `dotnet build src/EyeCtrl.ViewModels/EyeCtrl.ViewModels.csproj && dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~Toolbar"`
Expected: BUILD SUCCEEDED + ALL PASS (including the updated count test)

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.ViewModels/ToolbarViewModel.cs tests/EyeCtrl.ViewModels.Tests/ToolbarViewModelTests.cs
git commit -m "feat(task-switcher): add Task Switcher button to toolbar + update button-count test"
```

---

### Task C8: `TaskSwitcherWindow` overlay (XAML + code-behind)

**Files:**
- Create: `src/EyeCtrl.App/Overlays/TaskSwitcherWindow.xaml`
- Create: `src/EyeCtrl.App/Overlays/TaskSwitcherWindow.xaml.cs`

- [ ] **Step 1: Write the XAML (fullscreen, NOT click-through, topmost, no-activate)**

```xml
<!-- src/EyeCtrl.App/Overlays/TaskSwitcherWindow.xaml -->
<overlays:OverlayWindow x:Class="EyeCtrl.App.Overlays.TaskSwitcherWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:overlays="clr-namespace:EyeCtrl.App.Overlays"
        WindowStyle="None" AllowsTransparency="True" Background="#E6000000"
        Topmost="True" ShowInTaskbar="False" ResizeMode="NoResize"
        IsClickThrough="False">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>
        <ScrollViewer Grid.Row="0" VerticalScrollBarVisibility="Auto" HorizontalAlignment="Center">
            <ItemsControl x:Name="TilesPanel" ItemsSource="{Binding Tiles}">
                <ItemsControl.ItemsPanel>
                    <ItemsPanelTemplate>
                        <WrapPanel ItemWidth="200" ItemHeight="120"/>
                    </ItemsPanelTemplate>
                </ItemsControl.ItemsPanel>
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <Border Margin="8" Background="#40FFFFFF" CornerRadius="8" Padding="8">
                            <StackPanel HorizontalAlignment="Center" VerticalAlignment="Center">
                                <Image Source="{Binding Icon}" Width="32" Height="32" Margin="0,0,0,4"/>
                                <TextBlock Text="{Binding Title}" Foreground="White" FontSize="12"
                                           TextWrapping="NoWrap" TextTrimming="CharacterEllipsis"
                                           MaxWidth="180"/>
                            </StackPanel>
                        </Border>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
        </ScrollViewer>
        <TextBlock Grid.Row="1" Text="Select a window — or look away to cancel"
                   Foreground="LightGray" FontSize="14" HorizontalAlignment="Center" Margin="0,12"/>
    </Grid>
</overlays:OverlayWindow>
```

- [ ] **Step 2: Write the code-behind (resolves icons via `IAppIconProvider` — H1 fix)**

```csharp
// src/EyeCtrl.App/Overlays/TaskSwitcherWindow.xaml.cs
namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.App.Services;
using EyeCtrl.ViewModels;

public partial class TaskSwitcherWindow : OverlayWindow
{
    private TaskSwitcherViewModel? _viewModel;
    private IAppIconProvider? _iconProvider;

    public TaskSwitcherWindow()
    {
        InitializeComponent();
    }

    public void Configure(TaskSwitcherViewModel viewModel, IAppIconProvider iconProvider)
    {
        _viewModel = viewModel;
        _iconProvider = iconProvider;
        DataContext = viewModel;
    }

    public void ShowFullScreen()
    {
        // Resolve icons for each tile (View-side concern; the tile VM stores only ProcessId)
        if (_iconProvider != null)
        {
            foreach (var tile in _viewModel?.Tiles ?? Enumerable.Empty<TaskSwitcherTileViewModel>())
                tile.Icon = _iconProvider.GetIcon(tile.ProcessId);
        }

        // Cover the primary screen
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        Left = 0;
        Top = 0;
        Show();

        // After layout, update tile positions for dwell hit-testing, THEN activate the VM
        // (H3 fix: IsVisible is set only after positions are real, not at the (0,0) placeholder).
        Dispatcher.BeginInvoke(() =>
        {
            UpdateTilePositions();
            _viewModel?.ActivateAfterLayout();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void UpdateTilePositions()
    {
        if (_viewModel == null) return;

        // For each tile, compute its screen center and tell the VM
        foreach (var item in TilesPanel.Items)
        {
            if (item is TaskSwitcherTileViewModel tile)
            {
                var container = TilesPanel.ItemContainerGenerator.ContainerFromItem(tile);
                if (container is FrameworkElement fe)
                {
                    var transform = fe.TransformToAncestor(this) as GeneralTransform;
                    var point = fe.TransformToAncestor(this).Transform(new Point(fe.ActualWidth / 2, fe.ActualHeight / 2));
                    var physical = new Core.Models.PhysicalPoint(
                        DpiHelper.LogicalToPhysicalX(point.X),
                        DpiHelper.LogicalToPhysicalY(point.Y));
                    _viewModel.UpdateTilePosition(tile.Hwnd, physical);
                }
            }
        }
    }

    public void HideOverlay()
    {
        Hide();
    }
}
```

- [ ] **Step 3: Build App**

Run: `dotnet build src/EyeCtrl.App/EyeCtrl.App.csproj`
Expected: BUILD SUCCEEDED

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.App/Overlays/TaskSwitcherWindow.xaml src/EyeCtrl.App/Overlays/TaskSwitcherWindow.xaml.cs
git commit -m "feat(task-switcher): add fullscreen TaskSwitcherWindow overlay"
```

---

### Task C9: Wire Task Switcher into DI + `OverlayManager` + `EventCoordinationContext`

**Files:**
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs` (register `IWindowManager`, `IAppIconProvider`, `TaskSwitcherViewModel`)
- Modify: `src/EyeCtrl.App/Services/IOverlayManager.cs` (add `TaskSwitcherWindow` property + `TaskSwitcherViewModel` to `OverlayViewModels`)
- Modify: `src/EyeCtrl.App/Services/OverlayManager.cs` (instantiate)
- Modify: `src/EyeCtrl.App/Services/IEventCoordinator.cs` (add `TaskSwitcherVM` + `IWindowManager` to `EventCoordinationContext`)
- Modify: `src/EyeCtrl.App/App.xaml.cs` (construction sites)

> **Run impact analysis on `EventCoordinationContext` and `OverlayViewModels` before editing.**

- [ ] **Step 1: Register services + VM in `AppBootstrapper`**

In `src/EyeCtrl.App/Services/AppBootstrapper.cs`:

After the `IStartupRegistrar` registration (from Task A3), add:
```csharp
        // Window management
        services.AddSingleton<IWindowManager, Win32WindowManager>();
        services.AddSingleton<IAppIconProvider, Win32AppIconProvider>();
```

After `services.AddSingleton<TrackStatusViewModel>();` (from Task B7), add:
```csharp
        services.AddSingleton<TaskSwitcherViewModel>();
```

In `ResolveViewModels`, add `TaskSwitcher: sp.GetRequiredService<TaskSwitcherViewModel>()` as the new last field (move the `)` from `TrackStatus`).

- [ ] **Step 2: Add `TaskSwitcherWindow` to `IOverlayManager` + `OverlayViewModels`**

In `src/EyeCtrl.App/Services/IOverlayManager.cs`, add:
```csharp
    TaskSwitcherWindow TaskSwitcher { get; }
```
And add `TaskSwitcherViewModel TaskSwitcher` to the `OverlayViewModels` record (after `TrackStatusViewModel TrackStatus`).

- [ ] **Step 3: Constructor-inject `IAppIconProvider` into `OverlayManager` + instantiate `TaskSwitcher` (avoids `Initialize` signature churn)**

> **H2 + #4 fix:** `OverlayManager` is DI-constructed via a factory at `AppBootstrapper.cs:184` (`new OverlayManager(dispatcher, logger)`). Constructor-injecting `IAppIconProvider` avoids changing the `IOverlayManager.Initialize` interface signature (line 77) and the `InitializeOverlays` wrapper in `App.xaml.cs` — no call-site churn. This is cleaner than adding a 4th `Initialize` parameter.

In `src/EyeCtrl.App/Services/OverlayManager.cs`, add `IAppIconProvider` to the constructor + a field:
```csharp
    private readonly IAppIconProvider _iconProvider;

    public OverlayManager(Dispatcher dispatcher, ILogger<OverlayManager> logger, IAppIconProvider iconProvider)
    {
        // ... existing constructor body ...
        _iconProvider = iconProvider;
    }

    public TaskSwitcherWindow TaskSwitcher { get; private set; } = null!;
```

In `Initialize` (no signature change — still `Initialize(OverlayViewModels, EyeCtrlSettings, GazeProviderType)`):
```csharp
        TaskSwitcher = new TaskSwitcherWindow();
        TaskSwitcher.Configure(viewModels.TaskSwitcher, _iconProvider);
```

In `src/EyeCtrl.App/Services/AppBootstrapper.cs`, update the `OverlayManager` factory registration (line 184) to pass the `IAppIconProvider`:
```csharp
        services.AddSingleton<IOverlayManager>(sp => new OverlayManager(
            sp.GetRequiredService<Dispatcher>(),
            sp.GetRequiredService<ILogger<OverlayManager>>(),
            sp.GetRequiredService<IAppIconProvider>()));
```

> **No interface change:** `IOverlayManager.Initialize` stays `(OverlayViewModels, EyeCtrlSettings, GazeProviderType)`. No `InitializeOverlays` wrapper change needed. The `IAppIconProvider` registration (from Task C4 step 1) must precede the `OverlayManager` registration in `BuildServiceProvider` — verify the ordering.

- [ ] **Step 4: Add `TaskSwitcherVM` + `IWindowManager` to `EventCoordinationContext`**

In `src/EyeCtrl.App/Services/IEventCoordinator.cs`, after `TrackStatusViewModel TrackStatusVM` (added in Task B7), add:
```csharp
    TrackStatusViewModel TrackStatusVM,
    TaskSwitcherViewModel TaskSwitcherVM,
    IWindowManager WindowManager);
```

- [ ] **Step 5: Update `App.xaml.cs` construction sites**

Update the `EventCoordinationContext` construction to add `taskSwitcherVm` + `_serviceProvider.GetRequiredService<IWindowManager>()`.

Update `ResolveViewModels` deconstruct + `OverlayViewModels` construction to include `taskSwitcherVm`.

- [ ] **Step 6: Build the full solution**

Run: `dotnet build EyeCtrl.slnx`
Expected: BUILD SUCCEEDED

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.App/Services/AppBootstrapper.cs src/EyeCtrl.App/Services/IOverlayManager.cs src/EyeCtrl.App/Services/OverlayManager.cs src/EyeCtrl.App/Services/IEventCoordinator.cs src/EyeCtrl.App/App.xaml.cs
git commit -m "feat(task-switcher): wire IWindowManager + TaskSwitcherVM into DI + EventCoordinationContext"
```

---

### Task C10: Handle Task Switcher show/hide + selection in `EventCoordinator`

**Files:**
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs`

> **Design:** the VM already receives `TaskSwitcherToggledMessage` via `IRecipient<TaskSwitcherToggledMessage>` (auto-registered by `ObservableRecipient.IsActive = true` in the base class — `MessengerViewModelBase`). The VM toggles its own `IsVisible` and populates/clears tiles. The coordinator does NOT handle `TaskSwitcherToggledMessage` — it only watches the VM's `IsVisible` PropertyChanged to show/hide the window, and handles `TaskSwitcherSelectionMessage` to bring the chosen window to front. `ShowOverlay`/`HideOverlay` stay private on the VM.

- [ ] **Step 1: Subscribe to VM `IsVisible` + register selection handler in `EventCoordinator.Initialize`**

In the `Initialize` method (where other message handlers and PropertyChanged subscriptions are registered), add:

```csharp
            // Task Switcher: watch VM IsVisible to show/hide the fullscreen overlay.
            // The VM handles TaskSwitcherToggledMessage itself (via IRecipient + ObservableRecipient auto-register).
            _context.TaskSwitcherVM.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(_context.TaskSwitcherVM.IsVisible))
                {
                    _context.Dispatcher.BeginInvoke(() =>
                    {
                        if (_context.TaskSwitcherVM.IsVisible)
                            _context.OverlayManager.TaskSwitcher.ShowFullScreen();
                        else
                            _context.OverlayManager.TaskSwitcher.HideOverlay();
                    });
                }
            };

            // Task Switcher: handle window selection (bring to front + hide overlay)
            _context.Messenger.Register<EventCoordinator, TaskSwitcherSelectionMessage>(this, (_, msg) =>
                _context.Dispatcher.BeginInvoke(() => HandleTaskSwitcherSelection(msg)));
```

- [ ] **Step 2: Add the selection handler method**

```csharp
    private void HandleTaskSwitcherSelection(TaskSwitcherSelectionMessage msg)
    {
        if (_context == null) return;
        _context.WindowManager.BringToFront(msg.Hwnd);
        _context.TaskSwitcherVM.HideOverlay();  // HideOverlay is public (called from coordinator)
        _context.OverlayManager.TaskSwitcher.HideOverlay();
    }
```

> **Note:** `TaskSwitcherViewModel.HideOverlay()` must be `public` (not `private`) so the coordinator can call it after a selection. `ShowOverlay()` stays `private` — it's only called by the VM's own `Receive(TaskSwitcherToggledMessage)`. If `HideOverlay` is currently private in the C6 code, change it to `public`.

- [ ] **Step 3: Build + test**

Run: `dotnet build EyeCtrl.slnx && dotnet test --filter "Category!=UI"`
Expected: BUILD SUCCEEDED + ALL PASS

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.App/Services/EventCoordinator.cs
git commit -m "feat(task-switcher): handle toggle + selection in EventCoordinator"
```

---

### Task C11: Task Switcher tests + full checkpoint

**Files:**
- Create: `tests/EyeCtrl.ViewModels.Tests/TaskSwitcherViewModelTests.cs`

- [ ] **Step 1: Write the VM test with a fake window manager (no WPF types — H1 fix)**

> **H1 fix:** `EyeCtrl.ViewModels.Tests` targets plain `net10.0`. The test uses no `System.Windows.Media` types. The VM constructor takes `(IWindowManager, EyeCtrlSettings, IMessenger)` — no `IAppIconProvider` (that's resolved by the View, not the VM).

```csharp
// tests/EyeCtrl.ViewModels.Tests/TaskSwitcherViewModelTests.cs
namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class TaskSwitcherViewModelTests
{
    [Fact]
    public void Receive_Toggled_PopulatesTilesFromWindowManager()
    {
        var messenger = new WeakReferenceMessenger();
        var wm = new FakeWindowManager(new[]
        {
            new AppWindow(new IntPtr(1), "Notepad", 100, "notepad"),
            new AppWindow(new IntPtr(2), "Calculator", 200, "calc")
        });
        var settings = new EyeCtrlSettings();
        using var vm = new TaskSwitcherViewModel(wm, settings, messenger);

        messenger.Send(new TaskSwitcherToggledMessage());

        Check.That(vm.Tiles).HasSize(2);
        Check.That(vm.IsVisible).IsTrue();
        Check.That(vm.Tiles[0].Title).IsEqualTo("Notepad");
        Check.That(vm.Tiles[1].Title).IsEqualTo("Calculator");
    }

    [Fact]
    public void Receive_ToggledTwice_HidesOnSecondToggle()
    {
        var messenger = new WeakReferenceMessenger();
        var wm = new FakeWindowManager(Array.Empty<AppWindow>());
        var settings = new EyeCtrlSettings();
        using var vm = new TaskSwitcherViewModel(wm, settings, messenger);

        messenger.Send(new TaskSwitcherToggledMessage());
        Check.That(vm.IsVisible).IsTrue();

        messenger.Send(new TaskSwitcherToggledMessage());
        Check.That(vm.IsVisible).IsFalse();
        Check.That(vm.Tiles).IsEmpty();
    }

    private sealed class FakeWindowManager : IWindowManager
    {
        private readonly IReadOnlyList<AppWindow> _windows;
        public FakeWindowManager(IReadOnlyList<AppWindow> windows) => _windows = windows;
        public IReadOnlyList<AppWindow> EnumerateOpenWindows() => _windows;
        public void BringToFront(IntPtr hwnd) { }
    }
}
```

- [ ] **Step 2: Run tests**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~TaskSwitcherViewModelTests"`
Expected: ALL PASS

- [ ] **Step 3: Full build + test checkpoint**

Run: `dotnet build EyeCtrl.slnx && dotnet test --filter "Category!=UI"`
Expected: BUILD SUCCEEDED + ALL PASS

- [ ] **Step 4: Run `detect_changes` for regression review**

Use GitNexus `detect_changes({scope: "compare", base_ref: "main"})` to verify changes only affect expected symbols and execution flows.

- [ ] **Step 5: Commit**

```bash
git add tests/EyeCtrl.ViewModels.Tests/TaskSwitcherViewModelTests.cs
git commit -m "test(task-switcher): add TaskSwitcherViewModel tile-population + toggle tests"
```

---

## Self-Review

### Spec coverage

| Spec section | Covered by |
|---|---|
| Feature 1: Auto-launch (registry Run key) | Tasks A1–A5 |
| Feature 1: `IStartupRegistrar` seam | Task A1 |
| Feature 1: `StartupCommandValue` pure helper | Task A1 |
| Feature 1: `WindowsStartupRegistrar` impl | Task A2 |
| Feature 1: Reconcile on startup + live toggle | Task A3 |
| Feature 1: `Environment.ProcessPath` (not `Assembly.Location`) | Task A2 (constructor) |
| Feature 1: No package reference needed | Task A2 step 2 (build verifies) |
| Feature 2: `IWindowManager` (EnumerateOpenWindows + BringToFront) | Tasks C1, C3 |
| Feature 2: `WS_EX_TOOLWINDOW` filter (not `WS_EX_TRANSPARENT`) | Task C3 (filter logic) |
| Feature 2: `AllowSetForegroundWindow(ASFW_ANY)` in Platform layer | Task C3 (inside `BringToFront`) |
| Feature 2: `IAppIconProvider` with elevated-process try/catch | Task C4 |
| Feature 2: `TaskSwitcherViewModel` (DwellOverlayViewModelBase, tiles) | Task C6 |
| Feature 2: Reuse `ToolbarButtonDwellMs` (700ms) | Task C6 (ShowOverlay) |
| Feature 2: Toolbar button `("TaskSwitcher", "🪟")` | Task C7 |
| Feature 2: Fullscreen overlay, NOT click-through, subclasses `OverlayWindow` | Task C8 |
| Feature 2: EventCoordinationContext gains `TaskSwitcherVM` + `IWindowManager` | Task C9 |
| Feature 2: Handle toggle + selection | Task C10 |
| Feature 3: `GazeProviderCapabilities` flags enum | Task B1 |
| Feature 3: `TrackStatusSample` + `EyeSample` (IsValid = all coords valid) | Task B1 |
| Feature 3: `IGazeProvider` DIMs (Capabilities + TrackStatusReceived + TryGetLatestStatus) | Tasks B2, B5 |
| Feature 3: 8 test/benchmark fakes compile unchanged | Task B2 step 3 (build verifies) |
| Feature 3: Tobii streams (eye_position, head_pose, user_presence) | Task B4 |
| Feature 3: Cache in TobiiStreamEngineProvider under lock | Task B4 |
| Feature 3: `TrackStatusSettings` + EnumerateSubSettings | Task B3 |
| Feature 3: `TrackStatusWindow` owns DispatcherTimer (VM is WPF-free) | Tasks B5 (VM), B6 (window) |
| Feature 3: `TrackStatusWindow` click-through overlay | Task B6 |
| Feature 3: Gate on `settings.TrackStatus.Enabled` | Task B8 |
| Feature 3: Settings UI toggle | Task B8 |
| Feature 3: VM polls `TryGetLatestStatus` | Task B5 |
| Feature 3: Distance thresholds (TBD at impl time) | Task B5 (ComputeDistanceQuality, 450–750mm) |
| Sequencing: B adds only TrackStatusVM; C adds TaskSwitcherVM + IWindowManager | Tasks B7 (B) vs C9 (C) |
| All VMs registered in DI | Tasks A3, B7, C9 |

### Placeholder scan

- The Tobii struct field names (`eyePos.left.x`, `headPose.position.z`, `presence.is_user_present`) are flagged as impl-time-verify — this is acknowledged in the spec as the highest-uncertainty item. The code blocks show the intended structure; the implementer verifies exact field names.
- The `IAppIconProvider` project-location decision is flagged with two alternatives (ViewModels vs App). The implementer picks based on dependency graph.
- No "TBD", "TODO", "implement later", or "similar to Task N" placeholders.

### Type consistency

- `IStartupRegistrar.SetStartOnBoot(bool)` / `IsRegistered` — consistent across A1 (interface), A2 (impl), A3 (usage).
- `StartupCommandValue.BuildCommandValue(string)` — consistent across A1 test + A2 impl.
- `IGazeProvider.Capabilities` / `TrackStatusReceived` / `TryGetLatestStatus` — consistent across B2 (DIM), B4 (Tobii override), B5 (VM consumer), B9 (fake).
- `TrackStatusSample` / `EyeSample` — consistent across B1 (def), B4 (Tobii fill), B5 (VM consume), B9 (fake).
- `IWindowManager.EnumerateOpenWindows()` / `BringToFront(IntPtr)` — consistent across C1 (interface), C3 (impl), C6 (VM consumer), C11 (fake).
- `AppWindow(IntPtr, string, uint, string)` — consistent across C1 (def), C3 (fill), C6 (VM consume), C11 (fake).
- `TaskSwitcherToggledMessage` / `TaskSwitcherSelectionMessage(IntPtr)` — consistent across C5 (def), C6 (VM sends selection), C7 (toolbar sends toggle), C10 (coordinator handles selection), C11 (test sends toggle).
- `EventCoordinationContext` — B7 adds `TrackStatusVM` (21st field); C9 adds `TaskSwitcherVM` + `IWindowManager` (22nd + 23rd). Construction site updated in both.
