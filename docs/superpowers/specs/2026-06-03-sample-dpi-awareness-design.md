# Sample DPI Awareness — Design

## Overview

Fix a DPI-awareness bug in the `StreamEngineGazeTracker` sample where the gaze-following dot is offset from where the user is actually looking, and make the visual elements (gaze dot, trail dots, debug overlay) physical-pixel-sized so the sample looks the same regardless of system display scaling.

## Context

The sample (`samples/StreamEngineGazeTracker/`) is a self-contained WPF demo of the Tobii StreamEngine API. It is **not** part of `EyeCtrl.slnx` and has zero project references to `EyeCtrl.Core`, `EyeCtrl.Platform`, or `EyeCtrl.ViewModels`. This design keeps it self-contained.

### Why the dot is offset (the bug)

Tobii StreamEngine's `tobii_gaze_point_t` is normalized 0..1 to the **physical screen** (the OS/driver level, not the WPF/DIP level). The current code in `OnUITick`:

```csharp
double canvasW = GazeCanvas.ActualWidth;   // in DIPs (e.g. 800 at 200% DPI)
px = fx * canvasW;                          // treats canvas as full physical screen
```

treats the Canvas as if it were the full physical screen, but the Canvas only covers the window's client area — which is offset from the window's outer (`Left`/`Top`) position by the title bar and borders. At non-100% DPI the mismatch is amplified:

- 100% DPI, 800×600 window centered on a 1920×1080 screen: at `fx=0.5` (screen center) the dot happens to land correctly because canvas center coincides with screen center. The error grows linearly with distance from the center, reaching ~80 DIPs (= 80 physical px) at the left/right edges of the screen.
- 200% DPI, same window: the window is 800 DIPs wide but 1600 physical px wide on the screen, with a 160-px physical margin on each side. The Canvas is 800 DIPs wide. `px = fx * 800` DIPs ≠ where the user is actually looking. The error at the edges grows to ~160 physical px (= 80 DIPs at 200% DPI) horizontally and ~titlebar-height vertical offset (the canvas's (0,0) is below the title bar, but the old math doesn't know that).

The main app avoids this by treating the gaze as physical pixels throughout the pipeline (`TobiiStreamEngineProvider.cs:108-110` → `DpiHelper.PhysicalToLogicalX` at the WPF boundary in each overlay window). The sample does not.

## Goals

1. Gaze dot lands at the user's actual gaze point at any DPI scaling.
2. Visual elements (gaze dot, trail dots, debug overlay text) are physical-pixel-sized, so the sample looks the same at 100%, 125%, 150%, 200% scaling.
3. Keep the sample self-contained — no new project references.

## Non-Goals

- **PerMonitorV2 awareness / cross-DPI monitor drag handling.** The sample has no `app.manifest` and no `ApplicationHighDpiMode` set in the `.csproj`, so it runs at WPF's **default System-DPI awareness**. On a cross-DPI monitor drag, Windows bitmap-scales the window (blurry) and WPF does not raise a DPI-change notification. The `GetScale` value is effectively constant for a given system config. The gaze math (`PointFromScreen` based) is correct *for the monitor where the window was first shown*; dragging to a different-DPI monitor requires a restart to pick up the new scale. Acceptable for a sample's primary scenario (single monitor, fixed window). Adding an `app.manifest` opting into PerMonitorV2 is a separate, optional improvement.
- Adding a test project for the sample. The DpiHelper is trivial; verification is visual.
- Refactoring the sample to use `EyeCtrl.Core.DpiHelper`. That would break the self-contained property.

## Design

### New file: `samples/StreamEngineGazeTracker/DpiHelper.cs`

