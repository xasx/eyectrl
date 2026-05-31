# StreamEngine Gaze Tracker Sample — Design

## Overview

A standalone WPF sample application demonstrating Tobii StreamEngine API usage. Displays a window with a gaze-following dot, fading trail, debug overlay, and OneEuroFilter smoothing. Self-contained — no dependencies on EyeCtrl.Core or EyeCtrl.Platform.

## Project Structure

```
samples/
  StreamEngineGazeTracker/
    StreamEngineGazeTracker.csproj
    App.xaml
    App.xaml.cs
    MainWindow.xaml
    MainWindow.xaml.cs
```

The sample is **not** added to `EyeCtrl.slnx`. It hard-fails when the Tobii SDK is absent, unlike `EyeCtrl.Platform` which degrades gracefully. Keeping it out of the solution avoids breaking `dotnet build` / `dotnet test` on machines without a Tobii rig. Build it explicitly:

```bash
dotnet build samples/StreamEngineGazeTracker
```

## Window

- Normal resizable WPF window, default 800x600
- Dark background (`#1a1a2e`)
- Title: "StreamEngine Gaze Tracker"
- Contains a `Canvas` for all gaze visualization

## Visual Elements

### Gaze Trail
- Last 50 gaze points rendered as small circles (6px diameter)
- Decreasing opacity: newest = fully opaque, oldest = nearly transparent
- Color: cyan (`#00d4ff`)
- Use a fixed pool of 51 `Ellipse` elements, updating `Canvas.Left`/`Canvas.Top`/`Opacity` in place each frame to avoid per-frame allocations

### Current Gaze Dot
- Larger circle (16px diameter) at the latest filtered position
- Color: bright red (`#ff3366`)

### Debug Overlay
- `TextBlock` anchored top-left, monospace font, white text with semi-transparent dark background
- Shows:
  - Connection status (Connected / Disconnected / Error)
  - Raw normalized X/Y from StreamEngine (4 decimal places)
  - Filtered screen X/Y (integer pixels)
  - Sample validity (Valid / Invalid)
  - Gaze callback rate (Hz, computed from callback arrivals — see §Threading)

## Smoothing — OneEuroFilter

Implement a minimal OneEuroFilter directly in `MainWindow.xaml.cs` (~30 lines):
- Separate filter instances for X and Y axes
- Parameters: `minCutoff = 1.0`, `beta = 0.007`, `dCutoff = 1.0`
- Applied to normalized (0..1) coordinates before screen-space conversion
- **Time base:** `dt` is fixed at `1.0 / 60.0` (tied to the UI timer rate). When the timer fires with no new gaze data since the last tick, it re-filters the same (x, y) — this produces velocity ≈ 0, which momentarily increases smoothing. Acceptable for a sample.
- Add a code comment pointing to `src/EyeCtrl.Core/Processing/OneEuroFilter.cs` so readers know the production app uses a different parameterization (pixel-space, `minCutoff=5.0, beta=0.5`) with timestamp-derived `dt`.

## StreamEngine Lifecycle

All in `MainWindow.xaml.cs`:

### Instance Fields

```csharp
private IntPtr _api;
private IntPtr _device;
private tobii_gaze_point_callback_t _gazeCallback; // prevent GC — see §Threading
private CancellationTokenSource _cts;
private Task _pollTask;
```

The `_gazeCallback` field is **mandatory**. The managed delegate passed to `tobii_gaze_point_subscribe` must be kept alive for the subscription's lifetime. Without it, the GC collects the marshalled delegate and StreamEngine crashes with `CallbackOnCollectedDelegate`.

### 1. Initialization (OnLoaded)
```
tobii_api_create(out _api, null)
tobii_enumerate_local_device_urls(_api, out urls)
  → if empty: Dispatcher.BeginInvoke → MessageBox + Close()
tobii_device_create(_api, urls[0], STORE_OR_TRANSFER_FALSE, out _device)
  → if error: Dispatcher.BeginInvoke → MessageBox + Close()
```

Error paths defer `MessageBox` + `Close()` via `Dispatcher.BeginInvoke` to avoid closing mid-initialization.

