# Sample DPI Awareness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the gaze-dot offset bug in the `StreamEngineGazeTracker` sample so the dot lands where the user is actually looking, and make the visual elements (gaze dot, trail dots, debug overlay) physical-pixel-sized so the sample looks the same at any DPI scaling.

**Architecture:** Add a sample-local `DpiHelper` (~10 lines) for DPI scale queries and physical→DIP conversion. Use WPF's `UIElement.PointFromScreen` for the gaze→canvas mapping (handles DPI, window position, and window chrome in one call). Convert Ellipse sizes and debug-overlay font/padding from DIPs to physical pixels via `DpiHelper`. Keep the sample self-contained — no project references to `EyeCtrl.Core`.

**Tech Stack:** C# / .NET 10 / WPF / Tobii StreamEngine. No new dependencies.

**Note on testing:** The sample has no test project (the spec calls this out: "No tests. Acceptable for a sample."). Per-task verification is `dotnet build` plus a final visual verification at 100% and 200% DPI. TDD does not apply to this work.

**Reference spec:** `docs/superpowers/specs/2026-06-03-sample-dpi-awareness-design.md`

**Build prerequisite — Tobii SDK:** Every `dotnet build samples/StreamEngineGazeTracker` in this plan requires the Tobii StreamEngine SDK on disk. The `.csproj` has a hard `ErrorWhenNoTobiiSdk` target that fails the build with a clear error if either `Tobii.StreamEngine.Net.dll` or `tobii_stream_engine.dll` is missing from `C:\Program Files\Tobii\Tobii EyeX\`. Default path; override with `-p:TobiiSdkPath="path/to/sdk"`. On a non-Tobii dev machine, the SDK error is expected — compilation cannot proceed without the SDK.

**Confirmation of physical-pixel-constant text (Task 3):** Per the spec's Goal 2 (debug overlay text included) and the user's explicit choice during brainstorming, the debug overlay's font and padding are sized in physical pixels. This means the overlay text deliberately resists the OS's normal high-DPI text scaling — at 200% DPI, the overlay renders ~13 physical px (≈ 6.5 DIPs) rather than ~26 DIPs. The gaze dot and trail being physical-pixel-sized is clearly correct (consistent gaze target); the text being physical-pixel-sized is a deliberate, conscious trade-off documented in the spec's *Known Limitations*. Reverting just the text to DIPs is a one-line follow-up if the user later prefers it.

---

## File Structure

| File | Responsibility | Change |
|------|----------------|--------|
| `samples/StreamEngineGazeTracker/DpiHelper.cs` | Query DPI scale; convert physical pixels → DIPs | **New** |
| `samples/StreamEngineGazeTracker/MainWindow.xaml.cs` | Use `DpiHelper` for gaze math, Ellipse sizing, font/padding; rename size constants | Modify |
| `samples/StreamEngineGazeTracker/MainWindow.xaml` | Drop `FontSize`/`Padding` (now set in code); rename `Border` → `DebugOverlay` for code-behind access | Modify |

---

## Task 1: Create the sample-local `DpiHelper`

**Files:**
- Create: `samples/StreamEngineGazeTracker/DpiHelper.cs`

- [ ] **Step 1: Create the file with the static class**

Create `samples/StreamEngineGazeTracker/DpiHelper.cs` with this exact content:

```csharp
namespace StreamEngineGazeTracker;

using System.Windows;
using System.Windows.Media;

/// <summary>
/// Sample-local stand-in for <c>EyeCtrl.Core.Services.DpiHelper</c>.
/// The sample is intentionally self-contained, so the main app's
/// <c>DpiHelper</c> cannot be referenced. This class exposes the same
/// two operations needed here: query the current DPI scale of a
/// visual's window, and convert physical screen pixels to WPF DIPs.
/// Re-query per-tick to handle changes if/when the sample opts into
/// PerMonitorV2 awareness in the future.
/// </summary>
internal static class DpiHelper
{
    /// <summary>
    /// Returns the horizontal DPI scale of the given visual's window
    /// (e.g. 2.0 at 200% scaling). Falls back to 1.0 if the visual
    /// is not yet connected to a <see cref="PresentationSource"/>.
    /// </summary>
    public static double GetScale(Visual visual)
    {
        var source = PresentationSource.FromVisual(visual);
        return source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
    }

