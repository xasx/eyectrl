# Fix Zoom Mode Wrong Position

**Date:** 2026-03-07

## Problem

Actions execute twice — `GazeEngine.OnActionRequested` (direct) and `App.xaml.cs` (via `ActionRequestedMessage`). In zoom mode, `GazeEngine` uses `_centroidTracker.StableCentroid` which holds the raw screen-gaze position on the zoom overlay window, not the unmagnified real coordinate from `GetRealScreenPoint()`. This causes clicks to land on/near the zoom overlay itself instead of the intended target.

## Root Cause

`GazeEngine.OnActionRequested` subscribes directly to `InteractionManager.ActionRequested` and fires the action using `_centroidTracker.StableCentroid ?? point`. During zoom mode, the centroid tracker accumulates gaze samples in screen-space (on the zoom overlay), which is the wrong coordinate space for the action target.

Meanwhile, `GazeEngineAdapter` also subscribes to the same event, sends `ActionRequestedMessage` with the correct `ZoomFixationPoint` (output of `GetRealScreenPoint`), and `App.xaml.cs` executes the action again at the correct position.

Result: every action fires twice, and in zoom mode the first execution hits the wrong location.

## Approach: Remove Action Execution from GazeEngine

### Changes

**1. `GazeEngine.cs`**
- Remove `InteractionManager.ActionRequested += OnActionRequested;`
- Remove the `OnActionRequested` method
- Expose centroid: `public ScreenPoint? StableCentroid => _centroidTracker.StableCentroid;`

**2. `InteractionMessages.cs`**
- Add `bool IsFromZoom` property to `ActionRequestedMessage`

**3. `GazeEngineAdapter.cs`**
- Set `IsFromZoom = true` on `ActionRequestedMessage` when `InteractionManager.CurrentState == ZoomActionMenuVisible` at send time

**4. `App.xaml.cs`**
- In `ActionRequestedMessage` handler: use `gazeEngine.StableCentroid ?? msg.Point` for normal actions, `msg.Point` for zoom actions (when `msg.IsFromZoom`)

### Unchanged
- `InteractionManager` state machine
- `ZoomOverlayWindow.GetRealScreenPoint()`
- Zoom fixation detection wiring in `App.xaml.cs`
- `GazeEngineAdapter` message sending (still bridges Core events to MVVM messages)

### Testing
- Existing `InteractionManagerTests` pass (state machine unchanged)
- Manual: normal click lands correctly, zoom click lands at precise unmagnified position, no double-clicks
