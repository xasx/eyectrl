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
    Auto       → if AutoSelectionTarget != null
                     ShowActionMenu(AutoSelectionTarget.CenterPoint)
                 else
                     (fallback — see §No-target fallback)
```

A new settable property `AutoSelectionTarget` holds the resolved `ControlInfo?`. It is **set externally** — by `GazeEngineAdapter` before calling `OnActivationButtonDwelled()` (see [Async orchestration](#diasync-boundary)). It is cleared in `ResetToFreeLook()` to prevent stale reuse.

No new `InteractionState` values. Auto mode uses the existing `ActionMenuVisible` state. The `Fixation → ActionMenuVisible → FreeLook` chain is shared with None mode.

### Click path — the critical fix

Currently the click target is determined in two places that must both be changed:

**A) `InteractionManager.OnActionSelected()`** (line 84-91). The switch that resolves `targetPoint` from `CurrentState` must gain an Auto arm. The stored `AutoSelectionTarget` is used when the state is `ActionMenuVisible` **and** the mode is `AutoSelection` **and** a target was resolved:

```
OnActionSelected(action):
  targetPoint = CurrentState switch {
    ActionMenuVisible when SelectionMode == AutoSelection && AutoSelectionTarget != null
                                        => AutoSelectionTarget.CenterPoint,
    ActionMenuVisible                   => FixationPoint,
    ManualSelectionActionMenuVisible    => ManualSelectionDwellPoint,
    _                                   => null
  }
```

The `&& AutoSelectionTarget != null` guard is essential: when `FallbackToPixelOnNoControl` is true and no control was found, the menu is shown at `FixationPoint` with state `ActionMenuVisible` and mode still `AutoSelection` — without the guard this arm matches but yields null (since `AutoSelectionTarget` is null), and `targetPoint == null` at line 93 silently swallows the click. With the guard, it falls through to the raw-pixel arm below.

**B) `GazeEngineAdapter.OnActionRequested()`** (line 158). Currently:
```csharp
var isFromManualSelection = _interactionManager.CurrentState == InteractionState.ManualSelectionActionMenuVisible;
```
The message field controls whether `EventCoordinator` replaces the target with `StableCentroid` (line 687). For Auto mode the target must also survive that override, because after dwelling on the action menu the user's gaze is on the menu ring, not on the control.

**Change:** the message field is renamed `IsPreciseTarget` (meaning: this point was resolved precisely and must NOT be overwritten by live gaze). Set it true for both ManualSelection state AND Auto mode:

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

### `ResetToFreeLook()`

Must clear the new field:
```
ResetToFreeLook():
  FixationPoint = null
  ManualSelectionDwellPoint = null
  AutoSelectionTarget = null        // NEW
  SetState(FreeLook)
```

### Deprecated / removed

| Item | Reason |
|------|--------|
| `ManualSelectionEnabled` bool on `InteractionManager` | Replaced by `SelectionMode` |
| `ManualSelectionToggledMessage` | Replaced by `SelectionModeChangedMessage` |
| Toolbar ManualSelection toggle button | Replaced by cycling selection mode button |

---

## Control Detection

### `IControlFinder` (new interface)

```
EyeCtrl.Core/Interfaces/IControlFinder.cs
```

```csharp
ControlInfo? FindTarget(PhysicalPoint point);
```

Synchronous method. Core doesn't deal with async — the async orchestration (thread hop + timeout) happens in the App layer (see [Async boundary](#diasync-boundary)).

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

The Platform project already has `EnableWindowsTargeting=true` ([EyeCtrl.Platform.csproj:7](src/EyeCtrl.Platform/EyeCtrl.Platform.csproj:7)), which gives access to Win32 APIs. For UIA the project needs a new package reference: `System.Windows.Automation` (the managed UIA client library — adding a `net10.0-windows` TFM variant to the Platform csproj alongside the existing `net10.0` provides UIA access without breaking the non-Windows TFM).

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

### DI + Async boundary

`IControlFinder` is **not** injected into `InteractionManager` (which is not DI-constructed — it's created by `GazeEngine` via `new InteractionManager { ... }` ([GazeEngine.cs:56](src/EyeCtrl.Core/GazeEngine.cs:56))). Instead, the async orchestration lives in the App layer (`GazeEngineAdapter`), which already owns the `Dispatcher`, `IMessenger`, and DI references.

`GazeEngineAdapter` receives `IControlFinder` via its constructor (DI from `AppBootstrapper`). The `ActivationCompletedMessage` handler (line 50-51) becomes:

```
messenger.Register<ActivationCompletedMessage>(this, async (_, _) =>
{
    if (_interactionManager.SelectionMode == SelectionMode.AutoSelection)
    {
        var fixationPoint = _interactionManager.FixationPoint;
        if (fixationPoint != null)
        {
            var target = await ResolveTargetAsync(fixationPoint.Value);
            _interactionManager.AutoSelectionTarget = target;
        }
    }
    _interactionManager.OnActivationButtonDwelled();
});

