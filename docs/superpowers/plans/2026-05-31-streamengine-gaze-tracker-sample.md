# StreamEngine Gaze Tracker Sample — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create a standalone WPF sample that demonstrates Tobii StreamEngine API usage with gaze trail visualization, debug overlay, and OneEuroFilter smoothing.

**Architecture:** Single-window WPF app with all StreamEngine logic in MainWindow.xaml.cs. Background poll thread feeds gaze data, DispatcherTimer drives UI at 60Hz. Self-contained — no dependencies on EyeCtrl.Core/Platform.

**Tech Stack:** .NET 10.0, WPF, Tobii.StreamEngine.Net (managed wrapper), `net10.0-windows`

---

## File Structure

| File | Action | Purpose |
|------|--------|---------|
| `samples/StreamEngineGazeTracker/StreamEngineGazeTracker.csproj` | Create | Project file with conditional Tobii SDK reference |
| `samples/StreamEngineGazeTracker/App.xaml` | Create | WPF application definition |
| `samples/StreamEngineGazeTracker/App.xaml.cs` | Create | Minimal code-behind |
| `samples/StreamEngineGazeTracker/MainWindow.xaml` | Create | Window layout: Canvas, ellipse pool, debug TextBlock |
| `samples/StreamEngineGazeTracker/MainWindow.xaml.cs` | Create | StreamEngine lifecycle, OneEuroFilter, UI update loop |

---

### Task 1: Project Scaffolding

**Files:**
- Create: `samples/StreamEngineGazeTracker/StreamEngineGazeTracker.csproj`
- Create: `samples/StreamEngineGazeTracker/App.xaml`
- Create: `samples/StreamEngineGazeTracker/App.xaml.cs`

- [ ] **Step 1: Create the directory**

```bash
mkdir -p samples/StreamEngineGazeTracker
```

- [ ] **Step 2: Create the .csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
  </PropertyGroup>

  <!-- Tobii Stream Engine: requires the SDK to be installed.
       This sample hard-fails without it (unlike EyeCtrl.Platform which degrades gracefully).
       Override path via -p:TobiiSdkPath="..." -->
  <PropertyGroup>
    <TobiiSdkPath Condition="'$(TobiiSdkPath)' == ''">C:\Program Files\Tobii\Tobii EyeX\</TobiiSdkPath>
    <TobiiSdkPath>$([MSBuild]::EnsureTrailingSlash($(TobiiSdkPath)))</TobiiSdkPath>
    <TobiiManagedDll>$(TobiiSdkPath)Tobii.StreamEngine.Net.dll</TobiiManagedDll>
    <TobiiNativeDll>$(TobiiSdkPath)tobii_stream_engine.dll</TobiiNativeDll>
  </PropertyGroup>

  <ItemGroup Condition="$([MSBuild]::IsOSPlatform('Windows')) And Exists('$(TobiiManagedDll)') And Exists('$(TobiiNativeDll)')">
    <Reference Include="Tobii.StreamEngine.Net">
      <HintPath>$(TobiiManagedDll)</HintPath>
      <Private>true</Private>
    </Reference>
    <Content Include="$(TobiiNativeDll)">
      <Link>tobii_stream_engine.dll</Link>
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
  </ItemGroup>

  <!-- Error when either DLL is missing (or non-Windows). Uses Or to match the negation
       of the ItemGroup condition above (which requires both with And). -->
  <Target Name="ErrorWhenNoTobiiSdk" BeforeTargets="CoreCompile"
          Condition="!$([MSBuild]::IsOSPlatform('Windows')) Or !Exists('$(TobiiManagedDll)') Or !Exists('$(TobiiNativeDll)')">
    <Error Text="Tobii StreamEngine SDK not found at '$(TobiiSdkPath)'. Install the Tobii EyeX driver or set -p:TobiiSdkPath=&quot;path/to/sdk&quot;" />
  </Target>

</Project>
```

- [ ] **Step 3: Create App.xaml**

```xml
<Application x:Class="StreamEngineGazeTracker.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             StartupUri="MainWindow.xaml">
</Application>
```

- [ ] **Step 4: Create App.xaml.cs**

```csharp
namespace StreamEngineGazeTracker;

using System.Windows;