    /// <summary>
    /// Converts physical screen pixels to WPF device-independent
    /// pixels (DIPs) at the given visual's current DPI scale.
    /// </summary>
    public static double PhysicalToLogical(Visual visual, double physicalPixels)
        => physicalPixels / GetScale(visual);
}
```

- [ ] **Step 2: Build to confirm it compiles**

Run from the repo root:

```bash
dotnet build samples/StreamEngineGazeTracker
```

Expected: build succeeds. `DpiHelper` is currently unreferenced (no caller yet), so this only verifies the file is well-formed C# that compiles in this project.

- [ ] **Step 3: Commit**

```bash
git add samples/StreamEngineGazeTracker/DpiHelper.cs
git commit -m "feat: add sample-local DpiHelper for DPI scale queries"
```

---

## Task 2: Apply `DpiHelper` throughout `MainWindow.xaml.cs` (rename, sizing, gaze math)

**Files:**
- Modify: `samples/StreamEngineGazeTracker/MainWindow.xaml.cs`

This is the bulk of the work. The size constants are renamed to make their units explicit (physical pixels), and four call sites are updated: `InitializeVisuals` (Ellipse sizes), the gaze→canvas math in `OnUITick` (the actual bug fix), and the half-dot offset calculations in `OnUITick`. **All four edits must land in the same commit** — the constants are referenced in five places, so a partial rename leaves the tree non-compiling. Each step below shows one of the four edits; do all four, then build, then commit.

- [ ] **Step 1: Rename the two size constants**

In `samples/StreamEngineGazeTracker/MainWindow.xaml.cs`, find the constants block near line 41-43. Replace:

```csharp
    // ── Trail ─────────────────────────────────────────────────────────
    private const int MaxTrailPoints = 50;
    private const double TrailDotSize = 6;
    private const double GazeDotSize = 16;
```

with:

```csharp
    // ── Trail ─────────────────────────────────────────────────────────
    private const int MaxTrailPoints = 50;
    // Trail/gaze dot sizes are physical pixels — converted to DIPs at
    // runtime via DpiHelper so they look the same at any DPI scale.
    private const double TrailDotPhysicalSize = 6;
    private const double GazeDotPhysicalSize  = 16;
```

- [ ] **Step 2: Update `InitializeVisuals` to size Ellipses via `DpiHelper`**

Find `InitializeVisuals` (around line 303-331) and replace the body so the trail-dots loop and the gaze-dot creation use the new constants via `DpiHelper.PhysicalToLogical`.

Replace this block:

```csharp
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
```

with:

```csharp
    private void InitializeVisuals()
    {
        // Convert physical-pixel sizes to DIPs at the current DPI so the
        // dots look the same regardless of system display scaling.
        double trailDip = DpiHelper.PhysicalToLogical(this, TrailDotPhysicalSize);
        double gazeDip  = DpiHelper.PhysicalToLogical(this, GazeDotPhysicalSize);

        // Create trail dot pool
        _trailDots = new System.Windows.Shapes.Ellipse[MaxTrailPoints];
        for (int i = 0; i < _trailDots.Length; i++)
        {
            _trailDots[i] = new System.Windows.Shapes.Ellipse
            {
                Width = trailDip,
                Height = trailDip,
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
            Width = gazeDip,
            Height = gazeDip,
            Fill = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(
                    (byte)GazeDotColorR, (byte)GazeDotColorG, (byte)GazeDotColorB))
        };
        GazeCanvas.Children.Add(_gazeDot);
    }
```

- [ ] **Step 3: Fix the gaze→canvas math in `OnUITick`**

The old math (`px = fx * canvasW`) treats the Canvas as if it were the full physical screen. The new math uses WPF's `UIElement.PointFromScreen`, which maps a screen point in physical pixels to the visual's local DIPs in one call — handling DPI, window position, and window chrome (title bar / borders) correctly.

Find the block in `OnUITick` (around line 254-258) that currently reads:

```csharp
            // 5. Convert normalized coords to canvas pixels
            double canvasW = GazeCanvas.ActualWidth;
            double canvasH = GazeCanvas.ActualHeight;
            px = fx * canvasW;
            py = fy * canvasH;
```

Replace it with:

```csharp
            // 5. Convert normalized (0..1) gaze coords to canvas-local DIPs.
            //    StreamEngine's normalization is to the physical screen.
            //    SystemParameters.* is in DIPs, so multiply by the DPI
            //    scale to get physical pixels. PointFromScreen then maps
            //    that screen point into the canvas's local DIP space,
            //    correctly handling DPI, window position, and window
            //    chrome (title bar / borders).
            double scale = DpiHelper.GetScale(this);
            double physX = fx * SystemParameters.PrimaryScreenWidth  * scale;
            double physY = fy * SystemParameters.PrimaryScreenHeight * scale;
            Point canvasPt = GazeCanvas.PointFromScreen(new Point(physX, physY));
            px = canvasPt.X;
            py = canvasPt.Y;
