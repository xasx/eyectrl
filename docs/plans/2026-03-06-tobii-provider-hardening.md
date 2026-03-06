# Tobii Provider Hardening & Stream Engine Alternative — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Harden the Tobii gaze provider with reconnect/error handling, add a Stream Engine alternative, and let users configure which provider to use.

**Architecture:** `GazeProviderFactory` selects between `TobiiInteractionLibProvider`, `TobiiStreamEngineProvider`, and `MouseGazeProvider` based on a `GazeProviderType` setting. Both Tobii providers reconnect indefinitely on disconnect. The toolbar indicator shows which engine is active.

**Tech Stack:** C# / .NET 10 / WPF, Tobii Interaction Library, Tobii Stream Engine

---

### Task 1: Create GazeProviderType enum

**Files:**
- Create: `src/EyeCtrl.Core/Enums/GazeProviderType.cs`

**Step 1: Create the enum file**

```csharp
namespace EyeCtrl.Core.Enums;

public enum GazeProviderType
{
    Auto,
    InteractionLib,
    StreamEngine,
    Mouse
}
```

**Step 2: Build to verify**

Run: `dotnet build src/EyeCtrl.Core/EyeCtrl.Core.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/EyeCtrl.Core/Enums/GazeProviderType.cs
git commit -m "feat: add GazeProviderType enum"
```

---

### Task 2: Add GazeProviderType to GeneralSettings

**Files:**
- Modify: `src/EyeCtrl.Core/Settings/GeneralSettings.cs`

**Step 1: Add the property**

Add `using EyeCtrl.Core.Enums;` and a new observable property:

```csharp
[ObservableProperty] private GazeProviderType _gazeProviderType = GazeProviderType.Auto;
```

**Step 2: Build to verify**

Run: `dotnet build src/EyeCtrl.Core/EyeCtrl.Core.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/EyeCtrl.Core/Settings/GeneralSettings.cs
git commit -m "feat: add GazeProviderType to GeneralSettings"
```

---

### Task 3: Harden TobiiInteractionLibProvider

**Files:**
- Modify: `src/EyeCtrl.Platform/Gaze/TobiiInteractionLibProvider.cs`

**Step 1: Add reconnect + error dedup + clock drift fix**

Replace `PollLoop` with reconnect logic:
- On exception: dispose `_interactionLib`, set `IsConnected = false`
- Back-off: start at 500ms, double each time, cap at 5000ms
- Recreate `_interactionLib`, re-configure display area, re-subscribe event
- On success: reset back-off, set `IsConnected = true`
- Track `_lastExceptionMessage` — only log when it changes

Replace `OnGazePointData` timestamp:
- Use `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000` instead of `data.timestamp_us`

Full replacement of the class (preserving same public API):

