# Fullscreen Glass Backdrop for Overlays

**Date:** 2026-05-19
**Status:** Approved

## Overview

Add a fullscreen glass backdrop that covers the entire monitor when the Action Menu or Manual Selection overlay is visible. The backdrop sits behind the overlay content (above the desktop, below the overlays), providing visual separation from background windows. User-configurable via settings.

## Architecture

A new `GlassBackdropWindow : OverlayWindow` (fullscreen, click-through) is created once at startup. Managed by `EventCoordinator` — shown when Action Menu or Manual Selection becomes visible, hidden when both are invisible. Existing overlays are completely unchanged.

### Z-Order

```
Topmost layer (highest)
├── ManualSelectionGazeIndicatorWindow  (unchanged)
├── ActionMenuWindow                    (unchanged)
├── ManualSelectionOverlayWindow        (unchanged)
├── ManualSelectionSideButtonWindow     (unchanged)
├── GlassBackdropWindow                 ← NEW, below all content overlays
└── Desktop / normal windows
```

## Backdrop Types

| Type | Mechanism | OS Support | Behavior |
|------|-----------|------------|----------|
| `Mica` | `DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE)` | Win11 22H2+ | Wallpaper-adaptive, non-blurring, subtle |
| `Acrylic` | `SetWindowCompositionAttribute(ACCENT_ENABLE_ACRYLICBLURBEHIND)` | Win10 1803+ | Live blur + dark tint |
| `SolidDarken` | `Rectangle` with semi-transparent `SolidColorBrush` | All | Simple overlay, no OS dependency |

**Note on Mica and layered windows:** Mica via `DWMWA_SYSTEMBACKDROP_TYPE` requires a non-layered window (`AllowsTransparency="False"`), while Acrylic and SolidDarken need `AllowsTransparency="True"` for their transparency/blur. The backdrop window is created without `AllowsTransparency` by default. When Acrylic or SolidDarken is the effective type, the window switches to layered mode (`WS_EX_LAYERED`) at runtime via `SetWindowLong`. When Mica is the effective type, layering is removed. This is handled in `GlassBackdropWindow.ApplyBackdropType()`.

### Fallback Chain

```
Mica → Acrylic → SolidDarken
```

- If Mica fails (not Win11 22H2+, or DWM API rejects it), fall back to Acrylic.
- If Acrylic fails (remote desktop, GPU driver issue, or Win10 pre-1803), fall back to SolidDarken.
- SolidDarken is the universal fallback — always works.

## Settings

New `BackdropSettings` class in `EyeCtrl.Core/Settings/`, following existing pattern (`ObservableObject`, `IValidatable`, source-generated properties).

```csharp
public partial class BackdropSettings : ObservableObject, IValidatable
{
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private BackdropType _type = BackdropType.Acrylic;
    [ObservableProperty] private double _opacity = 0.3;
}
```

- `Enabled`: master toggle. When `false`, no backdrop is ever shown.
- `Type`: `Mica` / `Acrylic` / `SolidDarken`.
- `Opacity`: range 0.1–0.9, validated. Applies to `SolidDarken` only (Mica and Acrylic have their own intrinsic opacity).

New `BackdropType` enum in `EyeCtrl.Core/`:

```csharp
public enum BackdropType { Acrylic, Mica, SolidDarken }
```

`EyeCtrlSettings` gets a new property:

```csharp
[ObservableProperty] private BackdropSettings _backdrop = new();
```

Persistence via existing `SettingsService` (JSON, camelCase, `%APPDATA%/EyeCtrl/settings.json`).

## Hide Button

A small circular "×" button drawn on the backdrop in the top-right corner of the primary monitor. Dwell-detectable using the app's existing `DwellDetector` infrastructure. When activated:

1. Sends `BackdropDismissMessage` (new message in `InteractionMessages.cs`)
2. `EventCoordinator` handles it by hiding the backdrop and exiting any active overlay (Action Menu or Manual Selection)

The button is positioned at a fixed DIP offset from the top-right (e.g., 40 DIP from top, 40 DIP from right). Dwell target uses `UIConstants.ButtonRadius` and a dwell time defined in `UIConstants`.

## Re-capture Safety

When Manual Selection re-captures the screen (`ManualSelectionRecaptureMessage`), the backdrop must be hidden before `CaptureAndDisplay()` executes so it doesn't appear in the screenshot.

Implementation in `EventCoordinator` recapture handler:

```csharp
_glassBackdrop.Hide();
_context.Messenger.Send(new StartManualSelectionMessage(...));
_glassBackdrop.Show();
```

`CaptureAndDisplay()` is synchronous, so sequential hide→capture→show is safe. No async coordination, no new messages, no race conditions.

## Window Merge (Deferred)

The user asked about merging Manual Selection's three windows (overlay, side buttons, gaze indicator) into one. Note: the gaze indicator is the movable cursor window during manual selection mode. Merging is technically feasible — all three use `IsClickThrough = true` and are positioned from a shared origin — but is a separate refactoring concern. Deferred to a follow-up issue.

## Files

### New Files

| File | Purpose |
|------|---------|
| `src/EyeCtrl.Core/BackdropType.cs` | Enum: Acrylic, Mica, SolidDarken |
| `src/EyeCtrl.Core/Settings/BackdropSettings.cs` | Settings with validation |
| `src/EyeCtrl.App/Overlays/GlassBackdropWindow.xaml` | Fullscreen window (minimal XAML) |
| `src/EyeCtrl.App/Overlays/GlassBackdropWindow.xaml.cs` | Acrylic/Mica P/Invoke, SolidDarken fill, hide button drawing |
| `src/EyeCtrl.ViewModels/GlassBackdropViewModel.cs` | IsVisible, hide button dwell |

### Modified Files

| File | Changes |
|------|---------|
| `src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs` | Add `Backdrop` property |
| `src/EyeCtrl.App/Services/EventCoordinator.cs` | Show/hide backdrop with overlays, re-capture safety, dismiss handler |
| `src/EyeCtrl.App/App.xaml.cs` | Register VM, create window in OverlayManager |
| `src/EyeCtrl.App/Services/AppBootstrapper.cs` | Register `GlassBackdropViewModel` in DI |
| `src/EyeCtrl.Core/Messages/InteractionMessages.cs` | Add `BackdropDismissMessage` record |

## Edge Cases & Error Handling

- **Multiple monitors**: The backdrop covers the monitor that contains the gaze point (or the center of the overlay content). Does not attempt to cover multiple monitors simultaneously. If gaze moves to a different monitor while backdrop is visible, the backdrop repositions to that monitor.
- **Remote Desktop**: Acrylic/Mica APIs may fail. Fallback chain handles this gracefully — always lands on SolidDarken.
- **Settings disabled**: When `Backdrop.Enabled == false`, the backdrop window is never shown. The `Idle` / `Shown` / `Hidden` methods on `GlassBackdropWindow` become no-ops.
- **Backdrop already hidden during re-capture**: `Hide()` is idempotent — calling it on an already-hidden window is a no-op.
- **Hide button on secondary monitor**: If the backdrop spans multiple monitors or a non-primary monitor, compute the button position from the backdrop's actual bounds rather than assuming primary monitor.

## Testing

- Unit test for `BackdropSettings.Validate()` (boundary values)
- Unit test for fallback chain (`GlassBackdropWindow` resolves the effective backdrop type given OS capabilities)
- Unit test for `EventCoordinator` backdrop show/hide logic (with mock/fake `GlassBackdropWindow`)
- UI test: verify backdrop appears when Action Menu is shown and disappears when hidden
- UI test: verify backdrop does not appear in manual selection screen capture after re-capture
