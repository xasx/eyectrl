# Auto Selection Mode — Design Spec

## Overview

Add a third selection mode: **Auto Selection**. The existing two become **None** (raw pixel click) and **Manual Selection** (zoom-and-dwell). Auto Selection snaps the click target to the nearest interactive UI control identified via UIA/Win32, correcting for eye tracker imprecision.

The activation-button -> action-menu dwell flow stays identical across all three modes. The difference is what target the click ultimately fires on.

---

## Mode System

### `SelectionMode` enum (new file)

```
EyeCtrl.Core/Models/SelectionMode.cs
```

| Value | Behavior |
|-------|----------|
| `None` (default) | Raw pixel coordinates — unchanged |
| `ManualSelection` | Zoom-and-dwell overlay — unchanged |
| `AutoSelection` | Snap-to-control via UIA/Win32 — new |

Stored as a top-level property on `EyeCtrlSettings` (see [Settings](#settings-and-json)).

### State machine changes in `InteractionManager`

`ManualSelectionEnabled` bool is replaced by `SelectionMode` property. The branch in `OnActivationButtonDwelled()` becomes:

```
OnActivationButtonDwelled():
  switch SelectionMode:
    None       → ShowActionMenu(fixationPoint)            // unchanged
    Manual     → StartManualSelection(fixationPoint)       // unchanged
    Auto       → target = _controlFinder.FindTarget(fixationPoint)
                 if target != null → _autoSelectionTarget = target
                                     ShowActionMenu(target.CenterPoint)
                 else             → (fallback, see §No-target fallback)
```

A new field `_autoSelectionTarget: ControlInfo?` is stored for later use when the action actually fires.

No new `InteractionState` values. Auto mode uses the existing `ActionMenuVisible` state. The `Fixation → ActionMenuVisible → FreeLook` chain is shared with None mode.

### Click path — the critical fix

Currently the click target is determined in two places that must both be changed:

**A) `InteractionManager.OnActionSelected()`** (line 84-91). The switch that resolves `targetPoint` from `CurrentState` must gain an Auto arm. The stored `_autoSelectionTarget` is used when the state is `ActionMenuVisible` **and** the mode is `AutoSelection`:

```
OnActionSelected(action):
  targetPoint = CurrentState switch {
    ActionMenuVisible when SelectionMode == AutoSelection => _autoSelectionTarget?.CenterPoint,
    ActionMenuVisible                                     => FixationPoint,
    ManualSelectionActionMenuVisible                      => ManualSelectionDwellPoint,
    _                                                     => null
  }
```

**B) `GazeEngineAdapter.OnActionRequested()`** (line 158). Currently:
```csharp
var isFromManualSelection = _interactionManager.CurrentState == InteractionState.ManualSelectionActionMenuVisible;
```
The message field `ActionRequestedMessage.IsFromManualSelection` controls whether `EventCoordinator` replaces the target with `StableCentroid` (line 687). For Auto mode the target must **also** survive that override, because after dwelling on the action menu the user's gaze is on the menu ring, not on the original control.

**Change:** the message field is renamed `IsPreciseTarget` (meaning: this point was resolved precisely and must NOT be overwritten by live gaze). Set it true when either ManualSelection state OR Auto mode:

```csharp
var isPrecise = _interactionManager.CurrentState == InteractionState.ManualSelectionActionMenuVisible
             || (_interactionManager.CurrentState == InteractionState.ActionMenuVisible
                 && _interactionManager.SelectionMode == SelectionMode.AutoSelection);
```

**C) `EventCoordinator.HandleActionRequested()`** (line 687). Rename `IsFromManualSelection` → `IsPreciseTarget`. The condition becomes:

```csharp
var pt = msg.IsPreciseTarget ? msg.Point : (_context.GazeEngine.StableCentroid ?? msg.Point);
```

These three changes together ensure the snapped control center flows all the way to `IInputSimulator.LeftClick(pt)`.

### Deprecated / removed

| Item | Reason |
|------|--------|
| `ManualSelectionEnabled` bool on `InteractionManager` | Replaced by `SelectionMode` |
| `ManualSelectionToggledMessage` | Replaced by `SelectionModeChangedMessage` |
| Toolbar ManualSelection toggle button | Replaced by 3-segment selector |

---

## Control Detection

### `IControlFinder` (new interface)

```
EyeCtrl.Core/Interfaces/IControlFinder.cs
```

```csharp
ControlInfo? FindTarget(PhysicalPoint point);
```

### `PhysicalRect` (new model)

```
EyeCtrl.Core/Models/PhysicalRect.cs
```

