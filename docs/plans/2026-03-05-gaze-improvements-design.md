# Gaze Improvements Design

**Date:** 2026-03-05

## Overview

Two improvements to the gaze processing pipeline:

1. **Gap interpolation** — suppress fixation/dwell accumulation during blinks
2. **I-VT fixation centroid tracking** — accumulate a velocity-filtered mean gaze position during fixations to improve click accuracy

---

## Feature 1: GapInterpolator

### Problem

During blinks, the Tobii SDK emits invalid samples. All three filters (`DirectFilter`, `SmoothedFilter`, `OneEuroFilter`) currently hold the last valid position and pass it downstream. This causes `FixationDetector` and `DwellDetector` to keep accumulating on the stale position during blinks, leading to phantom dwell activation.

### Design

**New class:** `EyeCtrl.Core/Processing/GapInterpolator.cs`

Wraps an `IGazeFilter`. Detects blink-length gaps and signals whether downstream components should suppress their updates.

**Interface:**
```csharp
public sealed class GapInterpolator
{
    public GapInterpolator(IGazeFilter filter, CursorSettings settings);
    public (ScreenPoint Position, bool IsInGap) Process(GazePoint rawPoint);
    public void Reset();
}
```

**Logic:**
- Valid sample → pass through to wrapped filter, record `_lastValidTimestamp`, return `(filtered, false)`
- Invalid sample:
  - `gapDuration = now - _lastValidTimestamp`
  - If `gapDuration < MaxGapMicroseconds` → return `(heldPosition, true)` — cursor holds, fixation/dwell suppressed
  - If `gapDuration >= MaxGapMicroseconds` → return `(heldPosition, false)` and reset filter state — gap too long, treat as new sequence
- First sample ever (no prior valid) → pass through, `IsInGap = false`

**GazeEngine change:**

Replace `_filter.Filter(point)` call with `_gapInterpolator.Process(point)`:

```csharp
var (screenPoint, isInGap) = _gapInterpolator.Process(gazePoint);
CursorMoved?.Invoke(screenPoint);            // always update cursor
if (!isInGap)
{
    _fixationDetector.Update(screenPoint, gazePoint.TimestampMicroseconds);
    _dwellDetector.Update(screenPoint, gazePoint.TimestampMicroseconds);
}
```

`GapInterpolator` is recreated when `FilterMode` changes (same lifecycle as the current filter recreation).

**New setting:**

```csharp
// CursorSettings.cs
public int MaxGapInterpolationMs { get; set; } = 150;
```

---

## Feature 2: FixationCentroidTracker

### Problem

`FixationDetector` reports the center of the initial fixation radius as the fixation point. This drifts toward wherever gaze wanders within the radius and includes saccadic samples, causing clicks to land off-target.

### Design

**New class:** `EyeCtrl.Core/Processing/FixationCentroidTracker.cs`

Runs in parallel with `FixationDetector`. During an active fixation, accumulates a running mean of only low-velocity gaze samples (Welford online algorithm). Exposes `StableCentroid` as the best estimate of where the user is actually looking.

**Interface:**
```csharp
public sealed class FixationCentroidTracker
{
    public FixationCentroidTracker(DwellSettings settings);
    public void StartTracking();
    public void StopTracking();
    public void Update(ScreenPoint filteredPoint, long timestampMicroseconds);
    public ScreenPoint? StableCentroid { get; }
}
```

**Logic:**
- Only accumulates when `StartTracking()` has been called
- Computes velocity: `speed = sqrt(dx² + dy²) / dtSeconds` (px/s)
- Sample speed ≤ `VelocityThresholdPxPerSec` → include in Welford running mean
- Sample speed > threshold → skip (saccade), do not reset the mean
- `StableCentroid` returns current mean, or `null` if fewer than 2 stable samples accumulated
- `StopTracking()` clears all state

**Welford running mean** (numerically stable):
```
n++
delta = value - mean
mean += delta / n
```

Applied independently to X and Y.

**GazeEngine changes:**

```csharp
// Construction
_centroidTracker = new FixationCentroidTracker(_settings.Dwell);

// Wire fixation events
_fixationDetector.FixationStarted += pt =>
{
    _centroidTracker.StartTracking();
    // existing handling...
};
_fixationDetector.FixationEnded += () =>
{
    _centroidTracker.StopTracking();
    // existing handling...
};

// Feed each filtered point
_centroidTracker.Update(screenPoint, gazePoint.TimestampMicroseconds);

// Expose stable fixation point
public ScreenPoint? StableFixationPoint =>
    _centroidTracker.StableCentroid ?? _fixationDetector.FixationPoint;
```

**InteractionManager change:**

When executing a click or other positional action, read `GazeEngine.StableFixationPoint` instead of the `ScreenPoint` captured from `FixationStarted`.

**New setting:**

```csharp
// DwellSettings.cs
public double VelocityThresholdPxPerSec { get; set; } = 100.0;
```

---

## Settings Summary

| Setting | Type | Default | Location |
|---|---|---|---|
| `MaxGapInterpolationMs` | `int` | `150` | `CursorSettings` |
| `VelocityThresholdPxPerSec` | `double` | `100.0` | `DwellSettings` |

---

## Files Changed

| File | Change |
|---|---|
| `EyeCtrl.Core/Processing/GapInterpolator.cs` | New |
| `EyeCtrl.Core/Processing/FixationCentroidTracker.cs` | New |
| `EyeCtrl.Core/GazeEngine.cs` | Use GapInterpolator; feed centroid tracker; expose StableFixationPoint |
| `EyeCtrl.Core/Settings/CursorSettings.cs` | Add MaxGapInterpolationMs |
| `EyeCtrl.Core/Settings/DwellSettings.cs` | Add VelocityThresholdPxPerSec |
| `EyeCtrl.Core/Processing/InteractionManager.cs` | Use StableFixationPoint for actions |
| `tests/EyeCtrl.Core.Tests/Processing/GapInterpolatorTests.cs` | New |
| `tests/EyeCtrl.Core.Tests/Processing/FixationCentroidTrackerTests.cs` | New |