```csharp
namespace EyeCtrl.Platform.Gaze;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using Tobii.InteractionLib;

/// <summary>
/// Gaze provider using the Tobii Interaction Library (tobii_interaction_lib_cs.dll).
/// Polls the tracker on a background thread and fires GazePointReceived on each valid gaze sample.
/// Reconnects indefinitely with exponential back-off if the tracker disconnects.
/// </summary>
public class TobiiInteractionLibProvider : IGazeProvider
{
    private readonly float _screenWidth;
    private readonly float _screenHeight;

    private IInteractionLib? _interactionLib;
    private Thread? _pollThread;
    private volatile bool _running;
    private string? _lastExceptionMessage;

    public event Action<GazePoint>? GazePointReceived;
    public bool IsConnected { get; private set; }

    /// <summary>
    /// Creates a Tobii gaze provider. Throws immediately if the Tobii native library
    /// is not available, so callers can catch and fall back to the next provider.
    /// </summary>
    public TobiiInteractionLibProvider(double screenWidth, double screenHeight)
    {
        _screenWidth = (float)screenWidth;
        _screenHeight = (float)screenHeight;

        // Create the InteractionLib immediately to fail fast if Tobii is unavailable
        _interactionLib = InteractionLibFactory.CreateInteractionLib(FieldOfUse.Interactive);
    }

    public void Start()
    {
        ConfigureAndSubscribe();

        _running = true;
        _pollThread = new Thread(PollLoop)
        {
            IsBackground = true,
            Name = "TobiiGazePoll"
        };
        _pollThread.Start();
    }

    public void Stop()
    {
        _running = false;
        _pollThread?.Join(1000);
        _pollThread = null;

        if (_interactionLib != null)
        {
            _interactionLib.GazePointDataEvent -= OnGazePointData;
            _interactionLib = null;
        }

        IsConnected = false;
    }

    private void ConfigureAndSubscribe()
    {
        _interactionLib!.CoordinateTransformAddOrUpdateDisplayArea(
            _screenWidth, _screenHeight,
            _screenWidth, _screenHeight,
            0f, 0f, "main");

        _interactionLib.GazePointDataEvent += OnGazePointData;
    }

    private void PollLoop()
    {
        int backoffMs = 500;
        const int maxBackoffMs = 5000;

        while (_running)
        {
            try
            {
                _interactionLib?.WaitAndUpdate(33); // ~30 Hz
                if (!IsConnected)
                {
                    IsConnected = true;
                    _lastExceptionMessage = null;
                    backoffMs = 500; // reset back-off on success
                }
            }
            catch (Exception ex)
            {
                IsConnected = false;

                // Error dedup: only log when the message changes
                if (ex.Message != _lastExceptionMessage)
                {
                    _lastExceptionMessage = ex.Message;
                    System.Diagnostics.Trace.TraceError(
                        $"[TobiiInteractionLib] {ex.Message}");
                }

                // Attempt reconnect
                Thread.Sleep(backoffMs);
                backoffMs = Math.Min(backoffMs * 2, maxBackoffMs);

                try
                {
                    // Dispose old instance
                    if (_interactionLib != null)
                    {
                        _interactionLib.GazePointDataEvent -= OnGazePointData;
                        _interactionLib = null;
                    }

                    // Recreate
                    _interactionLib = InteractionLibFactory.CreateInteractionLib(
                        FieldOfUse.Interactive);
                    ConfigureAndSubscribe();
                }
                catch
                {
                    // Recreate failed — will retry on next loop iteration
                }
            }
        }
    }

    private void OnGazePointData(GazePointData data)
    {
        if (data.validity != Validity.Valid) return;

        // Use system time to avoid Tobii clock drift (~5ms/6min)
        var timestampUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;

        GazePointReceived?.Invoke(new GazePoint(
            data.x,
            data.y,
            timestampUs,
            true));
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
```

**Step 2: Build to verify**

Run: `dotnet build src/EyeCtrl.Platform/EyeCtrl.Platform.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/EyeCtrl.Platform/Gaze/TobiiInteractionLibProvider.cs
git commit -m "feat: harden TobiiInteractionLibProvider with reconnect, error dedup, clock drift fix"
```

---

### Task 4: Add Stream Engine reference to Platform csproj

**Files:**
- Modify: `src/EyeCtrl.Platform/EyeCtrl.Platform.csproj`

**Step 1: Add assembly reference + native DLL copy**

Add to the existing csproj, alongside the interaction lib references:

```xml
<ItemGroup>
  <Reference Include="Tobii.StreamEngine.Net">
    <HintPath>C:\Program Files (x86)\Tobii Dynavox\Computer Control\Tobii.StreamEngine.Net.dll</HintPath>
    <Private>true</Private>
  </Reference>
</ItemGroup>

<!-- Native Tobii Stream Engine DLL required at runtime (copied only if present) -->
<ItemGroup Condition="Exists('C:\Program Files (x86)\Tobii Dynavox\Computer Control\tobii_stream_engine.dll')">
  <Content Include="C:\Program Files (x86)\Tobii Dynavox\Computer Control\tobii_stream_engine.dll">
    <Link>tobii_stream_engine.dll</Link>
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </Content>
</ItemGroup>
```

**Step 2: Build to verify**

Run: `dotnet build src/EyeCtrl.Platform/EyeCtrl.Platform.csproj`
Expected: Build succeeded (the DLL is present on development machine)

**Step 3: Commit**

```bash
git add src/EyeCtrl.Platform/EyeCtrl.Platform.csproj
git commit -m "chore: add Tobii Stream Engine assembly reference and native DLL copy"
```

---

### Task 5: Create TobiiStreamEngineProvider

**Files:**
- Create: `src/EyeCtrl.Platform/Gaze/TobiiStreamEngineProvider.cs`