A `readonly record struct` with `double X, Y, Width, Height` — same conventions as the existing `PhysicalPoint`. This keeps Core framework-agnostic (no WPF `Rect` dependency).

### `ControlInfo` (new model)

```
EyeCtrl.Core/Models/ControlInfo.cs
```

| Field | Type | Description |
|-------|------|-------------|
| `BoundingRect` | `PhysicalRect` | Screen-coordinate bounding rectangle |
| `CenterPoint` | `PhysicalPoint` | Center of bounding rect (click target) |

No `WindowHandle` needed — clicks go through `IInputSimulator` which takes `PhysicalPoint` coordinates.

### Implementation: `ControlFinder`

```
EyeCtrl.Platform/Services/ControlFinder.cs
```

The Platform project already has `EnableWindowsTargeting=true` ([EyeCtrl.Platform.csproj:7](src/EyeCtrl.Platform/EyeCtrl.Platform.csproj:7)), which gives access to Win32 APIs. For UIA the project needs a new package reference: `System.Windows.Automation` (the managed UIA client library available on net10.0-windows — Platform will need either a `-windows` TFM variant or the COM `Interop.UIAutomationClient` route; the implementation chooses the shortest path available).

The `ControlFinder` constructor creates a single `AutomationElement` tree walker (cached; the walker is thread-safe for reuse); it does NOT call `Automation.AddAutomationEventHandler` or any global hook — only fire-and-forget `FindTarget`.

**UIA path (primary):**
1. `AutomationElement.FromPoint(screenPoint)` — deepest UIA element under that screen position (cross-process COM call)
2. Walk up ancestors via `TreeWalker.ControlViewWalker.GetParent()` to find the first interactive element — one whose `ControlType` is Button, Edit, Hyperlink, ListItem, TreeItem, TabItem, MenuItem, CheckBox, RadioButton, ComboBox, or Slider
3. Return its `BoundingRectangle` (converted to `PhysicalRect`) and compute center as `PhysicalPoint`

**Win32 fallback:**
1. If UIA element is null or has a non-interactive `ControlType` (Pane, Document, Text, Group, Window, Table, Header), call `WindowFromPoint`
2. Get the window's client-area center via `GetClientRect` + `ClientToScreen` → return as `PhysicalRect` + `PhysicalPoint`

**No-target fallback:**
- If nothing found, return `null`
- `InteractionManager.OnActivationButtonDwelled()` then falls back to raw pixel targeting (same as None mode)
- This fallback is controlled by `AutoSelectionSettings.FallbackToPixelOnNoControl` (default `true`). When `false`, no action menu is shown and the user must refixate.

### Threading model

`FindTarget` is called once per activation — NOT per gaze frame — from `OnActivationButtonDwelled()`. This runs on the WPF dispatcher path (the `GazeEngineAdapter` bridges events via `_dispatcher.BeginInvoke`).

**UIA deadlock risk:** Calling `AutomationElement.FromPoint` from the same thread that owns the WPF overlay windows *could* cause a COM re-entrancy deadlock. To mitigate:
1. `FindTarget` is called on a background thread via `Task.Run`
2. `ShowActionMenu` is deferred until the result arrives (displayed on the dispatcher)
3. A timeout (500ms) protects against hung UIA calls; on timeout the raw-pixel fallback is used
4. The entire UIA path is wrapped in try/catch (COM exceptions, `ElementNotAvailableException`)

**Performance:** The <5ms budget from the original spec was incorrect — `FindTarget` is not in the gaze pipeline. It is called once per activation dwell (~700ms after fixation). Realistic budget: <500ms (the timeout), with typical UIA `FromPoint` costing 10-50ms for a local-process element and up to 150ms for a cross-process one.

---

## Visual Feedback

### Target Highlight Overlay

When Auto Selection resolves a control, a new overlay draws a colored border rectangle around the control's `BoundingRect`.

- **ViewModel:** `TargetHighlightOverlayViewModel` (in `EyeCtrl.ViewModels`). Holds `BoundingRect`, `HighlightColor`, `HighlightThickness`, `IsVisible`.
- **Window:** `TargetHighlightWindow` (new WPF overlay window in `EyeCtrl.App/Overlays/`). Transparent border-less window, topmost, shows a `Border` element with the configured stroke and thickness. No hit-testing (`IsHitTestVisible = false`), `Background = Transparent`.
- **Registration:** `OverlayManager` gains a `TargetHighlight` property (like the existing 9 overlays). `TargetHighlightWindow` is created in `OverlayManager.Initialize()` alongside the other windows. Its DataContext is set to `viewModels.TargetHighlight`, and `OverlayViewModels` gains a `TargetHighlight` property.
- **Lifecycle:** `EventCoordinator` subscribes to `ShowActionMenuMessage` — when Auto mode is active and the message carries a resolved `ControlInfo`, it positions and shows the highlight. Hidden on `ActionRequestedMessage`, `HideActionMenuMessage`, or `BackdropDismissMessage`. Fade-in over 200ms, fade-out on dismiss.
- If fallback to raw pixel occurs (no control found), no highlight is shown.