async Task<ControlInfo?> ResolveTargetAsync(PhysicalPoint point)
{
    try
    {
        return await Task.Run(() => _controlFinder.FindTarget(point))
            .WaitAsync(TimeSpan.FromMilliseconds(500));  // .NET 6+
    }
    catch (TimeoutException)
    {
        _logger.LogWarning("ControlFinder timed out after 500ms — falling back to pixel");
        return null;
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "ControlFinder failed — falling back to pixel");
        return null;
    }
}
```

This keeps `InteractionManager.OnActivationButtonDwelled()` and `IControlFinder.FindTarget()` both synchronous and testable. The async hop + timeout + error handling live entirely in `GazeEngineAdapter`. `InteractionManager` just reads the pre-resolved `AutoSelectionTarget` property when it's called.

**Threading note:** `FindTarget` is called once per activation (not per gaze frame) on a background thread. UIA `FromPoint` cross-process COM calls typically cost 10-150ms. The 500ms timeout is conservative — protects against hung COM servers without rejecting legitimate calls.

---

## Visual Feedback

### Target Highlight Overlay

When Auto Selection resolves a control, a new overlay draws a colored border rectangle around the control's `BoundingRect`.

- **ViewModel:** `TargetHighlightOverlayViewModel` (in `EyeCtrl.ViewModels`). Holds `BoundingRect: PhysicalRect`, `HighlightColor`, `HighlightThickness`, `IsVisible`.
- **Window:** `TargetHighlightWindow` (new WPF overlay file `EyeCtrl.App/Overlays/TargetHighlightWindow.xaml` + `.xaml.cs`). Transparent border-less window, topmost, shows a `Border` element with the configured stroke and thickness. No hit-testing (`IsHitTestVisible = false`), `Background = Transparent`.
- **Registration:** `OverlayManager` gains a `TargetHighlight` property (like the existing 9 overlays). Created in `OverlayManager.Initialize()`. Its DataContext is set to `viewModels.TargetHighlight`. `OverlayViewModels` gains a `TargetHighlight` property.
- **Lifecycle:** `EventCoordinator` subscribes to `ShowActionMenuMessage` — when `msg.ResolvedControl != null`, it positions and shows the highlight. Hidden on `ActionRequestedMessage`, `HideActionMenuMessage`, or `BackdropDismissMessage`. Fade-in over 200ms, fade-out on dismiss.
- If fallback to raw pixel occurs (`ResolvedControl` is null), no highlight is shown.

### `ShowActionMenuMessage` addition

The existing record ([InteractionMessages.cs:20](src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs:20)) gains one optional `init` property:

```csharp
public record ShowActionMenuMessage(
    PhysicalPoint Center,
    IReadOnlyList<InteractionAction> Actions,
    bool ShowBack,
    int DwellTimeMs,
    PhysicalPoint GazePoint,
    int ManualSelectionDisplayRadius = 0,
    double CircleSize = 250)
{
    public PhysicalPoint? ManualSelectionCenter { get; init; }
    public int? ManualSelectionRadius { get; init; }
    public ControlInfo? ResolvedControl { get; init; }  // NEW — non-null only in Auto mode
}
```

In `GazeEngineAdapter.OnShowActionMenu()` (line 74-117), when building the message, set:

```csharp
ResolvedControl = (im.SelectionMode == SelectionMode.AutoSelection) ? im.AutoSelectionTarget : null
```

`EventCoordinator` reads `ResolvedControl` from the message to drive the highlight overlay.

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
  "selectionMode": "none",
  "autoSelection": {
    "highlightColor": "#0078D4",
    "highlightThickness": 3.0,
    "fallbackToPixelOnNoControl": true
  }
}
```