public partial class App : Application
{
}
```

- [ ] **Step 5: Verify it compiles (will error if no Tobii SDK — that's expected)**

```bash
dotnet build samples/StreamEngineGazeTracker
```

Expected: Build error "Tobii StreamEngine SDK not found" if SDK is absent. If SDK is present, build succeeds (no MainWindow content yet, but the project compiles).

- [ ] **Step 6: Commit**

```bash
git add samples/StreamEngineGazeTracker/
git commit -m "feat(sample): add StreamEngine gaze tracker project scaffolding"
```

---

### Task 2: Window Layout (MainWindow.xaml)

**Files:**
- Create: `samples/StreamEngineGazeTracker/MainWindow.xaml`

- [ ] **Step 1: Create MainWindow.xaml**

```xml
<Window x:Class="StreamEngineGazeTracker.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="StreamEngine Gaze Tracker"
        Width="800" Height="600"
        Background="#1a1a2e"
        WindowStartupLocation="CenterScreen">

    <Canvas x:Name="GazeCanvas"
            ClipToBounds="True"
            Background="Transparent">

        <!-- Debug overlay (high ZIndex so dots never draw over it) -->
        <Border Canvas.Left="10" Canvas.Top="10"
                Panel.ZIndex="100"
                Background="#80000000"
                Padding="8"
                CornerRadius="4">
            <TextBlock x:Name="DebugText"
                       FontFamily="Consolas"
                       FontSize="13"
                       Foreground="White"
                       Text="Connecting..." />
        </Border>

    </Canvas>
</Window>
```

- [ ] **Step 2: Commit**

```bash
git add samples/StreamEngineGazeTracker/MainWindow.xaml
git commit -m "feat(sample): add MainWindow XAML layout with canvas and debug overlay"
```

---

### Task 3: Complete MainWindow.xaml.cs

**Files:**
- Create: `samples/StreamEngineGazeTracker/MainWindow.xaml.cs`

This task creates the complete file in one step. The StreamEngine lifecycle, OneEuroFilter, and UI update loop are all interdependent (the file must compile as a whole), so they cannot be split across commits.

- [ ] **Step 1: Create MainWindow.xaml.cs**

```csharp
namespace StreamEngineGazeTracker;

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Tobii.StreamEngine;

public partial class MainWindow : Window
{
    // ── StreamEngine handles ──────────────────────────────────────────
    private IntPtr _api;
    private IntPtr _device;

    // Must keep a reference to prevent GC from collecting the marshalled delegate.
    // Without this field, the GC collects the delegate and StreamEngine crashes
    // with CallbackOnCollectedDelegate after the first GC.
    private tobii_gaze_point_callback_t? _gazeCallback;

    // ── Poll loop ─────────────────────────────────────────────────────
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private readonly IntPtr[] _deviceArray = new IntPtr[1]; // hoist to avoid per-iteration allocation

    // ── Shared gaze data (callback thread → UI timer) ─────────────────
    private readonly object _gazeLock = new();
    private double _rawX;
    private double _rawY;
    private bool _rawValid;
    private int _gazeCallbackCount;

    // ── Hz computation (1-second rolling window) ──────────────────────
    private readonly Queue<(DateTime time, int count)> _hzWindow = new();
    private double _currentHz;

    // ── OneEuroFilter state ───────────────────────────────────────────
    private OneEuroFilter? _filterX;
    private OneEuroFilter? _filterY;

    // ── Trail ─────────────────────────────────────────────────────────
    private const int MaxTrailPoints = 50;
    private const double TrailDotSize = 6;
    private const double GazeDotSize = 16;
    private const double TrailColorR = 0;
    private const double TrailColorG = 212;
    private const double TrailColorB = 255;
    private const double GazeDotColorR = 255;
    private const double GazeDotColorG = 51;
    private const double GazeDotColorB = 102;

    private readonly List<Point> _trailPoints = new(MaxTrailPoints + 1);
    private System.Windows.Shapes.Ellipse[] _trailDots = null!;
    private System.Windows.Shapes.Ellipse _gazeDot = null!;
    private bool _uiInitialized;

    // ── Performance tracking ──────────────────────────────────────────
    private readonly Stopwatch _uptime = new();