**Step 1: Create the provider**

This provider uses `Tobii.StreamEngine` for gaze data. Key differences from InteractionLib:
- Coordinates are normalized (0-1), multiply by screen dimensions
- Uses `tobii_gaze_point_subscribe` callback + `tobii_wait_for_callbacks` blocking poll
- Reconnect via `tobii_device_reconnect`

**IMPORTANT:** The exact API shape depends on the `Tobii.StreamEngine.Net.dll` wrapper. The code below is based on OptiKey's usage of the Stream Engine .NET bindings. If the namespace or types differ, adapt accordingly. Key classes: `Interop` (static P/Invoke methods), `tobii_error_t` (error enum), `tobii_gaze_point_t` (gaze data struct), `tobii_validity_t` (validity enum), `tobii_gaze_point_callback_t` (callback delegate).

```csharp
namespace EyeCtrl.Platform.Gaze;

using System.Diagnostics;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using Tobii.StreamEngine;

/// <summary>
/// Gaze provider using the Tobii Stream Engine (Tobii.StreamEngine.Net.dll).
/// Alternative to TobiiInteractionLibProvider. Uses lower-level SDK with
/// callback-based gaze subscription and blocking wait.
/// Reconnects indefinitely on disconnect.
/// </summary>
public class TobiiStreamEngineProvider : IGazeProvider
{
    private readonly double _screenWidth;
    private readonly double _screenHeight;

    private IntPtr _apiContext;
    private IntPtr _deviceContext;
    private Thread? _pollThread;
    private volatile bool _running;
    private readonly object _lock = new();
    private string? _lastErrorMessage;

    // Must keep a reference to prevent GC from collecting the delegate
    private tobii_gaze_point_callback_t? _gazeCallback;

    public event Action<GazePoint>? GazePointReceived;
    public bool IsConnected { get; private set; }

    /// <summary>
    /// Creates a Tobii Stream Engine provider. Throws if the Stream Engine API
    /// cannot be initialized or no device is found.
    /// </summary>
    public TobiiStreamEngineProvider(double screenWidth, double screenHeight)
    {
        _screenWidth = screenWidth;
        _screenHeight = screenHeight;

        ConnectApi();
        ConnectDevice();
    }

    public void Start()
    {
        SubscribeGaze();

        _running = true;
        _pollThread = new Thread(PollLoop)
        {
            IsBackground = true,
            Name = "TobiiStreamEnginePoll"
        };
        _pollThread.Start();
    }

    public void Stop()
    {
        _running = false;
        _pollThread?.Join(1000);
        _pollThread = null;

        lock (_lock)
        {
            CleanupDevice();
            CleanupApi();
        }

        IsConnected = false;
    }

    private void ConnectApi()
    {
        tobii_error_t result = Interop.tobii_api_create(out _apiContext, null);
        if (result != tobii_error_t.TOBII_ERROR_NO_ERROR)
            throw new InvalidOperationException(
                $"Failed to create Tobii Stream Engine API: {result}");
    }

    private void ConnectDevice()
    {
        List<string> urls;
        tobii_error_t result = Interop.tobii_enumerate_local_device_urls(
            _apiContext, out urls);
        if (result != tobii_error_t.TOBII_ERROR_NO_ERROR || urls.Count == 0)
            throw new InvalidOperationException("No Tobii device found via Stream Engine");

        result = Interop.tobii_device_create(
            _apiContext, urls[0],
            Interop.tobii_field_of_use_t.TOBII_FIELD_OF_USE_STORE_OR_TRANSFER_FALSE,
            out _deviceContext);
        if (result != tobii_error_t.TOBII_ERROR_NO_ERROR)
            throw new InvalidOperationException(
                $"Failed to create Tobii device: {result}");
    }

    private void SubscribeGaze()
    {
        _gazeCallback = (ref tobii_gaze_point_t gazePoint, IntPtr userData) =>
        {
            if (gazePoint.validity == tobii_validity_t.TOBII_VALIDITY_VALID)
            {
                var timestampUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
                GazePointReceived?.Invoke(new GazePoint(
                    _screenWidth * gazePoint.position.x,
                    _screenHeight * gazePoint.position.y,
                    timestampUs,
                    true));
            }
        };

        tobii_error_t result = Interop.tobii_gaze_point_subscribe(
            _deviceContext, _gazeCallback);
        Debug.Assert(result == tobii_error_t.TOBII_ERROR_NO_ERROR);
    }

    private void PollLoop()
    {
        int backoffMs = 500;
        const int maxBackoffMs = 5000;

        while (_running)
        {
            lock (_lock)
            {
                if (_deviceContext == IntPtr.Zero) break;

                tobii_error_t result = Interop.tobii_wait_for_callbacks(
                    new[] { _deviceContext });
                bool hasError = IsError(result);

                if (!hasError)
                {
                    result = Interop.tobii_device_process_callbacks(_deviceContext);
                    hasError = IsError(result);
                }

                if (hasError)
                {
                    IsConnected = false;
                    Thread.Sleep(backoffMs);
                    backoffMs = Math.Min(backoffMs * 2, maxBackoffMs);
                    Interop.tobii_device_reconnect(_deviceContext);
                }
                else
                {
                    if (!IsConnected)
                    {
                        IsConnected = true;
                        _lastErrorMessage = null;
                        backoffMs = 500;
                    }
                }
            }
        }
    }

    private bool IsError(tobii_error_t error)
    {
        if (error == tobii_error_t.TOBII_ERROR_NO_ERROR) return false;
        // Error dedup
        var msg = error.ToString();
        if (msg != _lastErrorMessage)
        {
            _lastErrorMessage = msg;
            Trace.TraceError($"[TobiiStreamEngine] {msg}");
        }
        return true;
    }

    private void CleanupDevice()
    {
        if (_deviceContext != IntPtr.Zero)
        {
            Interop.tobii_gaze_point_unsubscribe(_deviceContext);
            Interop.tobii_device_destroy(_deviceContext);
            _deviceContext = IntPtr.Zero;
        }
    }

    private void CleanupApi()
    {
        if (_apiContext != IntPtr.Zero)
        {
            Interop.tobii_api_destroy(_apiContext);
            _apiContext = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
```

