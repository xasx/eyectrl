# Design: Zoom Mode Action Buttons Outside Circle

**Date:** 2026-03-07

## Problem

In Zoom Mode, when the user fixates within the zoom circle and the action menu appears, the buttons are placed at `RingRadius = 160px` from the fixation center. At default zoom settings (`CircleRadiusPixels = 150`, `MagnificationLevel = 2.0`), the zoom circle display radius is 300px — so action buttons at 160px appear **inside** the zoom circle, obscuring the magnified content.

## Goal

Action buttons must always appear fully outside the zoom circle, with a small gap.

## Design

### Effective Ring Radius Formula

```
effectiveRingRadius = ZoomDisplayRadius + ButtonRadius + 20
```

Where:
- `ZoomDisplayRadius = CircleRadiusPixels × MagnificationLevel`
- `ButtonRadius = 50px` (existing constant)
- `20px` = fixed gap between zoom circle edge and button inner edge

At defaults: `300 + 50 + 20 = 370px`

For the non-zoom action menu the existing `160px` default is unchanged.

### Data Flow

`ShowActionMenuMessage` gains `int ZoomDisplayRadius = 0`. `GazeEngineAdapter.OnShowActionMenu` sets it to `(int)(CircleRadiusPixels × MagnificationLevel)` when `showBack = true` (zoom mode), otherwise 0.

`ActionMenuViewModel` computes `EffectiveRingRadius` on receiving `ShowActionMenuMessage` and uses it for dwell hit target positions. `ActionMenuWindow.Show()` accepts a `ringRadius` parameter and uses it for rendering.

### Screen-Edge Clamping

The existing hard-coded `margin = 210` (= 160 + 50) in `GazeEngineAdapter.OnShowActionMenu` becomes:

```
margin = effectiveRingRadius + ButtonRadius
```

At default zoom settings: `370 + 50 = 420px`.

## Files Changed

| File | Change |
|---|---|
| `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` | Add `int ZoomDisplayRadius = 0` to `ShowActionMenuMessage` |
| `src/EyeCtrl.App/Services/GazeEngineAdapter.cs` | Compute and pass `ZoomDisplayRadius`; make clamping margin dynamic |
| `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs` | Add `EffectiveRingRadius` property; compute on receive |
| `src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs` | Add `ringRadius` param to `Show()`; replace `const RingRadius = 160` |
| `src/EyeCtrl.App/App.xaml.cs` | Pass `actionMenuVM.EffectiveRingRadius` to `actionMenu.Show()` |

No Core changes. No new settings.