Notes on serialization:
- `SettingsService` uses `JsonNamingPolicy.CamelCase` ([SettingsService.cs:12](src/EyeCtrl.Core/Settings/SettingsService.cs:12)) — keys are camelCase.
- Enums serialize as integers by default. To support human-readable JSON, **add `JsonStringEnumConverter`** to `SettingsService.JsonOptions`. This affects `ViewPosition` (serialized as `"atCenter"` via camelCase) and the new `SelectionMode` (`"none"`, `"manualSelection"`, `"autoSelection"`). Existing integer-valued JSON files still deserialize correctly — `JsonStringEnumConverter` falls back to integer parsing.

### Validate() arm

`EyeCtrlSettings.Validate()` gains after the `Theme` arm:

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

The 4 existing buttons (Pause, ManualSelection, Keyboard, Settings) stay — same count, same layout. The "ManualSelection" button becomes a cycling "Selection Mode" button. On each dwell it advances the mode:

```
None → ManualSelection → AutoSelection → None → ...
```

- Button ID changes from `"ManualSelection"` to `"SelectionMode"`
- `OnTargetActivated("SelectionMode")`: computes `(SelectionMode)(((int)_currentSelectionMode + 1) % 3)`, sends `SelectionModeChangedMessage(newMode)`
- `_currentSelectionMode` is tracked as a field, updated by receiving `SelectionModeChangedMessage` (to reflect settings changes)
- `IsManualSelectionOn` property is replaced by `CurrentSelectionMode`
- The button symbol changes per mode: `"⊙"` for None, `"⊕"` for Manual, `"◎"` for Auto

### `SelectionModeChangedMessage` (new)

```csharp
// EyeCtrl.ViewModels/Messages/InteractionMessages.cs
public record SelectionModeChangedMessage(SelectionMode Mode);
```

All existing messages live in this file ([InteractionMessages.cs](src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs)). The `ManualSelectionToggledMessage` record on line 83 is **deleted**.

### ToolbarWindow code-behind

`SetManualSelectionState(bool)` becomes `SetSelectionModeState(SelectionMode)`:
- None: dark gray background (normal)
- Manual: green background (same as current "on" state)
- Auto: blue background (new accent)

### Message flow

```
Toolbar dwell → ToolbarViewModel → messenger.Send(SelectionModeChangedMessage)
                                     ↓
GazeEngineAdapter receives → _interactionManager.SelectionMode = newMode
                                     ↓
         also: _settings.SelectionMode = newMode
```

When settings are saved (`SettingsChangedMessage`), `EventCoordinator.OnSettingsChanged` pushes `s.SelectionMode` to `InteractionManager.SelectionMode` — same pattern as the current `ManualSelectionEnabled` sync ([EventCoordinator.cs:536](src/EyeCtrl.App/Services/EventCoordinator.cs:536)).

### `GazeEngineAdapter` handler changes

Replace `ManualSelectionToggledMessage` registration (lines 58-62) with:

```csharp
messenger.Register<SelectionModeChangedMessage>(this, (_, msg) =>
{
    _interactionManager.SelectionMode = msg.Mode;
    _settings.SelectionMode = msg.Mode;
});
```

---

## Settings Window Integration

### `BuildTabs()` in `SettingsViewModel`

**ManualSelection tab** (lines 71-76) is renamed to "Selection". Items:

1. `ComboSettingViewModel("selectionMode", "Selection Mode", ...)` — values from `Enum.GetNames<SelectionMode>()`, formatted as "None", "Manual", "Auto". Bound to `_settings.SelectionMode`.
2. (Existing) Circle Radius slider — unchanged, always visible
3. (Existing) Magnification slider — unchanged, always visible
4. (New) `ColorSettingViewModel("highlightColor", "Highlight Color", ...)` — bound to `_settings.AutoSelection.HighlightColor`
5. (New) `SliderSettingViewModel("highlightThickness", "Highlight Thickness", ...)` — bound to `_settings.AutoSelection.HighlightThickness`, range 1–10

The `ToggleSettingViewModel("ManualSelectionEnabled", ...)` on line 73 is **deleted**.

Auto-specific settings (items 4-5) are always visible. To semantically gate them behind Auto mode, add an `IsEnabled` property to `SettingItemViewModel` (default `true`). The combo sets `highlightColor.IsEnabled = highlightThickness.IsEnabled = (mode == AutoSelection)` whenever `SelectionMode` changes. This is two lines of inline logic in `BuildTabs` — no new setting item type needed.

