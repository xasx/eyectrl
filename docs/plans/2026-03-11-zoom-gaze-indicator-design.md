# Zoom Gaze Indicator Design

**Date:** 2026-03-11
**Status:** Approved

## Problem

In zoom view, there is no gaze indicator — the cursor circle is hidden when zoom opens. The user has no visual feedback about where they're looking within the magnified area, and no indication of fixation progress.

## Solution

A dedicated overlay window showing a shrinking-circle indicator inside the zoom view.

## Visual Design

Two concentric circles, centered at the gaze point in magnified coordinates:

- **Outer circle:** ~30px diameter, 2px stroke, semi-transparent lime green. Shrinks during fixation.
- **Center dot:** ~10px diameter, filled lime green. Always stays the same size — the convergence target.

All sizes are configurable for iterative tuning.

### States

| State              | Outer circle                          | Center dot | Position                     |
|--------------------|---------------------------------------|------------|------------------------------|
| Grace period       | 30px, static                          | 10px       | Tracks gaze in magnified coords |
| Fixation tracking  | Shrinking 30px → 10px (ease-in)       | 10px       | Stays at fixation center     |
| Fixation aborted   | Expanding back to 30px (~100ms)       | 10px       | Resumes tracking gaze        |
| Fixation complete  | N/A — action menu takes over          | N/A        | Hidden                       |

## Animation

- **Shrink duration:** Matches `FixationDetectionMs` setting (e.g., 800ms).
- **Shrink easing:** Ease-in (quadratic/cubic) — starts slow, accelerates. "Building momentum" feel.
- **Shrink range:** Outer circle diameter interpolates from 30px → 10px (center dot size).
- **Reset on abort:** Quick expand back to 30px over ~100ms, linear ease.
- **Fixation progress:** 0.0 → 1.0 value derived from elapsed fixation time / total fixation time. ViewModel applies ease-in curve to map progress to diameter.

## Architecture

### Approach: Separate overlay window

Consistent with existing `CursorCircleWindow` pattern. Independent window, easy to show/hide, no coupling with zoom overlay internals.

### New Files

- `ZoomGazeIndicatorWindow.xaml/.cs` — overlay window (two ellipses on a Canvas)
- `ZoomGazeIndicatorViewModel.cs` — drives position, outer circle size, animation state

### ViewModel Properties

- `IndicatorX`, `IndicatorY` — screen position (magnified coordinates)
- `OuterCircleDiameter` — animated 30 → 10 during fixation
- `IsVisible` — show/hide
- `FixationProgress` (0.0 → 1.0) — drives shrink with ease-in curve applied

### Coordinate Mapping

Gaze arrives in screen space. Convert to magnified coordinates:

```
magnifiedPoint = zoomCenter + (gazePoint - originalCenter) * magnification
```

This is the inverse of the existing `GetRealScreenPoint()` which maps magnified → real.

### Integration Flow (App.xaml.cs)

1. **Zoom starts** → Show indicator, begin tracking gaze in magnified coords
2. **Gaze samples arrive** → Convert to magnified coords, update indicator position
3. **Grace period ends, FixationDetector starts** → Begin shrink animation
4. **Fixation aborted** → Reset animation (~100ms expand), resume gaze tracking
5. **Fixation completes** → Hide indicator (action menu takes over)
6. **Zoom closes** → Hide indicator

### FixationDetector Changes

Expose fixation progress: the detector already tracks elapsed time internally. Add a way to query or receive elapsed/total ratio so the ViewModel can drive the animation.

### Communication

- Gaze position: existing `GazeCursorMovedMessage`
- Fixation progress: new lightweight callback or message from the zoom fixation detector in App.xaml.cs
