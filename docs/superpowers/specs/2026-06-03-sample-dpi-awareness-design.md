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

treats the Canvas as if it were the full physical screen, but the Canvas only covers the window's client area. At non-100% DPI the mismatch is amplified:

- 100% DPI, 800×600 window on a 1920×1080 screen: gaze at the center of the physical screen (fx=0.5) goes to the center of the canvas (px=400), which happens to coincide with where the user is looking **only if** the window covers the full screen.
- 200% DPI: the same window is 800 DIPs wide but 1600 physical pixels wide on the screen. `canvasW = 800` DIPs; the gaze normalization is to 1920 physical pixels. The dot lands at `fx * 800` DIPs, but the user's actual gaze in DIPs (relative to the window) is `fx * 1920 / 2.0 = fx * 960` DIPs — i.e. off by 160 DIPs at the right edge of the window.

The main app avoids this by treating the gaze as physical pixels throughout the pipeline (`TobiiStreamEngineProvider.cs:108-110` → `DpiHelper.PhysicalToLogicalX` at the WPF boundary in each overlay window). The sample does not.

## Goals

1. Gaze dot lands at the user's actual gaze point at any DPI scaling.
2. Visual elements (gaze dot, trail dots, debug overlay text) are physical-pixel-sized, so the sample looks the same at 100%, 125%, 150%, 200% scaling.
3. Keep the sample self-contained — no new project references.

## Non-Goals

- Live visual-size updates when dragging the window between monitors with different DPI (PerMonitorV2). The gaze math is correct across monitors; the visual sizes are set once in `InitializeVisuals`. A monitor swap would require a restart to pick up the new DPI for sizes. This is acceptable for a sample and documented below.
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

**`OnUITick`** fixes the gaze→canvas math:

```csharp
// Before (buggy)
double canvasW = GazeCanvas.ActualWidth;
double canvasH = GazeCanvas.ActualHeight;
px = fx * canvasW;
py = fy * canvasH;

// After
double scale = DpiHelper.GetScale(this);

// StreamEngine's normalized 0..1 is relative to the physical screen.
double physScreenW = SystemParameters.PrimaryScreenWidth * scale;
double physScreenH = SystemParameters.PrimaryScreenHeight * scale;
double physX = fx * physScreenW;
double physY = fy * physScreenH;

// Convert physical → DIPs, then offset to the window's client area.
px = physX / scale - Left;
py = physY / scale - Top;
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

- **`PresentationSource` is null** (e.g., before the window is shown): `GetScale` returns 1.0. Behavior degrades to the old buggy mapping, but the UI timer doesn't fire until after `Loaded`, so this is unreachable in practice. Defensive only.
- **Window dragged between monitors with different DPI** (PerMonitorV2): `GetScale` is re-evaluated each tick, so the gaze math stays correct after a drag. The Ellipse `Width`/`Height` and the font/padding are set once (in `InitializeVisuals` and `OnLoaded`); they retain the original monitor's DPI. A restart picks up the new monitor's DPI. Out of scope to fix.
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

- **Visual sizes don't update on PerMonitorV2 drag.** Gaze math does, but Ellipse sizes and font are set once. A drag to a different-DPI monitor leaves the visuals at the original monitor's size until restart.
- **No tests.** Acceptable for a sample.
- **Window-position-dependent.** If the user moves the window to a secondary monitor with different DPI, both the gaze math and visual sizing would need a restart. Same as above.