**Step 2: Build to verify**

Run: `dotnet build src/EyeCtrl.Platform/EyeCtrl.Platform.csproj`
Expected: Build succeeded. If `Tobii.StreamEngine` types differ from expected, adapt the code to match the actual DLL's API surface.

**Step 3: Commit**

```bash
git add src/EyeCtrl.Platform/Gaze/TobiiStreamEngineProvider.cs
git commit -m "feat: add TobiiStreamEngineProvider using Tobii Stream Engine SDK"
```

---

### Task 6: Create GazeProviderFactory

**Files:**
- Create: `src/EyeCtrl.Platform/Gaze/GazeProviderFactory.cs`

**Step 1: Write the factory test**

Create test file `tests/EyeCtrl.Platform.Tests/Gaze/GazeProviderFactoryTests.cs`:

```csharp
namespace EyeCtrl.Platform.Tests.Gaze;

using EyeCtrl.Core.Enums;
using EyeCtrl.Platform.Gaze;

public class GazeProviderFactoryTests
{
    private readonly GazeProviderFactory _factory = new();

    [Fact]
    public void Create_Mouse_ReturnsMouseGazeProvider()
    {
        var provider = _factory.Create(GazeProviderType.Mouse, 1920, 1080);
        Assert.IsType<MouseGazeProvider>(provider);
        provider.Dispose();
    }

    [Fact]
    public void Create_Auto_FallsBackToMouse_WhenNoTobiiHardware()
    {
        // On CI/dev machines without Tobii hardware, Auto should fall through
        // InteractionLib (throws) -> StreamEngine (throws) -> Mouse (succeeds)
        var provider = _factory.Create(GazeProviderType.Auto, 1920, 1080);
        Assert.IsType<MouseGazeProvider>(provider);
        provider.Dispose();
    }

    [Fact]
    public void Create_InteractionLib_Throws_WhenNoTobiiHardware()
    {
        // Explicit InteractionLib on a machine without Tobii should throw
        Assert.ThrowsAny<Exception>(() =>
            _factory.Create(GazeProviderType.InteractionLib, 1920, 1080));
    }

    [Fact]
    public void Create_StreamEngine_Throws_WhenNoTobiiHardware()
    {
        // Explicit StreamEngine on a machine without Tobii should throw
        Assert.ThrowsAny<Exception>(() =>
            _factory.Create(GazeProviderType.StreamEngine, 1920, 1080));
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EyeCtrl.Platform.Tests/ --filter "GazeProviderFactory"`
Expected: FAIL (GazeProviderFactory doesn't exist yet)

**Step 3: Write the factory**

```csharp
namespace EyeCtrl.Platform.Gaze;

using System.Diagnostics;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Interfaces;

public class GazeProviderFactory
{
    /// <summary>
    /// Creates a gaze provider for the requested type.
    /// For Auto: tries InteractionLib -> StreamEngine -> Mouse, returning the first that succeeds.
    /// For explicit types: creates that provider and throws if unavailable.
    /// </summary>
    public IGazeProvider Create(GazeProviderType type, double screenWidth, double screenHeight)
    {
        return type switch
        {
            GazeProviderType.Mouse => new MouseGazeProvider(),
            GazeProviderType.InteractionLib => new TobiiInteractionLibProvider(screenWidth, screenHeight),
            GazeProviderType.StreamEngine => new TobiiStreamEngineProvider(screenWidth, screenHeight),
            GazeProviderType.Auto => CreateAuto(screenWidth, screenHeight),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };
    }

    /// <summary>
    /// Returns the GazeProviderType that describes the given provider instance.
    /// Used by the toolbar to show which engine is active.
    /// </summary>
    public static GazeProviderType GetProviderType(IGazeProvider provider) => provider switch
    {
        TobiiInteractionLibProvider => GazeProviderType.InteractionLib,
        TobiiStreamEngineProvider => GazeProviderType.StreamEngine,
        _ => GazeProviderType.Mouse
    };

    private IGazeProvider CreateAuto(double screenWidth, double screenHeight)
    {
        // Try InteractionLib first
        try
        {
            var provider = new TobiiInteractionLibProvider(screenWidth, screenHeight);
            Trace.TraceInformation("[GazeProviderFactory] Using Tobii Interaction Library");
            return provider;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                $"[GazeProviderFactory] InteractionLib unavailable: {ex.Message}");
        }

        // Try StreamEngine
        try
        {
            var provider = new TobiiStreamEngineProvider(screenWidth, screenHeight);
            Trace.TraceInformation("[GazeProviderFactory] Using Tobii Stream Engine");
            return provider;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                $"[GazeProviderFactory] StreamEngine unavailable: {ex.Message}");
        }

        // Fall back to mouse
        Trace.TraceInformation("[GazeProviderFactory] Falling back to Mouse simulation");
        return new MouseGazeProvider();
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.Platform.Tests/ --filter "GazeProviderFactory"`
Expected: 4 passed

Note: The `Create_Auto_FallsBackToMouse_WhenNoTobiiHardware` and explicit-type-throws tests assume no Tobii hardware on the build machine. On a machine with Tobii, Auto would return a Tobii provider and the explicit tests would succeed — both are correct behavior.

**Step 5: Commit**

```bash
git add src/EyeCtrl.Platform/Gaze/GazeProviderFactory.cs tests/EyeCtrl.Platform.Tests/Gaze/GazeProviderFactoryTests.cs
git commit -m "feat: add GazeProviderFactory with auto-fallback chain"
```

---

### Task 7: Wire factory into App.xaml.cs

**Files:**
- Modify: `src/EyeCtrl.App/App.xaml.cs:77-91` (provider selection)
- Modify: `src/EyeCtrl.App/App.xaml.cs:122` (isTobii bool)
- Modify: `src/EyeCtrl.App/App.xaml.cs:155` (SetProviderIndicator call)

**Step 1: Replace provider selection (lines 77-91)**

Replace the `IGazeProvider` registration:

```csharp
// Old:
services.AddSingleton<IGazeProvider>(_ =>
{
    if (IsTestMode)
        return new MouseGazeProvider();
    try
    {
        return new TobiiInteractionLibProvider(
            SystemParameters.PrimaryScreenWidth,
            SystemParameters.PrimaryScreenHeight);
    }
    catch
    {
        return new MouseGazeProvider();
    }
});

// New:
services.AddSingleton<IGazeProvider>(_ =>
{
    if (IsTestMode)
        return new MouseGazeProvider();
    var factory = new GazeProviderFactory();
    return factory.Create(settings.General.GazeProviderType,
        SystemParameters.PrimaryScreenWidth,
        SystemParameters.PrimaryScreenHeight);
});
```

**Step 2: Replace isTobii detection (line 122) and indicator call (line 155)**

```csharp
// Old:
bool isTobii = gazeProvider is TobiiInteractionLibProvider;
// ...
toolbar.SetProviderIndicator(isTobii);

// New:
var activeProviderType = GazeProviderFactory.GetProviderType(gazeProvider);
// ...
toolbar.SetProviderIndicator(activeProviderType);
```

Add `using EyeCtrl.Core.Enums;` if not already present.

**Step 3: Build to verify** (will fail until Task 8 updates toolbar signature)

Run: `dotnet build src/EyeCtrl.App/`
Expected: Build error on `SetProviderIndicator` signature mismatch — fixed in Task 8.

**Step 4: Commit (together with Task 8)**

This task is committed together with Task 8 since they have a compile-time dependency.

---

### Task 8: Update toolbar indicator to three-state

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs:113-117` (SetProviderIndicator)
- Modify: `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs:87-110` (AddIndicatorElement)
- Modify: `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs:15` (field type)
- Modify: `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs:42` (Initialize re-add)

**Step 1: Change field and method signatures**

```csharp
// Old (line 15):
private bool? _providerIsTobii;

// New:
private GazeProviderType? _providerType;
```

Add `using EyeCtrl.Core.Enums;` at the top.

**Step 2: Update SetProviderIndicator**

```csharp
// Old:
public void SetProviderIndicator(bool isTobii)
{
    _providerIsTobii = isTobii;
    AddIndicatorElement(isTobii);
}

// New:
public void SetProviderIndicator(GazeProviderType providerType)
{
    _providerType = providerType;
    AddIndicatorElement(providerType);
}
```

**Step 3: Update AddIndicatorElement**

```csharp
// Old:
private void AddIndicatorElement(bool isTobii)
{
    var symbol = isTobii ? "\U0001F441" : "\U0001F5B1"; // eye or mouse
    // ... rest unchanged

// New:
private void AddIndicatorElement(GazeProviderType providerType)
{
    var symbol = providerType switch
    {
        GazeProviderType.InteractionLib => "\U0001F441",  // 👁 eye
        GazeProviderType.StreamEngine => "\u2B55",         // ⭕ circle (stream)
        GazeProviderType.Mouse => "\U0001F5B1",           // 🖱 mouse
        _ => "?"
    };
    // ... rest of method stays the same, just replace isTobii parameter name with providerType
```

**Step 4: Update Initialize re-add (line 42)**

```csharp
// Old:
if (_providerIsTobii.HasValue)
    AddIndicatorElement(_providerIsTobii.Value);

// New:
if (_providerType.HasValue)
    AddIndicatorElement(_providerType.Value);
```

**Step 5: Build full solution**

Run: `dotnet build`
Expected: Build succeeded

**Step 6: Commit (together with Task 7)**

```bash
git add src/EyeCtrl.App/App.xaml.cs src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs
git commit -m "feat: wire GazeProviderFactory into App.xaml.cs, update toolbar to three-state indicator"
```

---

### Task 9: Run all tests

**Step 1: Run the full test suite**

Run: `dotnet test`
Expected: All tests pass (existing 30 + 4 new factory tests = 34 total)

**Step 2: Fix any failures**

If any existing tests break, diagnose and fix.

---

### Task 10: Build and manual test

**Step 1: Publish**

Run: `dotnet publish src/EyeCtrl.App/ -r win-x64 --self-contained true -p:PublishSingleFile=true`
Expected: Publish succeeded

**Step 2: Manual test checklist**

- [ ] Launch with Tobii connected — indicator shows 👁 (InteractionLib)
- [ ] Unplug tracker — `IsConnected` goes false, gaze stops
- [ ] Plug tracker back in — gaze resumes within a few seconds
- [ ] Change setting to `StreamEngine`, restart — indicator shows ⭕
- [ ] Change setting to `Mouse`, restart — indicator shows 🖱, mouse simulation works
- [ ] Change setting to `Auto`, restart — selects best available provider
- [ ] Launch with `--test-mode` — always uses Mouse regardless of setting

**Step 3: Final commit if any manual test fixes needed**
