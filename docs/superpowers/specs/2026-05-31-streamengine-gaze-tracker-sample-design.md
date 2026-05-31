# StreamEngine Gaze Tracker Sample ÔÇö Design

## Overview

A standalone WPF sample application demonstrating Tobii StreamEngine API usage. Displays a window with a gaze-following dot, fading trail, debug overlay, and OneEuroFilter smoothing. Self-contained ÔÇö no dependencies on EyeCtrl.Core or EyeCtrl.Platform.

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
  - Update rate (Hz, computed over last 60 samples)

## Smoothing ÔÇö OneEuroFilter

Implement a minimal OneEuroFilter directly in `MainWindow.xaml.cs` (~30 lines):
- Separate filter instances for X and Y axes
- Parameters: `minCutoff = 1.0`, `beta = 0.007`, `dCutoff = 1.0`
- Applied to normalized (0..1) coordinates before screen-space conversion

## StreamEngine Lifecycle

All in `MainWindow.xaml.cs`:

### 1. Initialization (OnLoaded)
```
tobii_api_create(out api, null)
tobii_enumerate_local_device_urls(api, out urls)
  ÔåÆ if empty: MessageBox error, Close()
tobii_device_create(api, urls[0], STORE_OR_TRANSFER_FALSE, out device)
  ÔåÆ if error: MessageBox error, Close()
```

### 2. Subscription
```
tobii_gaze_point_subscribe(device, callback, IntPtr.Zero)
```

Callback stores `(x, y, validity)` via `Interlocked.Exchange` on the background thread.

### 3. Poll Loop
```
Task.Run(() => {
    while (!token.IsCancellationRequested) {
        tobii_wait_for_callbacks(new[] { device })
        tobii_device_process_callbacks(device)
    }
})
```

### 4. UI Update (DispatcherTimer @ 60Hz)
1. Read latest raw (x, y, validity) via atomic operations
2. Feed (x, y) through OneEuroFilter ÔåÆ get filtered (fx, fy)
3. Convert filtered normalized coords to canvas pixels: `px = fx * canvas.ActualWidth`, `py = fy * canvas.ActualHeight`
4. Append to trail list, trim to 50
5. Clear canvas, redraw trail circles with opacity fade
6. Draw current gaze dot at (px, py)
7. Update debug TextBlock

### 5. Cleanup (OnClosing)
```
tobii_gaze_point_unsubscribe(device)
tobii_device_destroy(device)
tobii_api_destroy(api)
Cancel poll task
```

## Threading Model

- StreamEngine callback stores (x, y, validity) via `Interlocked.Exchange` on the background thread
- `DispatcherTimer` reads via `Interlocked.CompareExchange(ref field, 0, 0)` on the UI thread ÔÇö atomic 64-bit read
- No `Dispatcher.Invoke` from callbacks (avoids blocking StreamEngine thread)

## Error Handling

- No device found ÔåÆ MessageBox + close
- Device creation failure ÔåÆ MessageBox + close
- No reconnection logic (keep sample simple)

## Dependencies

The `.csproj` references Tobii SDK DLLs using the same conditional pattern as `EyeCtrl.Platform.csproj`:
- Default SDK path: `C:\Program Files\Tobii\Tobii EyeX\`
- Override with: `-p:TobiiSdkPath="..."`
- Checks for `Tobii.StreamEngine.Net.dll` (managed) and `tobii_stream_engine.dll` (native)
- If absent: build fails with a clear error message (acceptable for a sample)

Target framework: `net10.0-windows`, `UseWPF`.

## What This Sample Does NOT Demonstrate

- Reconnection / error recovery (see main EyeCtrl app)
- Gaze filtering beyond basic OneEuroFilter
- Fixation detection
- Cursor control
- DI / MVVM patterns
