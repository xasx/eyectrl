# EyeCtrl Bugfixes & Polish — Design Document

**Date:** 2026-03-01

## Issues

1. Multiple settings/keyboard buttons trigger simultaneously during dwell
2. Cursor circle stays visible in pause mode
3. Cursor stops short of target position (filter undershoot)
4. Keyboard overlay steals focus from other applications
5. All controls should be slightly larger

## Fix 1: DwellDetector — Nearest Target Only

**Problem:** `DwellDetector.Update()` accumulates dwell on ALL targets whose
circular hit area contains the gaze point. Overlapping hit circles cause multiple
buttons to show progress and potentially activate simultaneously.

**Solution:** Before the accumulation loop, find the single nearest target within
hit range. Only that target accumulates dwell; all others reset.

```
Update(gazePoint):
  1. Find all targets where HitTest(gazePoint) == true
  2. Of those, pick the one closest to center (min distance)
  3. Only that target accumulates dwell time and fires progress
  4. All other targets: reset IsGazeInside + emit progress(0)
```

Affects all DwellDetector instances (settings tabs, controls, pagination, modal,
toolbar, action menu, activation button). All benefit.

## Fix 2: Hide Cursor Circle on Pause

**Problem:** `CursorMoved` event fires unconditionally (even when paused), so
the cursor circle keeps following gaze in pause mode.

**Solution:** In `App.xaml.cs`, when `StateChanged` fires:
- `Paused` → `_cursorCircle.Hide()`
- `FreeLook` → `_cursorCircle.Show()`

## Fix 3: OneEuroFilter for Gaze Smoothing

**Problem:** The current `SmoothedFilter` (exponential moving average, alpha=0.5)
causes consistent undershoot. It blends 50% new gaze with 50% previous every
frame, so the cursor never quite reaches the target—especially noticeable with
small calibration offsets.

**Solution:** Replace with OneEuroFilter (Casiez et al., 2012), an adaptive
low-pass filter that adjusts cutoff frequency based on movement speed:
- Fixating (low speed) → high smoothing → no jitter
- Saccade (high speed) → low smoothing → fast response, reaches target

**New class:** `OneEuroFilter : IGazeFilter` in `EyeCtrl.Core/Processing/`

**Default parameters:**
- `MinCutoff = 1.0` (smoothness at rest)
- `Beta = 0.07` (speed responsiveness)
- `DerivativeCutoff = 1.0` (derivative smoothing)

**Settings integration:**
- New `GazeFilterMode.OneEuro` enum value (becomes default)
- `MinCutoff` and `Beta` properties added to `CursorSettings`
- `GazeFilterFactory` creates `OneEuroFilter` for the new mode
- Old `Smoothed` and `Direct` modes preserved

No UI sliders for filter params (YAGNI).

## Fix 4: Keyboard WS_EX_NOACTIVATE

**Problem:** `KeyboardWindow` uses `WS_EX_TOOLWINDOW` but not `WS_EX_NOACTIVATE`.
When WPF renders the window, it steals focus from the target application.
`SendInput` then sends keystrokes to the keyboard overlay instead of the
intended app.

**Solution:** Add `WS_EX_NOACTIVATE` (0x08000000) to `OnSourceInitialized()`:
```csharp
extStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
```

Apply to all overlay windows for consistency:
- KeyboardWindow
- ActionMenuWindow
- ToolbarWindow
- ActivationButtonWindow
- ZoomOverlayWindow
- SettingsWindow

`SendInput` injects into the system input queue, so keystrokes go to whichever
window has focus — with NOACTIVATE, focus stays on the target app (correct).

## Fix 5: Increase Control Sizes ~25%

### Settings window

| Element | Current | New |
|---------|---------|-----|
| Tab buttons | 80x80, r=70 | 100x100, r=85 |
| Control buttons | 80x64, r=40 | 100x80, r=50 |
| Pagination | 80x64, r=50 | 100x80, r=60 |
| Modal buttons | 140x64, r=60 | 170x80, r=70 |
| Color swatches | 72x60, r=30 | 90x75, r=38 |
| Sliders | 80x64 | 100x80 |

### Keyboard

| Element | Current | New |
|---------|---------|-----|
| Base key size | 56px | 70px |
| Prediction targets | r=40 | r=50 |