### New `ShowActionMenuMessage` payload

The existing `ShowActionMenuMessage` record carries a `PhysicalPoint`. For Auto mode it needs to also carry the resolved `ControlInfo?` so the highlight overlay can be positioned. Add an optional `ControlInfo? ResolvedControl` property to the message:

```csharp
public record ShowActionMenuMessage(
    IReadOnlyList<ActionMenuItemModel> Actions,
    PhysicalPoint Center,
    int DwellMs,
    bool IsFromManualSelection,
    double? DwellRadiusPixels = null,
    PhysicalPoint? CircleCenter = null,
    double? CircleRadiusPixels = null,
    ControlInfo? ResolvedControl = null  // NEW — non-null only in Auto mode
);
```

---

## Settings and JSON

### `EyeCtrlSettings` changes

New top-level property and sub-settings class:

```csharp
[ObservableProperty] private SelectionMode _selectionMode = SelectionMode.None;
[ObservableProperty] private AutoSelectionSettings _autoSelection = new();
```

### `AutoSelectionSettings` (new file)

```
EyeCtrl.Core/Settings/AutoSelectionSettings.cs
```

Inherits `ObservableObject, IValidatable`. Properties:

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `HighlightColor` | `string` | `"#0078D4"` | Border color (hex) |
| `HighlightThickness` | `double` | `3.0` | Border thickness |
| `FallbackToPixelOnNoControl` | `bool` | `true` | Fall back to raw pixel |

`Validate()` checks `HighlightColor` is a valid hex color and `HighlightThickness` is 1..10.

### JSON structure

```json
{
  "selectionMode": 0,
  "autoSelection": {
    "highlightColor": "#0078D4",
    "highlightThickness": 3.0,
    "fallbackToPixelOnNoControl": true
  }
}
```

Notes on serialization:
- `SettingsService` uses `JsonNamingPolicy.CamelCase` ([SettingsService.cs:12](src/EyeCtrl.Core/Settings/SettingsService.cs:12)) — keys are camelCase.
- Enums serialize as integers by default. To support human-readable JSON, **add `JsonStringEnumConverter`** to `SettingsService.JsonOptions`. This affects `ViewPosition` (serialized as `"atCenter"` via camelCase) and the new `SelectionMode` (`"none"`, `"manualSelection"`, `"autoSelection"`) — both become readable strings. Existing integer-valued JSON files still deserialize correctly due to `JsonStringEnumConverter`'s built-in fallback.

### Validate() arm

`EyeCtrlSettings.Validate()` must gain:

```csharp
if (AutoSelection is null)
    errors.Add("AutoSelection settings must not be null.");
else
    errors.AddRange(AutoSelection.Validate());
```

### No migration

The app is unreleased. Old `ManualSelection.Enabled` key simply stops being written. No migration code.

---

## Toolbar Mode Selector

### ViewModel changes (`ToolbarViewModel`)

The 4 existing buttons become: Pause, SelectionMode, Keyboard, Settings. The "SelectionMode" button replaces the "ManualSelection" toggle. Instead of a single toggle, it cycles the mode on each dwell.

Mode cycle: `None → ManualSelection → AutoSelection → None → ...`

- `OnTargetActivated("SelectionMode")` determines the next mode: `(SelectionMode)(((int)_currentMode + 1) % 3)`
- Sends `SelectionModeChangedMessage(newMode)`
- `_currentMode` is tracked as a field, updated by receiving `SelectionModeChangedMessage` (to reflect settings changes)
- The button's symbol/label changes to show the current mode: `"⊙"` (None), `"⊕"` (ManualSelection), `"⊕A"` (AutoSelection, or similar distinguishable symbol)

### `SelectionModeChangedMessage` (new, in ViewModels)

```csharp
// EyeCtrl.ViewModels/Messages/InteractionMessages.cs
public record SelectionModeChangedMessage(SelectionMode Mode);
```

(All existing messages live in this file ([InteractionMessages.cs](src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs)).)

