# Settings Overlay UX Improvements

**Date:** 2026-03-17
**Status:** Approved

## Overview

Two improvements to the EyeCtrl settings overlay (`SettingsWindow.xaml.cs`):

1. Replace all cycling-arrow option controls (`AddCombo`, `AddRadio`) with a new horizontal button list control (`AddHorizontalList`).
2. Hide the circle cursor while the settings window is visible.

---

## Change 1: `AddHorizontalList` replaces `AddCombo` and `AddRadio`

### Motivation

`AddCombo` renders a `◀ value ▶` row that hides available options behind cycling arrows. `AddRadio` renders a vertical stack of full-width buttons. Both are suboptimal for settings with 2–5 named options: all choices should be immediately visible and dwell-selectable without paging through them.

### New method

```csharp
private void AddHorizontalList(
    string idPrefix,
    string label,
    string[] displayOptions,
    string[] codeOptions,
    Func<string> getCode,
    Action<string> setCode)
```

**Rendering:**
- `AddLabel(label)` above, as with all other controls.
- A `WrapPanel` with `Orientation = Horizontal` and `Margin = (0, 2, 0, 12)` is added to `ContentPanel` as a single child. Wrapping to a second line is acceptable when options overflow the content area width.
- Each `Border` per option is added as a child of the `WrapPanel` (not of `ContentPanel` directly). Each `Border` is also stored in `_controlBorders[id]` for dwell progress updates.
- Each button: `Height = 70`, `CornerRadius = 8`, `MinWidth = 150`, `Padding = (20, 0, 20, 0)`, `Margin = (0, 0, 8, 8)`, auto-width based on label length.
- Selected button background: `Color.FromArgb(200, 30, 100, 30)` (green).
- Unselected button background: `BtnBase` = `Color.FromArgb(200, 60, 60, 80)`.
- Button label text: `◉ DisplayName` when selected, `◎ DisplayName` when unselected (matching current `AddRadio` style).
- Font: `FontSize = 22`, `FontWeight = SemiBold`, `Foreground = White`.
- `AutomationProperties.AutomationId` is set on the inner `TextBlock` to `Settings_HList_{idPrefix}_{i}` (consistent with the `TextBlock`-level ID pattern used in `AddRadio` and `AddToggle`).

**Dwell registration:**
Each button registers a `DwellTarget` in `_controlDwell` with id `{idPrefix}_{i}`, radius `DpiHelper.LogicalToPhysicalX(ControlBtnRadius)`, and dwell time `_dwellMs` — identical to how `AddRadio` registered its targets. The dwell target center is updated by `UpdateTargetPositions()` each frame.

**Dwell progress visual:** `OnControlProgress` always blends from `BtnBase` toward the highlight color. For a currently-selected button (green background), this means dwelling will transiently override the green with a `BtnBase`→highlight blend. This is the same behavior as the existing `AddRadio` and is acceptable.

**Selection and refresh:**
The method captures `List<Border> borders` and `List<TextBlock> texts` across all options in the group. A shared local `Refresh()` method iterates these lists, setting each `Border.Background` and `TextBlock.Text` from `getCode()`. `Refresh()` is called:
- From each option's `_controlActions[id]` entry (invoked on dwell or mouse click).
- Once immediately after all buttons are created, to set the initial selected state.

**Mouse click fallback:** Each border wires `MouseLeftButtonDown` to invoke its `_controlActions` entry, as in `AddRadio`.

### Call-site mapping

These are **all** call sites for `AddCombo` and `AddRadio` in `SettingsWindow.xaml.cs`:

| Old method | ID | Label | `displayOptions` | `codeOptions` |
|---|---|---|---|---|
| `AddCombo` | `c_filter` | Filter Mode | `Enum.GetNames<GazeFilterMode>()` | same (preserve dynamic call) |
| `AddCombo` | `k_layout` | Active Layout | `["QWERTY","QWERTZ","AZERTY"]` | same |
| `AddCombo` | `k_edge` | Screen Edge | `["Bottom","Top"]` | same |
| `AddCombo` | `t_edge` | Docked Edge | `["Top","Bottom","Left","Right"]` | same |
| `AddCombo` | `th_mode` | Theme Mode | `["System","Light","Dark"]` | same |
| `AddRadio` | `k_lang` | Dictionary Language | `["English","French","Spanish","German","Polish"]` | `["en","fr","es","de","pl"]` |

For the 5 former `AddCombo` sites, `displayOptions` and `codeOptions` are the same array. For `k_lang`, separate arrays are used as before. `c_filter` must use `Enum.GetNames<GazeFilterMode>()` dynamically (not a hard-coded string array).

### Removed

- `AddCombo` method — deleted (all 5 call sites migrated).
- `AddRadio` method — deleted (1 call site migrated).
- Both methods only registered targets in `_controlDwell`; no `_tabDwell` entries are affected.

---

## Change 2: Circle cursor hidden while settings open

### Motivation

The circle cursor following the user's gaze while the settings window is open causes visual noise. The settings window manages its own `DwellDetector` instances, so the cursor circle serves no purpose while it is open.

### Implementation

`SettingsWindow` is created fresh each time it is opened (not a singleton reused by hiding).

In `App.xaml.cs`, add a `bool _settingsOpen` field. Modify the `cursorVM.PropertyChanged` handler to suppress re-showing `cursorCircle` while settings is open:

```csharp
case nameof(CursorCircleViewModel.IsVisible):
    if (cursorVM.IsVisible && !_settingsOpen) cursorCircle.Show();
    else cursorCircle.Hide();
    break;
```

When opening the settings window:
1. Set `_settingsOpen = true`.
2. Call `cursorCircle.Hide()` directly (the VM may not change `IsVisible` at this moment).

Subscribe to `settingsWindow.Closed`:
1. Set `_settingsOpen = false`.
2. Call `cursorCircle.Show()` if `cursorVM.IsVisible` is currently true.

This prevents the VM-driven `PropertyChanged` handler from re-showing `cursorCircle` while settings is open, even as gaze data continues to arrive and update the VM.

---

## Files Changed

| File | Change |
|---|---|
| `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` | Add `AddHorizontalList`; delete `AddCombo` and `AddRadio`; update 6 call sites |
| `src/EyeCtrl.App/App.xaml.cs` | Add `_settingsOpen` guard; hide/restore cursor around settings window lifecycle |

---

## Out of Scope

- `AddSlider` (numeric stepping controls) — unchanged.
- `AddToggle`, `AddColorPicker`, `AddLabel`, `AddResetButton` — unchanged.
- Pagination, tab navigation, modal — unchanged.
