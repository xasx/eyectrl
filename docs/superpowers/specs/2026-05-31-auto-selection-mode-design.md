# Auto Selection Mode — Design Spec

## Overview

Add a third selection mode: **Auto Selection**. The existing two modes, renamed for clarity, become **None** (raw pixel click) and **Manual Selection** (zoom-and-dwell). Auto Selection snaps the click target to the nearest interactive UI control identified via UIA/Win32, correcting for eye tracker imprecision.

**Guiding principle:** the activation-button → action-menu dwell flow stays identical across all three modes. Only what happens *between* activation button dwell and action menu display differs (target resolution).

---

## Mode System

### `SelectionMode` enum (new)

```
EyeCtrl.Core/Models/SelectionMode.cs
```

| Value | Behavior |
|-------|----------|
| `None` (default) | Raw pixel coordinates — unchanged from current behavior |
| `ManualSelection` | Zoom-and-dwell via ManualSelection overlay — unchanged from current behavior |
| `AutoSelection` | Snap-to-control via UIA/Win32 — new |

### State machine changes

`InteractionManager` stores `SelectionMode` (replaces `ManualSelectionEnabled` bool). The transition in `OnActivationButtonDwelled()` becomes:

```
OnActivationButtonDwelled():
  switch SelectionMode:
    None → ShowActionMenu(fixationPoint)          // unchanged
    Manual → SetState(ManualSelectionActive)       // unchanged
    Auto → target = _controlFinder.FindTarget(fixationPoint)
           ShowActionMenu(target.CenterPoint)      // NEW — menu snaps to control
```

No new `InteractionState` values. The existing `Fixation → ActivationButtonDwelled → ActionMenuVisible → FreeLook` chain remains intact.

### Deprecated

- `ManualSelectionEnabled` bool on `InteractionManager` — removed
- `ManualSelectionToggledMessage` — removed
- Existing toolbar ManualSelection toggle button — removed, replaced by 3-segment selector

---

## Control Detection

### `IControlFinder` (new interface)

```
EyeCtrl.Core/Abstractions/IControlFinder.cs
```

```csharp
ControlInfo? FindTarget(PhysicalPoint point);
```

### `ControlInfo` (new model)

```
EyeCtrl.Core/Models/ControlInfo.cs
```

| Field | Type | Description |
|-------|------|-------------|
| `BoundingRect` | `Rect` | Screen-coordinate bounding rectangle |
| `CenterPoint` | `PhysicalPoint` | Center of bounding rect (click target) |
| `ControlType` | `string` | UIA control type or "Window" for fallback |
| `WindowHandle` | `IntPtr` | HWND for fallback SendInput targeting |

### Implementation: `ControlFinder`

```
EyeCtrl.Platform/Services/ControlFinder.cs
```

**UIA path (primary):**
1. `AutomationElement.FromPoint(screenPoint)` — deepest UIA element under cursor
2. Walk up ancestors to find first element with `IsInvokePatternAvailable` or `IsTextPatternAvailable` or a meaningful `ControlType` (Button, Edit, Hyperlink, ListItem, TreeItem, TabItem, MenuItem, CheckBox, RadioButton, ComboBox, Slider, Spinner)
3. If found, return its `BoundingRectangle` and compute center

**Win32 fallback:**
1. If UIA element is null or has a non-interactive `ControlType` (Pane, Document, Text, Group, Window), call `WindowFromPoint`
2. Return the window's client-area center as target

**No-target fallback:**
- If nothing found within the fixation radius (`FixationRadiusPixels`, default 50px), return `null`
- Caller (`InteractionManager`) falls back to raw pixel targeting (behaves like None mode)

**Performance constraint:** `FindTarget` must complete in <5ms to avoid frame drops in the gaze pipeline. Cache UIA `AutomationElement` tree walker; avoid recursive searches.

---

## Visual Feedback

### Target Highlight Overlay

When Auto Selection finds a control, a new overlay draws a colored border rectangle around the detected control's `BoundingRect`.

- Lives in `EyeCtrl.ViewModels` as `TargetHighlightOverlayViewModel`
- Triggered by `InteractionManager` raising `ShowActionMenu` event with the resolved `ControlInfo`; `EventCoordinator` subscribes and creates/positions the WPF overlay on the overlay window
- Destroyed when action fires or is dismissed (via `ActionRequestedMessage` or `BackdropDismissMessage`)
- Rendered as a WPF `Border` with `Stroke` and `StrokeThickness` from settings, `Background = Transparent`, on the overlay window
- Fade in (200ms opacity animation), fade out on dismiss
- If fallback to raw pixel occurs (no control found), no highlight is shown

### Settings

New settings class `AutoSelectionSettings`:

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `HighlightColor` | `string` | `"#0078D4"` | Border color (accent blue) |
| `HighlightThickness` | `double` | `3.0` | Border thickness in WPF units |
| `FallbackToPixelOnNoControl` | `bool` | `true` | Fall back to raw pixel if no control found |

Stored in `%APPDATA%/EyeCtrl/settings.json` under key `"AutoSelection"`.

---

## Toolbar Mode Selector

