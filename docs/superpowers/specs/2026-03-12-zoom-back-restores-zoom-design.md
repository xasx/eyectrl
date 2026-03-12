# Zoom Back Button Restores Zoom View

**Date:** 2026-03-12
**Status:** Approved

## Problem

When the user selects a point in zoom view, the zoom overlay is hidden and the action menu appears with a Back button. Pressing Back should restore the zoom view exactly as it was when first activated — fresh `FixationDetector`, fresh grace period, gaze indicator shown from scratch.

Currently Back does not restore the zoom view at all. The zoom overlay stays hidden and the user is stuck.

### Root Cause

`InteractionManager.OnZoomFixation` fires the `HideZoom` event (not `EndZoom`) to temporarily hide the zoom overlay while the action menu is shown. This event is handled **directly** in `App.xaml.cs` — it hides the overlay window but never updates `ZoomOverlayViewModel.IsVisible`, which stays `true`.

When Back is pressed, `OnZoomBack()` fires `StartZoom → StartZoomMessage`. `ZoomOverlayViewModel.Receive(StartZoomMessage)` sets `IsVisible = true` — but since it was already `true`, no `PropertyChanged` fires. `App.xaml.cs` never calls `zoomOverlay.Show()` or creates a fresh `FixationDetector`.

The same root cause also breaks the Recapture button.

## Design

Close the gap in the message bus: route `HideZoom` through the adapter and ViewModel so `IsVisible` always reflects actual visibility.

### New message

```csharp
// InteractionMessages.cs
public record ZoomHiddenMessage;
```

### GazeEngineAdapter

Subscribe to `HideZoom` in the constructor and send `ZoomHiddenMessage` on the UI thread. Unsubscribe in `Dispose()`.

```csharp
_interactionManager.HideZoom += OnHideZoom;

private void OnHideZoom() =>
    _dispatcher.BeginInvoke(() => _messenger.Send(new ZoomHiddenMessage()));
```

### ZoomOverlayViewModel

Implement `IRecipient<ZoomHiddenMessage>`. Handle it identically to `EndZoomMessage`: set `IsVisible = false`, clear dwell targets, reset progress values. The existing `if (IsVisible)` guard in `Receive(GazeCursorMovedMessage)` already stops dwell processing once hidden.

### App.xaml.cs

Remove the direct `im.HideZoom` handler:

```csharp
// DELETE this block:
im.HideZoom += () => Dispatcher.BeginInvoke(() =>
{
    zoomOverlay.Hide();
    zoomSideButtons.Hide();
    zoomIndicator.Hide();
});
```

Its work is already done by the existing `IsVisible = false` `PropertyChanged` branches, which call `zoomOverlay.Hide()`, `zoomIndicator.Hide()`, `zoomSideButtons.Hide()`, and unsubscribe the `zoomGazeHandler` via `zoomVisibilityHandler`.

## Corrected Flow

1. Zoom activated → `StartZoomMessage` → `zoomVM.IsVisible = true` → fresh `FixationDetector`, zoom shown
2. Zoom fixation detected → `HideZoom` event → `ZoomHiddenMessage` → `zoomVM.IsVisible = false` → overlay hidden, gaze handler unsubscribed
3. Action menu shown with Back button
4. Back pressed → `BackSelectedMessage` → `OnZoomBack()` → `StartZoom` → `StartZoomMessage` → `zoomVM.IsVisible` transitions `false → true` → `PropertyChanged` fires → fresh `FixationDetector`, fresh grace period, zoom shown again

Step 4 is now identical to step 1 — "as if initially invoked through the interaction button."

## Files Changed

| File | Change |
|------|--------|
| `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` | Add `ZoomHiddenMessage` record |
| `src/EyeCtrl.App/Services/GazeEngineAdapter.cs` | Subscribe/unsubscribe `HideZoom`, send `ZoomHiddenMessage` |
| `src/EyeCtrl.ViewModels/ZoomOverlayViewModel.cs` | Implement `IRecipient<ZoomHiddenMessage>` |
| `src/EyeCtrl.App/App.xaml.cs` | Remove direct `im.HideZoom` handler |

## Side Effects

- **Recapture fixed for free:** The same `IsVisible` inconsistency that broke Back also broke Recapture (`ZoomRecaptureMessage`). This fix resolves both.
- **No behavioral change** in non-zoom flows.