### `ResetAllSettings()`

Add:

```csharp
CopySettings(defaults.AutoSelection, _settings.AutoSelection);
_settings.SelectionMode = defaults.SelectionMode;
_settings.ViewPosition = defaults.ViewPosition;  // previously missing
```

(`ResetAllSettings` currently only copies sub-setting objects, skipping top-level properties ([SettingsViewModel.cs:148-165](src/EyeCtrl.ViewModels/SettingsViewModel.cs:148)). This fix covers `ViewPosition` too.)

### `EventCoordinator.OnSettingsChanged()`

Replace ([lines 536-537](src/EyeCtrl.App/Services/EventCoordinator.cs:536)):
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

### New files (8)

| File | Purpose |
|------|---------|
| `EyeCtrl.Core/Models/SelectionMode.cs` | Mode enum |
| `EyeCtrl.Core/Models/PhysicalRect.cs` | Framework-agnostic rectangle |
| `EyeCtrl.Core/Models/ControlInfo.cs` | Resolved control model |
| `EyeCtrl.Core/Interfaces/IControlFinder.cs` | Control detection interface |
| `EyeCtrl.Core/Settings/AutoSelectionSettings.cs` | Auto-specific settings |
| `EyeCtrl.Platform/Services/ControlFinder.cs` | UIA + Win32 implementation |
| `EyeCtrl.ViewModels/TargetHighlightOverlayViewModel.cs` | Highlight overlay VM |
| `EyeCtrl.App/Overlays/TargetHighlightWindow.xaml` + `.xaml.cs` | Highlight overlay window |

### Modified files (14)

| File | Change |
|------|--------|
| `EyeCtrl.Core/Processing/InteractionManager.cs` | `SelectionMode` property, `AutoSelectionTarget` property, branch in `OnActivationButtonDwelled()`, Auto arm in `OnActionSelected()`, clear in `ResetToFreeLook()`, remove `ManualSelectionEnabled` |
| `EyeCtrl.Core/Settings/EyeCtrlSettings.cs` | Add `SelectionMode` + `AutoSelection` properties + Validate arm |
| `EyeCtrl.Core/Settings/SettingsService.cs` | Add `JsonStringEnumConverter` to `JsonOptions` |
| `EyeCtrl.Core/GazeEngine.cs` | Object initializer: `ManualSelectionEnabled = ...` → `SelectionMode = settings.SelectionMode` |
| `EyeCtrl.ViewModels/Messages/InteractionMessages.cs` | Add `SelectionModeChangedMessage` + `ControlInfo? ResolvedControl` to `ShowActionMenuMessage`; rename `ActionRequestedMessage.IsFromManualSelection` → `IsPreciseTarget`; delete `ManualSelectionToggledMessage` |
| `EyeCtrl.ViewModels/ToolbarViewModel.cs` | 3-mode cycle replaces toggle, `SelectionModeChangedMessage` send/receive, `CurrentSelectionMode` replaces `IsManualSelectionOn` |
| `EyeCtrl.ViewModels/SettingsViewModel.cs` | `BuildTabs`: remove toggle, add mode combo + Auto settings with `IsEnabled` gating; `ResetAllSettings`: add AutoSelection + top-level props; add `IsEnabled` to `SettingItemViewModel` |
| `EyeCtrl.App/Services/GazeEngineAdapter.cs` | DI `IControlFinder`, async `ResolveTargetAsync` in `ActivationCompletedMessage` handler, handle `SelectionModeChangedMessage`, remove `ManualSelectionToggledMessage`, set `IsPreciseTarget` for Auto mode, set `ResolvedControl` in `ShowActionMenuMessage` |
| `EyeCtrl.App/Services/EventCoordinator.cs` | `OnSettingsChanged`: use `SelectionMode`; handle highlight overlay lifecycle from `ShowActionMenuMessage.ResolvedControl`; rename `IsFromManualSelection` → `IsPreciseTarget` in `HandleActionRequested` |
| `EyeCtrl.App/Services/OverlayManager.cs` | Add `TargetHighlight` property + create/initialize in `Initialize()` |
| `EyeCtrl.App/Services/OverlayViewModels.cs` | Add `TargetHighlight` ViewModel property |
| `EyeCtrl.App/Services/AppBootstrapper.cs` | Register `IControlFinder` → `ControlFinder` in DI |
| `EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs` | `SetSelectionModeState()` replaces `SetManualSelectionState()`, selection mode button ID change |
| `EyeCtrl.Platform/EyeCtrl.Platform.csproj` | Add `net10.0-windows` TFM (or `PackageReference` to `System.Windows.Automation`) |