### Replacement widget

Existing ManualSelection toggle button is replaced with a **3-segment selector**:

```
┌──────┬──────────────────┬──────────────────┬──────────────────┐
│ Pause│  None Selection  │ Manual Selection │  Auto Selection  │
└──────┴──────────────────┴──────────────────┴──────────────────┘
```

- Each segment is a `ToolbarButtonViewModel` with a `DwellDetector` (same pattern as existing toolbar buttons)
- Dwell on a segment calls `InteractionManager.SetSelectionMode(mode)` and fires `SelectionModeChangedMessage`
- Active segment is styled with a highlight/accent background
- Text labels are short: "None", "Manual", "Auto"
- `ToolbarViewModel` exposes a `ToolbarButtonViewModel[] SelectionModeButtons` property

### Messages

- `SelectionModeChangedMessage(SelectionMode)` — sent when mode changes via toolbar or settings. `InteractionManager`, `EventCoordinator`, and the toolbar subscribe.

---

## Settings Window Integration

- Add `SelectionMode` selector (radio buttons or combo) to the General/Interaction tab
- Auto-specific settings (`HighlightColor`, `HighlightThickness`, `FallbackToPixelOnNoControl`) appear only when `SelectionMode == AutoSelection` is selected
- `SelectionMode` property bound to `SettingsViewModel`, persisted to JSON

### JSON structure (new section)

```json
{
  "SelectionMode": "None",
  "AutoSelection": {
    "HighlightColor": "#0078D4",
    "HighlightThickness": 3.0,
    "FallbackToPixelOnNoControl": true
  }
}
```

### No migration

The app is not yet released. No backward compatibility or migration from old settings format is needed. The old `ManualSelection.Enabled` key is simply not written anymore.

---

## Files Changed (summary)

### New files
| File | Purpose |
|------|---------|
| `EyeCtrl.Core/Models/SelectionMode.cs` | Enum |
| `EyeCtrl.Core/Models/ControlInfo.cs` | Model |
| `EyeCtrl.Core/Abstractions/IControlFinder.cs` | Interface |
| `EyeCtrl.Platform/Services/ControlFinder.cs` | UIA + Win32 implementation |
| `EyeCtrl.Core/Settings/AutoSelectionSettings.cs` | Settings model |
| `EyeCtrl.ViewModels/TargetHighlightOverlayViewModel.cs` | Highlight overlay |
| `EyeCtrl.Core/Messages/SelectionModeChangedMessage.cs` | Message |

### Modified files
| File | Change |
|------|--------|
| `EyeCtrl.Core/Processing/InteractionManager.cs` | `SelectionMode` property, branch in `OnActivationButtonDwelled()`, remove `ManualSelectionEnabled` |
| `EyeCtrl.ViewModels/ToolbarViewModel.cs` | 3-segment selector replaces toggle button |
| `EyeCtrl.ViewModels/SettingsViewModel.cs` | Bind `SelectionMode`, show/hide Auto settings |
| `EyeCtrl.App/Services/EventCoordinator.cs` | Handle `SelectionModeChangedMessage`, manage highlight overlay lifecycle |
| `EyeCtrl.App/Services/AppBootstrapper.cs` | Register `IControlFinder` in DI |
| `EyeCtrl.Core/Settings/EyeCtrlSettings.cs` | Add `SelectionMode` + `AutoSelection` properties |

### Removed
| Item | Reason |
|------|--------|
| `ManualSelection.Enabled` property (settings + InteractionManager) | Replaced by `SelectionMode` |
| `ManualSelectionToggledMessage` | Replaced by `SelectionModeChangedMessage` |
| Toolbar ManualSelection toggle button | Replaced by 3-segment selector |

---

## Error Handling

- `ControlFinder.FindTarget()` wraps UIA calls in try/catch; returns `null` on COM exceptions or unavailable UIA
- If no control found and `FallbackToPixelOnNoControl` is `true`, `InteractionManager` proceeds with raw pixel targeting (same as None mode)
- If no control found and `FallbackToPixelOnNoControl` is `false`, the action menu does not appear and the user must refixate

---

## Testing

### Unit tests (`EyeCtrl.Core.Tests`)
- `InteractionManager.OnActivationButtonDwelled_NoneMode_ShowsActionMenuAtFixationPoint`
- `InteractionManager.OnActivationButtonDwelled_AutoMode_ShowsActionMenuAtControlCenter`
- `InteractionManager.OnActivationButtonDwelled_AutoMode_NoControl_FallsBackToPixel`
- `InteractionManager.SetSelectionMode_FiresChangedMessage`

### Unit tests (`EyeCtrl.ViewModels.Tests`)
- `ToolbarViewModel_SelectionModeButtons_ThreeSegments`
- `ToolbarViewModel_DwellOnSegment_ChangesMode`

### Platform tests (`EyeCtrl.Platform.Tests`)
- `ControlFinder_FindTarget_ReturnsControlInfoForButton`
- `ControlFinder_FindTarget_ReturnsNullForEmptyArea` (desktop)
- `ControlFinder_FindTarget_UIA_OverElement` (integration-level, may need a helper window)