### ToolbarWindow code-behind

`SetManualSelectionState(bool)` becomes `SetSelectionModeState(SelectionMode)`, updating the SelectionMode button's appearance:
- None: dark gray background (normal)
- Manual: green background (same as current "on" state)
- Auto: blue background (new)

The existing 4-button layout (Pause, X, Keyboard, Settings) stays; the middle button just gets a new ID and behavior.

### Message flow

```
Toolbar dwell → ToolbarViewModel → messenger.Send(SelectionModeChangedMessage)
                                     ↓
GazeEngineAdapter receives → _interactionManager.SelectionMode = newMode
                                     ↓
         also: _settings.SelectionMode = newMode
```

When settings are saved (`SettingsChangedMessage`), `EventCoordinator.OnSettingsChanged` pushes `s.SelectionMode` to `InteractionManager.SelectionMode` — same pattern as the current `ManualSelectionEnabled` sync ([EventCoordinator.cs:536](src/EyeCtrl.App/Services/EventCoordinator.cs:536)).

---

## Settings Window Integration

### `BuildTabs()` in `SettingsViewModel`

**ManualSelection tab** (L71-76): The `ToggleSettingViewModel("ManualSelectionEnabled", ...)` toggle is **removed**. The tab keeps Radius and Magnification sliders only. A new combo at the top switches the mode, or the mode selector is placed on the General tab.

**New AutoSelection tab** (or fold into ManualSelection / General):

Decision: **Add to ManualSelection tab** (renamed to "Selection"), since the three modes are siblings. Items:

1. `ComboSettingViewModel("selectionMode", "Selection Mode", ...)` — values from `Enum.GetNames<SelectionMode>()`, formatted as "None", "Manual", "Auto". Gates `SelectionMode` on `_settings`.
2. (Existing) Circle Radius slider
3. (Existing) Magnification slider
4. (New, Auto only) `ColorSettingViewModel` for `HighlightColor`
5. (New, Auto only) `SliderSettingViewModel` for `HighlightThickness`, range 1–10

**Conditional visibility:** Auto-specific settings (highlight color, thickness) are always present in the tab but use a lighter/disabled appearance when selection mode is not Auto. The Settings UI has no dynamic show/hide mechanism today; keeping items always present with disabled state is the low-friction choice.

### `ResetAllSettings()`

Add line:

```csharp
CopySettings(defaults.AutoSelection, _settings.AutoSelection);
```

And reset top-level `SelectionMode` explicitly:

```csharp
_settings.SelectionMode = defaults.SelectionMode;
_settings.ViewPosition = defaults.ViewPosition;
```

(`ResetAllSettings` currently skips all top-level properties, only copying sub-setting objects ([SettingsViewModel.cs:148-165](src/EyeCtrl.ViewModels/SettingsViewModel.cs:148)). This fix corrects `ViewPosition` oversight too.)

### `EventCoordinator.OnSettingsChanged()`

Replace:
```csharp
_context.InteractionManager.ManualSelectionEnabled = s.ManualSelection.Enabled;
_context.OverlayManager.Toolbar.SetManualSelectionState(s.ManualSelection.Enabled);
```
With:
```csharp
_context.InteractionManager.SelectionMode = s.SelectionMode;
_context.OverlayManager.Toolbar.SetSelectionModeState(s.SelectionMode);
```

---

## Complete File Manifest

### New files (7)

| File | Purpose |
|------|---------|
| `EyeCtrl.Core/Models/SelectionMode.cs` | Mode enum |
| `EyeCtrl.Core/Models/PhysicalRect.cs` | Framework-agnostic rectangle |
| `EyeCtrl.Core/Models/ControlInfo.cs` | Resolved control model |
| `EyeCtrl.Core/Interfaces/IControlFinder.cs` | Control detection interface |
| `EyeCtrl.Core/Settings/AutoSelectionSettings.cs` | Auto-specific settings |
| `EyeCtrl.Platform/Services/ControlFinder.cs` | UIA + Win32 implementation |
| `EyeCtrl.App/Overlays/TargetHighlightWindow.xaml[.cs]` | Highlight overlay window |

### Modified files (11)