```

- [ ] **Step 4: Update the half-dot offset calculations in `OnUITick`**

The existing code subtracts `GazeDotSize / 2` and `TrailDotSize / 2` (the OLD constant names — now renamed) to center the dot on the gaze point. These need to use the new physical-pixel names, converted to DIPs.

Find the block in `OnUITick` (around line 260-285) that updates the trail dots and gaze dot:

```csharp
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
```

Replace the `Canvas.SetLeft`/`SetTop` lines (just those four lines, plus add a hoisted `halfTrailDotDip` and a `halfGazeDotDip`) with:

```csharp
            // 6. Update trail
            _trailPoints.Add(new Point(px, py));
            while (_trailPoints.Count > MaxTrailPoints)
                _trailPoints.RemoveAt(0);

            // 7. Update trail dots (pool of Ellipses)
            double halfTrailDotDip = DpiHelper.PhysicalToLogical(this, TrailDotPhysicalSize / 2.0);
            for (int i = 0; i < _trailDots.Length; i++)
            {
                if (i < _trailPoints.Count)
                {
                    var pt = _trailPoints[i];
                    double opacity = (double)(i + 1) / _trailPoints.Count;
                    Canvas.SetLeft(_trailDots[i], pt.X - halfTrailDotDip);
                    Canvas.SetTop(_trailDots[i], pt.Y - halfTrailDotDip);
                    _trailDots[i].Opacity = opacity * 0.7;
                    _trailDots[i].Visibility = Visibility.Visible;
                }
                else
                {
                    _trailDots[i].Visibility = Visibility.Collapsed;
                }
            }

            // 8. Update gaze dot
            double halfGazeDotDip = DpiHelper.PhysicalToLogical(this, GazeDotPhysicalSize / 2.0);
            Canvas.SetLeft(_gazeDot, px - halfGazeDotDip);
            Canvas.SetTop(_gazeDot, py - halfGazeDotDip);
```

(`halfTrailDotDip` is hoisted out of the loop since it doesn't change per iteration.)

- [ ] **Step 5: Build — must succeed**

```bash
dotnet build samples/StreamEngineGazeTracker
```

Expected: build succeeds. This verifies the constant rename, the `DpiHelper` call sites in `InitializeVisuals` and `OnUITick`, the `PointFromScreen` call, and the `Point` reference (already covered by the existing `using System.Windows;`) all compile together. If the build fails, the most likely cause is a missed `TrailDotSize` / `GazeDotSize` reference — search for those old names and update them.

- [ ] **Step 6: Commit — all four edits atomically**

```bash
git add samples/StreamEngineGazeTracker/MainWindow.xaml.cs
git commit -m "fix: apply DpiHelper throughout sample (gaze math, sizing, constants)"
```

---

## Task 3: Make the debug overlay's font and padding physical-pixel-sized

**Files:**
- Modify: `samples/StreamEngineGazeTracker/MainWindow.xaml.cs`
- Modify: `samples/StreamEngineGazeTracker/MainWindow.xaml`

The debug overlay's `FontSize` and `Padding` are currently set as `FontSize="13"` and `Padding="8"` in XAML. To size them in physical pixels, the values need to be set in code where the current DPI scale is available. The `Border` is renamed to `DebugOverlay` so it can be referenced from code-behind.

- [ ] **Step 1: Update `MainWindow.xaml`**

Replace the full contents of `samples/StreamEngineGazeTracker/MainWindow.xaml` with:

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

        <!-- Debug overlay (high ZIndex so dots never draw over it).
             FontSize and Padding are set in code-behind so they can be
             physical-pixel-sized at the current DPI. -->
        <Border x:Name="DebugOverlay"
                Canvas.Left="10" Canvas.Top="10"
                Panel.ZIndex="100"
                Background="#80000000"
                CornerRadius="4">
            <TextBlock x:Name="DebugText"
                       FontFamily="Consolas"
                       Foreground="White"
                       Text="Connecting..." />
        </Border>

    </Canvas>
</Window>
```

- [ ] **Step 2: Set the font and padding in `OnLoaded`**

Find the `OnLoaded` method (around line 69-87):

```csharp
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
```

Replace it with:

```csharp
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Size debug overlay in physical pixels so it looks the same
        // regardless of system DPI scaling.
        DebugText.FontSize   = DpiHelper.PhysicalToLogical(this, 13);
        DebugOverlay.Padding = new Thickness(DpiHelper.PhysicalToLogical(this, 8));

        try
        {
            InitializeStreamEngine();
            SubscribeGaze();
            StartPollLoop();
            StartUITimer();
            _uptime.Start();
        }
```

(The rest of `OnLoaded` — the catch block — is unchanged.)

- [ ] **Step 3: Build**

```bash
dotnet build samples/StreamEngineGazeTracker
```

Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add samples/StreamEngineGazeTracker/MainWindow.xaml samples/StreamEngineGazeTracker/MainWindow.xaml.cs
git commit -m "fix: set sample debug overlay font/padding in code-behind for DPI sizing"
```

---

## Task 4: Visual verification

**Files:** none (read-only inspection)

- [ ] **Step 1: Build the solution to confirm no regressions**

```bash
dotnet build EyeCtrl.slnx
```

Expected: build succeeds. (The sample isn't part of the solution, but confirming the main app still builds catches accidental cross-project side effects. Should be a no-op since no shared files were touched.)

- [ ] **Step 2: Run at 100% DPI**

1. Set Windows display scaling to 100% (Settings → Display → Scale).
2. From the repo root:
   ```bash
   dotnet run --project samples/StreamEngineGazeTracker
   ```
3. The Tobii SDK is required at `C:\Program Files\Tobii\Tobii EyeX\` (override with `-p:TobiiSdkPath="..."`). If absent, the build in Step 1 already verified compilation; the run will fail to start due to the SDK check in the `.csproj` — that's expected on a non-Tobii dev machine.
4. **Where a Tobii rig is available:** look at the canvas. The gaze dot should track where you're looking. Drag the window around — the dot should still track. The debug overlay text should be readable at 13 physical px.

- [ ] **Step 3: Run at 200% DPI**

1. Set Windows display scaling to 200%, sign out and back in (or just re-run — at System-DPI awareness, the new scale takes effect on next launch).
2. Re-run:
   ```bash
   dotnet run --project samples/StreamEngineGazeTracker
   ```
3. **Where a Tobii rig is available:** the gaze dot should still land where you're looking (the bug fix). The gaze dot and trail dots should be the **same physical size** as at 100% (16 px and 6 px respectively, not 32 px and 12 px). The debug overlay text should also be the same physical size (13 px).
4. **Where no Tobii rig is available:** this step is a no-op for you. The 100% run already verified the math compiles and the windows open. The DPI scaling assertion is documented for the user to run on a Tobii-equipped machine.

- [ ] **Step 4: No commit (verification only)**

Verification step — nothing to commit. If anything looks wrong, fix the offending task's code and rebuild.

---

## Summary

| File | Lines changed | Purpose |
|------|---------------|---------|
| `samples/StreamEngineGazeTracker/DpiHelper.cs` | +18 (new) | DPI scale queries + physical→DIP conversion |
| `samples/StreamEngineGazeTracker/MainWindow.xaml.cs` | ~20 lines changed across 5 locations | Constants renamed, `InitializeVisuals` sizing, gaze math, half-dot offsets, font/padding |
| `samples/StreamEngineGazeTracker/MainWindow.xaml` | 1 attribute removed (`FontSize`), 1 removed (`Padding`), 1 added (`x:Name` on Border) | Allow code-behind to set physical-pixel-sized text |

Four small commits:

1. **feat:** add sample-local `DpiHelper` (Task 1) — DpiHelper.cs is created and compiles
2. **fix:** apply `DpiHelper` throughout sample — gaze math, sizing, constants (Task 2) — the four edits in `MainWindow.xaml.cs` land atomically because the constant rename and all five call sites must move together
3. **fix:** set sample debug overlay font/padding in code-behind for DPI sizing (Task 3) — XAML + `OnLoaded` change
4. (no commit) — visual verification (Task 4) — build + run at 100% / 200% DPI; nothing to commit unless a fix is needed

Build at the end of every code-change task; visual verification at the end.

## What stays unchanged

- Tobii SDK subscription, poll loop, callback, cleanup
- OneEuroFilter (operates on normalized 0..1, DPI-independent)
- `OnLoaded`'s `try`/`catch` for the `MessageBox` + `Close()` error path (only the `DebugText`/`DebugOverlay` setup was added before it)
- 60Hz `DispatcherTimer` and the 1-second rolling Hz window
- `MaxTrailPoints = 50` and the trail-position ring buffer
- The conditional Tobii SDK references in the `.csproj`
- The sample's "not part of `EyeCtrl.slnx`" property