### Renamed (1)

| Item | From → To | Reason |
|------|-----------|--------|
| `ActionRequestedMessage.IsFromManualSelection` | → `IsPreciseTarget` | Generalized to cover Auto mode too |

### Deleted (2)

| Item | Reason |
|------|--------|
| `ManualSelection.Enabled` property (settings + `InteractionManager`) | Replaced by `SelectionMode` |
| `ManualSelectionToggledMessage` | Replaced by `SelectionModeChangedMessage` |

---

## Error Handling

- `ControlFinder.FindTarget()` wraps all UIA calls in try/catch. COM exceptions, `ElementNotAvailableException`, or missing UIA assemblies → return `null`.
- `GazeEngineAdapter.ResolveTargetAsync()` wraps `FindTarget` in `Task.Run` + `WaitAsync(500ms)`. On timeout or exception → return `null`.
- If no control found and `FallbackToPixelOnNoControl` is `true` → `InteractionManager` proceeds with raw pixel targeting (behaves like None mode) — `AutoSelectionTarget` is null, so `OnActivationButtonDwelled()` shows action menu at `FixationPoint`, and `OnActionSelected()` falls through the guard to return `FixationPoint`.
- If no control found and `FallbackToPixelOnNoControl` is `false` → `ShowActionMenu` is not invoked, `FixationPoint` is cleared, state resets to `FreeLook`. The user sees no menu and must refixate.

---

## Testing

### Unit tests (`EyeCtrl.Core.Tests`)

```csharp
OnActivationButtonDwelled_AutoMode_WithTarget_ShowsActionMenuAtControlCenter()
OnActivationButtonDwelled_AutoMode_NoTarget_FallbackToPixel_ShowsActionMenuAtFixationPoint()
OnActionSelected_AutoMode_ReturnsStoredTargetPoint()
OnActionSelected_AutoMode_NoTarget_FallbackToFixationPoint()
ResetToFreeLook_ClearsAutoSelectionTarget()
```

**Test site note:** ~8 tests do `new InteractionManager()` with no args ([InteractionManagerTests.cs](tests/EyeCtrl.Core.Tests/Processing/InteractionManagerTests.cs)). Since `SelectionMode` is a settable property (default `None`), and `AutoSelectionTarget` is nullable by default — no ctor change needed. Existing tests are unaffected. New tests set up mode + target via property assignment.

### Unit tests (`EyeCtrl.ViewModels.Tests`)

```csharp
ToolbarViewModel_DwellOnSelectionMode_CyclesModes()
ToolbarViewModel_ReceivesSelectionModeChangedMessage_UpdatesButton()
SettingsViewModel_BuildTabs_IncludesSelectionMode_Combo()
SettingsViewModel_BuildTabs_AutoSettings_Disabled_WhenModeIsNotAuto()
SettingsViewModel_ResetAllSettings_ResetsSelectionMode_And_AutoSelection_And_ViewPosition()
```

**Test site note:** `ManualSelectionToggle_SendsManualSelectionToggledMessage` ([ToolbarViewModelTests.cs:49](tests/EyeCtrl.ViewModels.Tests/ToolbarViewModelTests.cs:49)) is updated to test the new button ID (`"SelectionMode"`) and new message type.

### Platform tests (`EyeCtrl.Platform.Tests`)

```csharp
ControlFinder_FindTarget_ReturnsControlInfoForButton()
ControlFinder_FindTarget_ReturnsNullForDesktopEmptyArea()
ControlFinder_FindTarget_ThrowsOnUIAUnavailable_ReturnsNull()
```

These need a live desktop session. Mark with `[SkippableFact]` or `[Fact(Skip = "Requires interactive desktop")]` so they don't fail on headless CI — consistent with the existing graceful-skip pattern for environment-dependent code (see `EyeCtrl.Benchmarks` pre-flight checks in AGENTS.md).