Sample-local static class. Mirrors the role of `EyeCtrl.Core.Services.DpiHelper` (the main app's version) but lives in the sample project so no cross-project reference is needed.

```csharp
namespace StreamEngineGazeTracker;

using System.Windows;
using System.Windows.Media;

internal static class DpiHelper
{
    public static double GetScale(Visual visual)
    {
        var source = PresentationSource.FromVisual(visual);
        return source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
    }

    public static double PhysicalToLogical(Visual visual, double physicalPixels)
        => physicalPixels / GetScale(visual);
}
```

- `GetScale` returns the horizontal DPI scale (e.g. 2.0 at 200% scaling). Same source the main app uses (`src/EyeCtrl.App/App.xaml.cs:69-70`).
- Falls back to 1.0 if the visual is not yet connected to a `PresentationSource` (defensive — in practice the UI timer doesn't tick until after `Loaded`).
- `PhysicalToLogical` is a convenience for the visual-sizing use case.

### Modified: `MainWindow.xaml.cs`

**Constants get explicit unit suffixes** (the values themselves are unchanged — the meaning is just clearer now):

```csharp
// Before
private const double TrailDotSize = 6;
private const double GazeDotSize  = 16;

// After
private const double TrailDotPhysicalSize = 6;   // physical pixels
private const double GazeDotPhysicalSize  = 16;  // physical pixels
```

**`InitializeVisuals`** uses `DpiHelper.PhysicalToLogical` so the dot and trail sizes are physical-pixel-sized at the current DPI:

```csharp
double trailDip = DpiHelper.PhysicalToLogical(this, TrailDotPhysicalSize);
double gazeDip  = DpiHelper.PhysicalToLogical(this, GazeDotPhysicalSize);

for (int i = 0; i < _trailDots.Length; i++)
{
    _trailDots[i] = new System.Windows.Shapes.Ellipse
    {
        Width = trailDip,
        Height = trailDip,
        // ... rest unchanged
    };
}

_gazeDot = new System.Windows.Shapes.Ellipse
{
    Width = gazeDip,
    Height = gazeDip,
    // ... rest unchanged
};
```

**`OnUITick`** fixes the gaze→canvas math using WPF's `PointFromScreen`, which maps a screen point (physical pixels) to the visual's local DIPs in one call. This handles DPI scaling, window position, and window chrome (title bar / borders) correctly — manually subtracting `Window.Left`/`Top` would not, because the Canvas's (0,0) is at the *client area* (below the title bar, inside the borders), not at the window's outer position.

```csharp
// Before (buggy)
double canvasW = GazeCanvas.ActualWidth;
double canvasH = GazeCanvas.ActualHeight;
px = fx * canvasW;
py = fy * canvasH;

// After
double scale = DpiHelper.GetScale(this);

// StreamEngine's normalized 0..1 is relative to the physical screen.
// SystemParameters.* is in DIPs, so multiply by the DPI scale to get physical px.
double physX = fx * SystemParameters.PrimaryScreenWidth  * scale;
double physY = fy * SystemParameters.PrimaryScreenHeight * scale;

// PointFromScreen maps a screen point in physical pixels to the visual's
// local DIPs. Accounts for DPI, window position, and chrome.
Point canvasPt = GazeCanvas.PointFromScreen(new Point(physX, physY));
px = canvasPt.X;
py = canvasPt.Y;
```

The half-dot offset for centering also converts from physical pixels to DIPs:

```csharp
double halfGazeDotDip  = DpiHelper.PhysicalToLogical(this, GazeDotPhysicalSize  / 2.0);
double halfTrailDotDip = DpiHelper.PhysicalToLogical(this, TrailDotPhysicalSize / 2.0);

Canvas.SetLeft(_gazeDot,  px - halfGazeDotDip);
Canvas.SetTop(_gazeDot,   py - halfGazeDotDip);

// Trail dots:
Canvas.SetLeft(_trailDots[i], pt.X - halfTrailDotDip);
Canvas.SetTop(_trailDots[i],  pt.Y - halfTrailDotDip);
```

**Debug overlay font/padding** is set in code-behind in `OnLoaded` (so it can be physical-pixel-sized):

```csharp
// In OnLoaded, before InitializeStreamEngine():
DebugText.FontSize    = DpiHelper.PhysicalToLogical(this, 13);  // was FontSize="13" in XAML
DebugOverlay.Padding  = new Thickness(DpiHelper.PhysicalToLogical(this, 8));  // was Padding="8" in XAML
```

(Reasonable physical-pixel defaults: 13px font, 8px padding — matches the XAML values that exist today. If these look wrong at extreme DPI scales, the constants can be tweaked.)

### Modified: `MainWindow.xaml`

Remove the two attributes that are now set in code-behind:

```xml
<Border Canvas.Left="10" Canvas.Top="10"
        Panel.ZIndex="100"
        Background="#80000000"
        CornerRadius="4">
    <TextBlock x:Name="DebugText"
               FontFamily="Consolas"
               Foreground="White"
               Text="Connecting..." />
</Border>
```

(Renaming the Border to `DebugOverlay` so it can be referenced from code-behind, and dropping `FontSize="13"` and `Padding="8"`.)

## Error Handling

- **`PresentationSource` is null** (e.g., before the window is shown): `GetScale` returns 1.0. `PointFromScreen` returns `(NaN, NaN)` for a non-connected visual — the existing `if (rawValid)` guard in `OnUITick` would propagate NaN into the dot position. Defensive only; the UI timer doesn't fire until after `Loaded`, so `PresentationSource` is available.
- **Window dragged between monitors with different DPI** (PerMonitorV2): not handled. The sample is System-DPI-aware (default); see *Non-Goals*. A cross-DPI drag results in WPF bitmap-scaling the window and no DPI change event — gaze math and visual sizes retain the original monitor's values. A restart picks up the new monitor's DPI. Out of scope to fix.
- **Tobii stream produces invalid gaze** (blinks, tracking loss): unchanged — sample already skips trail/dot updates on invalid samples (`MainWindow.xaml.cs:249-250`).

## Data Flow (per-tick)

1. `OnUITick` reads `(rawX, rawY, rawValid)` from the shared struct (unchanged).
2. `OnUITick` calls `OneEuroFilter.Filter` on raw → filtered (unchanged).
3. **New:** `DpiHelper.GetScale(this)` returns the current window DPI scale.
4. **New:** `(px, py)` are computed in physical pixels first, then converted to DIPs relative to the window's client area.
5. **New:** Half-dot offsets are computed in DIPs.
6. Trail dot pool updated with new DIP positions (unchanged structure).
7. Gaze dot positioned at new DIP coords (unchanged structure).
8. Debug text updated (unchanged).

## Testing

The sample has no test project. The DpiHelper is ~10 lines of trivial math (one `Matrix.M11` read, one division). The real verification is **visual**:

1. `dotnet build samples/StreamEngineGazeTracker`
2. Run on a 100% DPI display: confirm the gaze dot lands where the user is looking.
3. Change Windows display scaling to 150% or 200%, rebuild & re-run: confirm the dot still lands accurately, and that the dot/trail/text are the same physical size as at 100%.
4. Sanity-check the debug overlay's `Raw: (x, y)` and `Screen: (px, py)` lines — they should match the on-screen position.

## Files Changed

| File | Change |
|------|--------|
| `samples/StreamEngineGazeTracker/DpiHelper.cs` | **New** — `GetScale(Visual)` + `PhysicalToLogical(Visual, double)` |
| `samples/StreamEngineGazeTracker/MainWindow.xaml.cs` | Rename size constants to `*PhysicalSize`; convert Ellipse sizes and gaze-position math via `DpiHelper`; set debug font/padding in code-behind |
| `samples/StreamEngineGazeTracker/MainWindow.xaml` | Remove `FontSize` and `Padding` from the debug overlay; rename `Border` to `DebugOverlay` for code-behind access |

## What Stays Unchanged

- Tobii SDK subscription, poll loop, callback, cleanup (all DPI-agnostic, already correct)
- OneEuroFilter (operates on normalized 0..1, DPI-independent)
- `OnLoaded` / `OnClosing` lifecycle and the `MessageBox` + `Close()` error path
- 60Hz `DispatcherTimer` and the 1-second rolling Hz window
- `MaxTrailPoints = 50` and the trail-position ring buffer
- The conditional `TOBII_AVAILABLE` SDK references in the `.csproj`
- The sample's "not part of `EyeCtrl.slnx`" property

## Known Limitations

- **System-DPI-aware, not PerMonitorV2.** Dragging the window to a different-DPI monitor does not trigger a DPI update; gaze math and visual sizes retain the original monitor's values. A restart picks up the new monitor's DPI. Adding an `app.manifest` for PerMonitorV2 is a separate, optional improvement.
- **Debug overlay text is physical-pixel-sized.** This is intentional (per the chosen design direction) so the sample "looks the same" across DPI scales, but it does mean the overlay text resists the OS's normal high-DPI text scaling. High-DPI users who rely on OS text scaling will see the debug overlay render smaller than other UI. The gaze dot and trail dots being physical-pixel-sized is clearly correct (consistent gaze target); the text being physical-pixel-sized is a deliberate trade-off and could be reverted to DIPs in a follow-up.
- **No tests.** Acceptable for a sample.