    // ── UI timer ──────────────────────────────────────────────────────
    private DispatcherTimer _uiTimer = null!;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            InitializeStreamEngine();
            SubscribeGaze();
            StartPollLoop();
            StartUITimer();
            _uptime.Start();
        }
        catch (Exception ex)
        {
            Dispatcher.BeginInvoke(() =>
            {
                MessageBox.Show(this, ex.Message, "StreamEngine Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            });
        }
    }

    // ── StreamEngine initialization ───────────────────────────────────

    private void InitializeStreamEngine()
    {
        var result = Interop.tobii_api_create(out _api, null);
        if (result != tobii_error_t.TOBII_ERROR_NO_ERROR)
            throw new InvalidOperationException($"Failed to create Tobii API: {result}");

        result = Interop.tobii_enumerate_local_device_urls(_api, out List<string> urls);
        if (result != tobii_error_t.TOBII_ERROR_NO_ERROR || urls.Count == 0)
            throw new InvalidOperationException("No Tobii device found. Connect a Tobii tracker and try again.");

        result = Interop.tobii_device_create(
            _api, urls[0],
            Interop.tobii_field_of_use_t.TOBII_FIELD_OF_USE_STORE_OR_TRANSFER_FALSE,
            out _device);
        if (result != tobii_error_t.TOBII_ERROR_NO_ERROR)
            throw new InvalidOperationException($"Failed to create Tobii device: {result}");

        _deviceArray[0] = _device;
    }

    private void SubscribeGaze()
    {
        _gazeCallback = (ref tobii_gaze_point_t gazePoint, IntPtr userData) =>
        {
            lock (_gazeLock)
            {
                _rawX = gazePoint.position.x;
                _rawY = gazePoint.position.y;
                _rawValid = gazePoint.validity == tobii_validity_t.TOBII_VALIDITY_VALID;
            }
            Interlocked.Increment(ref _gazeCallbackCount);
        };

        var result = Interop.tobii_gaze_point_subscribe(_device, _gazeCallback, IntPtr.Zero);
        if (result != tobii_error_t.TOBII_ERROR_NO_ERROR)
            throw new InvalidOperationException($"Failed to subscribe to gaze data: {result}");
    }

    // ── Poll loop ─────────────────────────────────────────────────────

    private void StartPollLoop()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _pollTask = Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                var result = Interop.tobii_wait_for_callbacks(_deviceArray);
                if (result == tobii_error_t.TOBII_ERROR_TIMED_OUT)
                    continue; // normal — no data yet, loop back to wait
                if (result != tobii_error_t.TOBII_ERROR_NO_ERROR)
                {
                    Thread.Sleep(100); // avoid hot-spin on persistent device errors
                    continue;
                }

                result = Interop.tobii_device_process_callbacks(_device);
                if (result != tobii_error_t.TOBII_ERROR_NO_ERROR)
                {
                    Thread.Sleep(100);
                    continue;
                }
            }
        }, token);
    }

    // ── Cleanup ───────────────────────────────────────────────────────

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Order is critical: stop the poll thread BEFORE destroying native handles.
        // Otherwise the poll thread may be inside tobii_wait_for_callbacks on a destroyed device.
        // Null-conditional because OnClosing can fire if initialization failed (no _cts/_pollTask yet).
        _cts?.Cancel();
        _pollTask?.Wait(); // Must wait until the task has actually exited to avoid use-after-destroy

        if (_device != IntPtr.Zero)
        {
            Interop.tobii_gaze_point_unsubscribe(_device);
            Interop.tobii_device_destroy(_device);
        }

        if (_api != IntPtr.Zero)
        {
            Interop.tobii_api_destroy(_api);
        }
    }

    // ── UI timer ──────────────────────────────────────────────────────

    private void StartUITimer()
    {
        // OneEuroFilter: separate instances for X and Y axes.
        // Parameters are for normalized (0..1) coordinate space.
        // NOTE: The production EyeCtrl app uses different params (minCutoff=5.0, beta=0.5)
        // and operates in pixel space with timestamp-derived dt.
        // See: src/EyeCtrl.Core/Processing/OneEuroFilter.cs
        _filterX = new OneEuroFilter(minCutoff: 1.0, beta: 0.007, dCutoff: 1.0);
        _filterY = new OneEuroFilter(minCutoff: 1.0, beta: 0.007, dCutoff: 1.0);

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / 60) };
        _uiTimer.Tick += OnUITick;
        _uiTimer.Start();
    }

    private void OnUITick(object? sender, EventArgs e)
    {
        // 1. Read latest gaze data
        double rawX, rawY;
        bool rawValid;
        int ticksThisFrame;
        lock (_gazeLock)
        {
            rawX = _rawX;
            rawY = _rawY;
            rawValid = _rawValid;
        }
        ticksThisFrame = Interlocked.Exchange(ref _gazeCallbackCount, 0);

        // 2. Update Hz rolling window (1-second accumulation)
        var now = DateTime.UtcNow;
        _hzWindow.Enqueue((now, ticksThisFrame));
        while (_hzWindow.Count > 0 && (now - _hzWindow.Peek().time).TotalSeconds > 1.0)
            _hzWindow.Dequeue();
        int totalInWindow = 0;
        foreach (var entry in _hzWindow)
            totalInWindow += entry.count;
        _currentHz = totalInWindow; // samples per 1-second window = Hz

        // 3. Initialize visual elements on first tick
        if (!_uiInitialized)
        {
            InitializeVisuals();
            _uiInitialized = true;
        }

        // 4. Apply OneEuroFilter (fixed dt = 1/60, tied to timer rate)
        //    When no new data arrives, re-filtering the same point produces velocity ≈ 0,
        //    which momentarily increases smoothing. Acceptable for a sample.
        //    Skip filter/trail/dot on invalid gaze (blinks, tracking loss) — StreamEngine
        //    leaves position undefined on invalid samples, so filtering garbage would snap
        //    the dot to (0,0) or stale coordinates.
        const double dt = 1.0 / 60.0;
        double px = 0, py = 0;
        double fx = 0, fy = 0;
        if (rawValid)
        {
            fx = _filterX!.Filter(rawX, dt);
            fy = _filterY!.Filter(rawY, dt);

            // 5. Convert normalized coords to canvas pixels
            double canvasW = GazeCanvas.ActualWidth;
            double canvasH = GazeCanvas.ActualHeight;
            px = fx * canvasW;
            py = fy * canvasH;

            // 6. Update trail
            _trailPoints.Add(new Point(px, py));
            while (_trailPoints.Count > MaxTrailPoints)
                _trailPoints.RemoveAt(0);

            // 7. Update trail dots (pool of Ellipses)
            for (int i = 0; i < _trailDots.Length; i++)
            {
                if (i < _trailPoints.Count)
                {
                    var pt = _trailPoints[i];
                    double opacity = (double)(i + 1) / _trailPoints.Count;
                    Canvas.SetLeft(_trailDots[i], pt.X - TrailDotSize / 2);
                    Canvas.SetTop(_trailDots[i], pt.Y - TrailDotSize / 2);
                    _trailDots[i].Opacity = opacity * 0.7;
                    _trailDots[i].Visibility = Visibility.Visible;
                }
                else
                {
                    _trailDots[i].Visibility = Visibility.Collapsed;
                }
            }

            // 8. Update gaze dot
            Canvas.SetLeft(_gazeDot, px - GazeDotSize / 2);
            Canvas.SetTop(_gazeDot, py - GazeDotSize / 2);
        }

        // 9. Update debug overlay (single assignment per tick)
        string status = rawValid ? "Connected" : "Invalid";
        DebugText.Text = rawValid
            ? $"Status: {status}\n" +
              $"Raw:    ({rawX:F4}, {rawY:F4})\n" +
              $"Filter: ({fx:F4}, {fy:F4})\n" +
              $"Screen: ({px:F0}, {py:F0})\n" +
              $"Gaze:   {_currentHz:F0} Hz\n" +
              $"Uptime: {_uptime.Elapsed:mm\\:ss}"
            : $"Status: {status}\n" +
              $"Raw:    ({rawX:F4}, {rawY:F4})\n" +
              $"Gaze:   {_currentHz:F0} Hz\n" +
              $"Uptime: {_uptime.Elapsed:mm\\:ss}";
    }

    private void InitializeVisuals()
    {
        // Create trail dot pool
        _trailDots = new System.Windows.Shapes.Ellipse[MaxTrailPoints];
        for (int i = 0; i < _trailDots.Length; i++)
        {
            _trailDots[i] = new System.Windows.Shapes.Ellipse
            {
                Width = TrailDotSize,
                Height = TrailDotSize,
                Fill = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(
                        (byte)TrailColorR, (byte)TrailColorG, (byte)TrailColorB)),
                Visibility = Visibility.Collapsed
            };
            GazeCanvas.Children.Add(_trailDots[i]);
        }

        // Create gaze dot
        _gazeDot = new System.Windows.Shapes.Ellipse
        {
            Width = GazeDotSize,
            Height = GazeDotSize,
            Fill = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(
                    (byte)GazeDotColorR, (byte)GazeDotColorG, (byte)GazeDotColorB))
        };
        GazeCanvas.Children.Add(_gazeDot);
    }

    // ── Minimal OneEuro Filter ────────────────────────────────────────
    // A self-contained ~30-line implementation for this sample.
    // See src/EyeCtrl.Core/Processing/OneEuroFilter.cs for the production version.

    private class OneEuroFilter
    {
        private readonly double _minCutoff;
        private readonly double _beta;
        private readonly double _dCutoff;
        private double _xFilt;
        private double _dxFilt;
        private bool _initialized;

        public OneEuroFilter(double minCutoff, double beta, double dCutoff)
        {
            _minCutoff = minCutoff;
            _beta = beta;
            _dCutoff = dCutoff;
        }

        public double Filter(double x, double dt)
        {
            if (!_initialized)
            {
                _xFilt = x;
                _dxFilt = 0;
                _initialized = true;
                return x;
            }

            double dx = (x - _xFilt) / dt;
            double edx = LowPass(dx, _dxFilt, Alpha(_dCutoff, dt));
            _dxFilt = edx;

            double cutoff = _minCutoff + _beta * Math.Abs(edx);
            _xFilt = LowPass(x, _xFilt, Alpha(cutoff, dt));
            return _xFilt;
        }

        private static double Alpha(double cutoff, double dt)
        {
            double tau = 1.0 / (2 * Math.PI * cutoff);
            return 1.0 / (1.0 + tau / dt);
        }

        private static double LowPass(double x, double prev, double alpha)
        {
            return alpha * x + (1 - alpha) * prev;
        }
    }
}
```

- [ ] **Step 2: Verify it compiles**

```bash
dotnet build samples/StreamEngineGazeTracker
```

Expected: Build succeeds (with SDK present) or the custom SDK-not-found error.

- [ ] **Step 3: Commit**

```bash
git add samples/StreamEngineGazeTracker/MainWindow.xaml.cs
git commit -m "feat(sample): add complete MainWindow — StreamEngine lifecycle, OneEuroFilter, gaze trail, debug overlay"
```

---

### Task 4: Final Review and Cleanup

- [ ] **Step 1: Review the complete MainWindow.xaml.cs for correctness**

Check:
- `_gazeCallback` is stored in an instance field (GC guard)
- Cleanup order: cancel + wait poll task, then unsubscribe, then destroy
- `_cts?.Cancel()` / `_pollTask?.Wait()` — null-safe for failed init
- `lock(_gazeLock)` used for callback writes and UI timer reads of gaze position
- `Interlocked.Exchange` for counter read-and-reset (outside lock)
- `Interlocked.Increment` in callback (outside lock)
- Hz rolling window accumulates over 1 second
- OneEuroFilter uses fixed `dt = 1/60`
- Invalid gaze skips filter/trail/dot (holds last position)
- Ellipse pool (no per-frame allocations)
- `_deviceArray` hoisted (no per-iteration allocation in poll loop)
- Error dialogs deferred via `Dispatcher.BeginInvoke`
- `Panel.ZIndex="100"` on debug overlay Border
- `using System.Windows.Controls` for `Canvas.SetLeft`/`SetTop`
- `Thread.Sleep(100)` on non-timeout poll errors (no hot-spin)
- `tobii_error_t` member names match the installed DLL (especially `TOBII_ERROR_TIMED_OUT`)
- `.csproj` error target uses `Or` (not `And`) for partial-install detection

- [ ] **Step 2: Verify the project builds**

```bash
dotnet build samples/StreamEngineGazeTracker
```

- [ ] **Step 3: Run with a Tobii device (manual test)**

```bash
dotnet run --project samples/StreamEngineGazeTracker
```

Expected behavior:
- Window opens, dark background
- Red dot follows gaze position
- Cyan trail fades behind the dot
- Debug overlay shows connection status, raw/filtered coords, Hz, uptime
- Dot holds position during blinks (no snap to 0,0)
- Closing the window exits cleanly (no native crash)

- [ ] **Step 4: Final commit if any cleanup was needed**

```bash
git add -A
git commit -m "chore(sample): final cleanup for StreamEngine gaze tracker"
```