| File | Change |
|------|--------|
| `EyeCtrl.Core/Models/InteractionState.cs` | No change (no new states needed) |
| `EyeCtrl.Core/Processing/InteractionManager.cs` | `SelectionMode` property, `_autoSelectionTarget` field, branch in `OnActivationButtonDwelled()`, Auto arm in `OnActionSelected()`, remove `ManualSelectionEnabled` |
| `EyeCtrl.Core/Settings/EyeCtrlSettings.cs` | Add `SelectionMode` + `AutoSelection` properties + Validate arm |
| `EyeCtrl.Core/Settings/SettingsService.cs` | Add `JsonStringEnumConverter` to `JsonOptions` |
| `EyeCtrl.ViewModels/Messages/InteractionMessages.cs` | Add `SelectionModeChangedMessage`; rename `ActionRequestedMessage.IsFromManualSelection` → `IsPreciseTarget`; add `ControlInfo? ResolvedControl` to `ShowActionMenuMessage` |
| `EyeCtrl.ViewModels/ToolbarViewModel.cs` | 3-mode cycle replaces toggle, `SelectionModeChangedMessage` send/receive, remove `IsManualSelectionOn` |
| `EyeCtrl.ViewModels/SettingsViewModel.cs` | `BuildTabs`: remove toggle, add mode combo + Auto settings; `ResetAllSettings`: add AutoSelection + top-level props |
| `EyeCtrl.App/Services/GazeEngineAdapter.cs` | Handle `SelectionModeChangedMessage`, remove `ManualSelectionToggledMessage`, set `IsPreciseTarget` for Auto mode |
| `EyeCtrl.App/Services/EventCoordinator.cs` | `OnSettingsChanged`: use `SelectionMode`; handle highlight overlay lifecycle from `ShowActionMenuMessage.ResolvedControl`; rename `IsFromManualSelection` → `IsPreciseTarget` |
| `EyeCtrl.App/Services/OverlayManager.cs` | Add `TargetHighlight` property + initialization |
| `EyeCtrl.App/Services/OverlayViewModels.cs` | Add `TargetHighlight` ViewModel property |
| `EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs` | `SetSelectionModeState()` replaces `SetManualSelectionState()` |

### Removed (2)

| Item | Reason |
|------|--------|
| `ManualSelection.Enabled` property (settings + `InteractionManager`) | Replaced by `SelectionMode` |
| `ManualSelectionToggledMessage` | Replaced by `SelectionModeChangedMessage` |

### Renamed (1)

| Item | From → To | Reason |
|------|-----------|--------|
| `ActionRequestedMessage.IsFromManualSelection` | → `IsPreciseTarget` | Generalized to cover Auto mode too |

---

## Error Handling

- `ControlFinder.FindTarget()` wraps all UIA calls in try/catch. COM exceptions, `ElementNotAvailableException`, or missing UIA assemblies → return `null`.
- `Task.Run` with 500ms timeout for the UIA call prevents dispatcher hangs.
- If no control found and `FallbackToPixelOnNoControl` is `true` → `InteractionManager` proceeds with raw pixel targeting (behaves like None mode).
- If no control found and `FallbackToPixelOnNoControl` is `false` → `ShowActionMenu` is not invoked, `FixationPoint` is cleared, state resets to `FreeLook`. The user sees no menu and must refixate.

---

## Testing

### Unit tests (`EyeCtrl.Core.Tests`)

```csharp
OnActivationButtonDwelled_NoneMode_ShowsActionMenuAtFixationPoint()
OnActivationButtonDwelled_AutoMode_ShowsActionMenuAtControlCenter()
OnActivationButtonDwelled_AutoMode_NoControl_FallsBackToPixel()
OnActionSelected_AutoMode_ReturnsStoredTargetPoint()
SelectionMode_SetProperty_ValuePersists()
```

### Unit tests (`EyeCtrl.ViewModels.Tests`)

```csharp
ToolbarViewModel_DwellOnSelectionMode_CyclesModes()
ToolbarViewModel_ReceivesSelectionModeChangedMessage_UpdatesButton()
SettingsViewModel_BuildTabs_Removes_ManualSelectionEnabled_Toggle()
SettingsViewModel_BuildTabs_IncludesSelectionMode_Combo()
SettingsViewModel_ResetAllSettings_ResetsSelectionMode_And_AutoSelection()
```

### Platform tests (`EyeCtrl.Platform.Tests`)

```csharp
ControlFinder_FindTarget_ReturnsControlInfoForButton()
ControlFinder_FindTarget_ReturnsNullForDesktopEmptyArea()
ControlFinder_FindTarget_ReturnsNullWhenUIAUnavailable()
```

These need a live desktop session. Mark with `[SkippableFact]` or conditional `[Fact(Skip = "Requires interactive desktop")]` so they don't fail on headless CI — consistent with the existing pattern of graceful skips for environment-dependent code (see `EyeCtrl.Benchmarks` pre-flight checks in AGENTS.md).