### 2. Subscription
```
_gazeCallback = (ref tobii_gaze_point_t gp, IntPtr userData) => { ... }
tobii_gaze_point_subscribe(_device, _gazeCallback, IntPtr.Zero)
```

Callback stores `(x, y, validity)` in a shared struct via `lock(_gazeLock)` on the background thread. Also does `Interlocked.Increment(ref _gazeCallbackCount)` for Hz computation (increment outside lock is fine — it's the reset that needs guarding).

### 3. Poll Loop
```
_cts = new CancellationTokenSource()
_pollTask = Task.Run(() => {
    while (!_cts.IsCancellationRequested) {
        tobii_wait_for_callbacks(new[] { _device })
        tobii_device_process_callbacks(_device)
    }
})
```

### 4. UI Update (DispatcherTimer @ 60Hz)
1. Read latest raw (x, y, validity) via `lock(_gazeLock)`
2. Read `Interlocked.Exchange(ref _gazeCallbackCount, 0)` to get ticks since last frame; accumulate into a 1-second rolling window for Hz display
3. Feed (x, y) through OneEuroFilter → get filtered (fx, fy)
4. Convert filtered normalized coords to canvas pixels: `px = fx * canvas.ActualWidth`, `py = fy * canvas.ActualHeight`
5. Append to trail list, trim to 50
6. Update the pooled `Ellipse` elements (position + opacity) — no clear/recreate
7. Update debug TextBlock

### 5. Cleanup (OnClosing)

**Order is critical.** The poll thread must be stopped before destroying native handles:

```
1. _cts.Cancel()
2. _pollTask.Wait()               // block until the poll task has actually exited
3. tobii_gaze_point_unsubscribe(_device)
4. tobii_device_destroy(_device)
5. tobii_api_destroy(_api)
```

Destroying `_device` while the poll thread is inside `tobii_wait_for_callbacks` / `tobii_device_process_callbacks` is a use-after-destroy crash. The `Wait` guarantees the poll loop has exited before cleanup proceeds.

## Threading Model

- **Callback → UI:** The StreamEngine callback and the UI timer communicate via a small shared struct protected by `lock(_gazeLock)`. The callback writes `(x, y, validity)`; the timer reads. This is simpler and more correct than `Interlocked` for multi-field updates (avoids torn reads where a new `x` pairs with an old `y`).
- **Hz computation:** `Interlocked.Increment(ref _gazeCallbackCount)` in the native callback. The UI timer resets it with `Interlocked.Exchange(ref _gazeCallbackCount, 0)` outside the lock (atomic on a single int, no torn-read risk). The timer accumulates per-tick counts into a rolling window (last ~1 second). When the window reaches 1 second, compute `totalCount` as Hz, then shift the window. This avoids per-tick reset flicker (tiny integers × 60 = 0/60/120 jumps).
- **No `Dispatcher.Invoke` from callbacks** — avoids blocking the StreamEngine thread.

## Error Handling

- No device found → `Dispatcher.BeginInvoke` → MessageBox + close
- Device creation failure → `Dispatcher.BeginInvoke` → MessageBox + close
- No reconnection logic (keep sample simple)

## Dependencies

The `.csproj` references Tobii SDK DLLs using the same conditional pattern as `EyeCtrl.Platform.csproj`:
- Default SDK path: `C:\Program Files\Tobii\Tobii EyeX\`
- Override with: `-p:TobiiSdkPath="..."`
- Checks for `Tobii.StreamEngine.Net.dll` (managed) and `tobii_stream_engine.dll` (native)
- If absent: build fails with a clear error message

Unlike `EyeCtrl.Platform`, the sample does **not** gracefully degrade — it requires the SDK. This is acceptable because the sample's sole purpose is demonstrating StreamEngine usage.

Target framework: `net10.0-windows`, `UseWPF`.

## What This Sample Does NOT Demonstrate

- Reconnection / error recovery (see main EyeCtrl app)
- Gaze filtering beyond basic OneEuroFilter
- Fixation detection
- Cursor control
- DI / MVVM patterns
