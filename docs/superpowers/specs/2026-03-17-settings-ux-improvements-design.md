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

`AddCombo` renders a `◀ value ▶` row that hides available options behind cycling arrows. `AddRadio` renders a vertical stack of full-width buttons. Both are suboptimal for settings with 3–5 named options: all choices should be immediately visible and dwell-selectable without paging through them.

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
- A `WrapPanel` with `Orientation = Horizontal` and `Margin = (0, 2, 0, 12)` containing one `Border` per option.
- Each button: `Height = 70`, `CornerRadius = 8`, `MinWidth = 150`, left/right `Padding = 20`, auto-width based on label length.
- Selected button background: `Color.FromArgb(200, 30, 100, 30)` (green, matching existing selected state).
- Unselected button background: `BtnBase` = `Color.FromArgb(200, 60, 60, 80)`.
- Button label text: `◉ DisplayName` when selected, `◎ DisplayName` when unselected (matching current AddRadio style).
- Font: `FontSize = 22`, `FontWeight = SemiBold`, `Foreground = White`.
- Each button registers a `DwellTarget` in `_controlDwell` using the same `ControlBtnRadius` and `_dwellMs` as all other controls in the content area.
- `AutomationProperties.AutomationId` set to `Settings_HList_{idPrefix}_{i}` on the inner `TextBlock` for each button (for UI automation / test compatibility).

**Selection behaviour:** Clicking or dwelling a button calls `setCode(codeOptions[i])` and refreshes all buttons in the group (green/grey + ◉/◎ prefix).

### Call-site mapping

| Old call | Old method | `idPrefix` | `displayOptions` | `codeOptions` |
|---|---|---|---|---|
| `c_filter` | `AddCombo` | `c_filter` | `["OneEuro","Kalman","Adaptive"]` | same |
| `t_edge` | `AddCombo` | `t_edge` | `["Top","Bottom","Left","Right"]` | same |
| `th_mode` | `AddCombo` | `th_mode` | `["System","Light","Dark"]` | same |
| `k_layout` | `AddCombo` | `k_layout` | `["QWERTY","QWERTZ","AZERTY"]` | same |
| `k_lang` | `AddRadio` | `k_lang` | `["English","French","Spanish","German","Polish"]` | `["en","fr","es","de","pl"]` |

For the former `AddCombo` sites, `displayOptions` and `codeOptions` are the same array (value = display label). For `k_lang`, separate arrays are used as before.

### Removed

- `AddCombo` method — deleted.
- `AddRadio` method — deleted.
- `_tabDwell`, `_controlDwell` usage in `AddRadio` is subsumed by `AddHorizontalList`.

---

## Change 2: Circle cursor hidden while settings open

### Motivation

The circle cursor following the user's gaze while the settings window is open causes visual noise and could accidentally trigger dwell actions on the desktop behind the settings overlay.

### Implementation

In `App.xaml.cs`, when showing `SettingsWindow`:

1. Set `CursorOverlay.Visibility = Visibility.Collapsed` before showing the settings window.
2. Restore `CursorOverlay.Visibility = Visibility.Visible` when the settings window is closed.

Wire this via `SettingsWindow.Closed` (or `IsVisibleChanged`) in `App.xaml.cs`, where both windows are already managed.

No changes are needed to `CursorOverlay` itself — collapsing the window is sufficient to suppress rendering and hit-testing.

---

## Files Changed

| File | Change |
|---|---|
| `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` | Add `AddHorizontalList`; delete `AddCombo` and `AddRadio`; update 5 call sites |
| `src/EyeCtrl.App/App.xaml.cs` | Hide/restore `CursorOverlay` around settings window visibility |

---

## Out of Scope

- `AddSlider` (numeric stepping controls) — unchanged.
- `AddToggle`, `AddColorPicker`, `AddLabel`, `AddResetButton` — unchanged.
- Pagination, tab navigation, modal — unchanged.
- Keyboard layout `AddCombo` (already uses `AddCombo` for the layout name, mapped above to `k_layout`).
