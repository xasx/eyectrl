# Consolidate ViewPosition into a Single Shared Setting

**Date:** 2026-05-18
**Status:** Design

## Problem

`ManualSelectionSettings` and `ActionMenuSettings` each have their own `Position` setting (`ViewPosition` enum: `AtGaze` / `AtCenter`). This allows the two views to use different placements, which confuses users. Both views should always use the same placement.

## Solution

Move `ViewPosition` up from the two sub-settings objects to `EyeCtrlSettings` as a single shared `ViewPosition` property. Add a new "Layout" tab in the Settings window to expose it.

## Changes

### 1. `EyeCtrlSettings.cs` — Add shared field

```csharp
[ObservableProperty] private ViewPosition _viewPosition = ViewPosition.AtCenter;
```

Default is `AtCenter` per user preference.

### 2. `ManualSelectionSettings.cs` — Remove Position

Remove line 12:
```csharp
[ObservableProperty] private ViewPosition _position = ViewPosition.AtGaze;
```

### 3. `ActionMenuSettings.cs` — Remove Position

Remove line 14:
```csharp
[ObservableProperty] private ViewPosition _position = ViewPosition.AtGaze;
```

### 4. `GazeEngineAdapter.cs` — Point both reads to shared field

Line 95: `_settings.ActionMenu.Position` → `_settings.ViewPosition`
Line 132: `_settings.ManualSelection.Position` → `_settings.ViewPosition`

### 5. `SettingsWindow.xaml.cs` — New "Layout" tab, remove old Position controls

- Add tab definition: `("Layout", "⊞", "Layout", BuildLayoutPages)`
- New `BuildLayoutPages()` method: one `DwellComboBox` for Position ("At Gaze" / "At Center") plus a reset button
- Remove Position `DwellComboBox` from `BuildManualSelectionPages()` (lines 510-514)
- Remove Position `DwellComboBox` from `BuildActionMenuPages()` (lines 555-559)
- Remove `_settings.ManualSelection.Position = z.Position` from ManualSelection reset (line 535)
- Remove `_settings.ActionMenu.Position = a.Position` from ActionMenu reset (line 564)
- Add `_settings.ViewPosition = d.ViewPosition` to Reset ALL block
- Add `_settings.ManualSelection.ManualSelectionGracePeriodMs` to Reset ALL block (currently missing)

### 6. `ViewPositionSettingsTests.cs` — Single shared setting tests

Replace 6 tests (2 defaults, 2 setters, 2 PropertyChanged for sub-objects) with 3 tests:
- `EyeCtrlSettings_DefaultViewPosition_IsAtCenter`
- `EyeCtrlSettings_CanSetViewPosition_ToAtGaze`
- `EyeCtrlSettings_ViewPosition_RaisesPropertyChanged`

### 7. `SettingsSerializationTests.cs` — Update references

Update 5 tests to reference `EyeCtrlSettings.ViewPosition` instead of sub-object positions.

### 8. `SettingsViewModel.cs` — No changes

Reflection-based `ResetAll` copies `ViewPosition` automatically.

## Backward Compatibility

Old JSON files have `"ManualSelection":{"Position":"AtGaze"}` and `"ActionMenu":{"Position":"AtGaze"}`. System.Text.Json ignores unknown properties by default, so old settings deserialize without error — `ViewPosition` gets its default (`AtCenter`). Users who previously used `AtGaze` on the old separate settings will need to switch it once.

## Not Changing

- `ViewPosition` enum (`AtGaze`, `AtCenter`) — unchanged
- `ManualSelectionLayoutHelper.CalculateDisplayPoint` — unchanged (already takes `ViewPosition` as parameter)
- `ActionMenuLayoutHelper.CalculateDisplayPoint` — unchanged
- `DwellComboBox` — unchanged
