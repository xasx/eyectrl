# Consolidate ViewPosition into Shared Setting — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move `ViewPosition` from `ManualSelectionSettings` and `ActionMenuSettings` up to `EyeCtrlSettings` as a single shared property (default `AtCenter`), add a "Layout" settings tab, and update all consumers.

**Architecture:** Remove the `_position` field from both sub-settings classes, add `_viewPosition` to the parent `EyeCtrlSettings`. All reads in `GazeEngineAdapter` point to the new shared property. A new "Layout" tab in the Settings window exposes the single `DwellComboBox`.

**Tech Stack:** C#, .NET 10, CommunityToolkit.Mvvm source generators, xUnit

---

### Task 1: Add `_viewPosition` to `EyeCtrlSettings`

**Files:**
- Modify: `src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs:14`

- [ ] **Step 1: Add the field**

After line 14 (`[ObservableProperty] private GeneralSettings _general = new();`), insert:

```csharp
    [ObservableProperty] private ViewPosition _viewPosition = ViewPosition.AtCenter;
```

This requires adding the import on line 3:
```csharp
using EyeCtrl.Core.Models;
```

The full import block becomes:
```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Models;
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build EyeCtrl.slnx`
Expected: PASS (new property compiles, generates `ViewPosition` public property)

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs
git commit -m "feat: add shared ViewPosition to EyeCtrlSettings"
```

---

### Task 2: Remove `_position` from `ManualSelectionSettings`

**Files:**
- Modify: `src/EyeCtrl.Core/Settings/ManualSelectionSettings.cs:12`

- [ ] **Step 1: Remove the field**

Delete line 12:
```csharp
    [ObservableProperty] private ViewPosition _position = ViewPosition.AtGaze;
```

- [ ] **Step 2: Remove unused import**

The `using EyeCtrl.Core.Models;` import was only needed for `ViewPosition`. Since the field is gone, remove line 4:
```csharp
using EyeCtrl.Core.Models;
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build EyeCtrl.slnx`
Expected: FAIL (callers still reference `ManualSelectionSettings.Position` — expected at this point)

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.Core/Settings/ManualSelectionSettings.cs
git commit -m "refactor: remove Position from ManualSelectionSettings"
```

---

### Task 3: Remove `_position` from `ActionMenuSettings`

**Files:**
- Modify: `src/EyeCtrl.Core/Settings/ActionMenuSettings.cs:14`

- [ ] **Step 1: Remove the field**

Delete line 14:
```csharp
    [ObservableProperty] private ViewPosition _position = ViewPosition.AtGaze;
```

- [ ] **Step 2: Remove unused import**

Remove line 4:
```csharp
using EyeCtrl.Core.Models;
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build EyeCtrl.slnx`
Expected: FAIL (same as Task 2 — GazeEngineAdapter still references old properties)

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.Core/Settings/ActionMenuSettings.cs
git commit -m "refactor: remove Position from ActionMenuSettings"
```

---

### Task 4: Update `GazeEngineAdapter` to use shared `ViewPosition`

**Files:**
- Modify: `src/EyeCtrl.App/Services/GazeEngineAdapter.cs:95,132`

- [ ] **Step 1: Change ActionMenu read**

Line 95:
```csharp
                _settings.ActionMenu.Position,
```
→
```csharp
                _settings.ViewPosition,
```

- [ ] **Step 2: Change ManualSelection read**

Line 132:
```csharp
                _settings.ManualSelection.Position,
```
→
```csharp
                _settings.ViewPosition,
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build EyeCtrl.slnx`
Expected: PASS (GazeEngineAdapter now compiles; SettingsWindow.xaml.cs still references old properties)

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.App/Services/GazeEngineAdapter.cs
git commit -m "refactor: point GazeEngineAdapter at shared ViewPosition"
```

---

### Task 5: Update `SettingsWindow.xaml.cs` — Add Layout tab, remove old Position controls

**Files:**
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs:81-90,498-571,728-766`

- [ ] **Step 1: Add Layout tab definition**

In the `_tabDefs` array, add after line 86 (`("Actions", ...)`):
```csharp
            ("Layout",   "⊞", "Layout",      BuildLayoutPages),
```

The tab list (lines 82-90) becomes:
```csharp
        _tabDefs =
        [
            ("Cursor",   "◎", "Cursor",      BuildCursorPages),
            ("Dwell",    "⏱", "Dwell Times", BuildDwellPages),
            ("ManualSelection",     "⊕", "ManualSelection",        BuildManualSelectionPages),
            ("Actions",  "⊙", "Action Menu", BuildActionMenuPages),
            ("Layout",   "⊞", "Layout",      BuildLayoutPages),
            ("Keyboard", "⌨", "Keyboard",    BuildKeyboardPages),
            ("Toolbar",  "▣", "Toolbar",     BuildToolbarPages),
            ("General",  "⚙", "General",     BuildGeneralPages)
        ];
```

- [ ] **Step 2: Create `BuildLayoutPages` method**

Insert between `BuildActionMenuPages()` and `BuildKeyboardPages()` (after line 571). The new method:

```csharp
    private List<List<Action>> BuildLayoutPages()
    {
        var r = DpiHelper.LogicalToPhysicalX(ControlBtnRadius);
        var cat = DwellCategory.Control;

        return
        [
            [
                () => Add(new DwellComboBox(_viewModel, "lay_pos", "Position",
                    ["At Gaze", "At Center"],
                    () => _settings.ViewPosition == ViewPosition.AtGaze ? "At Gaze" : "At Center",
                    v => { _settings.ViewPosition = v == "At Gaze" ? ViewPosition.AtGaze : ViewPosition.AtCenter; ApplyAndSave(); },
                    cat, r, _dwellMs)),
                () => Add(new DwellButton(_viewModel, "lay_reset", "Reset Layout Settings",
                    () => ShowModal("Reset Layout settings to defaults?", () =>
                    {
                        var d = new EyeCtrlSettings();
                        _settings.ViewPosition = d.ViewPosition;
                        ApplyAndSave();
                        RebuildContent();
                    }),
                    ResetBase, cat, r, _dwellMs, width: 300, height: 80)),
            ]
        ];
    }
```

- [ ] **Step 3: Remove Position `DwellComboBox` from `BuildManualSelectionPages`**

Delete lines 510-514 (the entire Position combo box entry):

Remove:
```csharp
                () => Add(new DwellComboBox(_viewModel, "z_pos", "Position",
                    ["At Gaze", "At Center"],
                    () => _settings.ManualSelection.Position == ViewPosition.AtGaze ? "At Gaze" : "At Center",
                    v => { _settings.ManualSelection.Position = v == "At Gaze" ? ViewPosition.AtGaze : ViewPosition.AtCenter; ApplyAndSave(); },
                    cat, r, _dwellMs)),
```

- [ ] **Step 4: Remove `_settings.ManualSelection.Position` from ManualSelection reset**

In the reset block (line 535), delete:
```csharp
                        _settings.ManualSelection.Position = z.Position;
```

- [ ] **Step 5: Remove Position `DwellComboBox` from `BuildActionMenuPages`**

Delete lines 555-559:

Remove:
```csharp
                () => Add(new DwellComboBox(_viewModel, "am_pos", "Position",
                    ["At Gaze", "At Center"],
                    () => _settings.ActionMenu.Position == ViewPosition.AtGaze ? "At Gaze" : "At Center",
                    v => { _settings.ActionMenu.Position = v == "At Gaze" ? ViewPosition.AtGaze : ViewPosition.AtCenter; ApplyAndSave(); },
                    cat, r, _dwellMs)),
```

- [ ] **Step 6: Remove `_settings.ActionMenu.Position` from ActionMenu reset**

In the reset block (line 564), delete:
```csharp
                        _settings.ActionMenu.Position = a.Position;
```

- [ ] **Step 7: Add `ViewPosition` and `ManualSelectionGracePeriodMs` to Reset ALL**

In the Reset ALL block (after line 749), add two lines:
```csharp
                        _settings.ViewPosition = d.ViewPosition;
                        _settings.ManualSelection.ManualSelectionGracePeriodMs = d.ManualSelection.ManualSelectionGracePeriodMs;
```

- [ ] **Step 8: Build to verify**

Run: `dotnet build EyeCtrl.slnx`
Expected: PASS

- [ ] **Step 9: Commit**

```bash
git add src/EyeCtrl.App/Views/SettingsWindow.xaml.cs
git commit -m "feat: add Layout tab with shared ViewPosition, remove old Position controls"
```

---

### Task 6: Update `ViewPositionSettingsTests.cs`

**Files:**
- Overwrite: `tests/EyeCtrl.Core.Tests/Settings/ViewPositionSettingsTests.cs`

- [ ] **Step 1: Replace entire file with 3 tests against `EyeCtrlSettings.ViewPosition`**

```csharp
namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

public class ViewPositionSettingsTests
{
    [Fact]
    public void EyeCtrlSettings_DefaultViewPosition_IsAtCenter()
    {
        var settings = new EyeCtrlSettings();

        Assert.Equal(ViewPosition.AtCenter, settings.ViewPosition);
    }

    [Fact]
    public void EyeCtrlSettings_CanSetViewPosition_ToAtGaze()
    {
        var settings = new EyeCtrlSettings
        {
            ViewPosition = ViewPosition.AtGaze
        };

        Assert.Equal(ViewPosition.AtGaze, settings.ViewPosition);
    }

    [Fact]
    public void EyeCtrlSettings_ViewPosition_RaisesPropertyChanged()
    {
        var settings = new EyeCtrlSettings();
        bool propertyChanged = false;
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EyeCtrlSettings.ViewPosition))
                propertyChanged = true;
        };

        settings.ViewPosition = ViewPosition.AtGaze;

        Assert.True(propertyChanged);
    }
}
```

- [ ] **Step 2: Run the tests to verify**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~ViewPositionSettingsTests"`
Expected: 3 PASS

- [ ] **Step 3: Commit**

```bash
git add tests/EyeCtrl.Core.Tests/Settings/ViewPositionSettingsTests.cs
git commit -m "test: update ViewPosition tests for shared EyeCtrlSettings.ViewPosition"
```

---

### Task 7: Update `SettingsSerializationTests.cs`

**Files:**
- Modify: `tests/EyeCtrl.Core.Tests/Settings/SettingsSerializationTests.cs:121-175`

- [ ] **Step 1: Replace the 5 position-related test methods**

Delete lines 121-175 (all 5 position tests) and replace with:

```csharp
    [Fact]
    public void EyeCtrlSettings_ViewPosition_RoundTrips()
    {
        var settings = new EyeCtrlSettings { ViewPosition = ViewPosition.AtGaze };
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<EyeCtrlSettings>(json, JsonOptions)!;
        Assert.Equal(ViewPosition.AtGaze, deserialized.ViewPosition);
    }

    [Fact]
    public void EyeCtrlSettings_ViewPositions_RoundTripTogether()
    {
        var original = new EyeCtrlSettings();
        original.ViewPosition = ViewPosition.AtGaze;

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<EyeCtrlSettings>(json, JsonOptions)!;

        Assert.Equal(ViewPosition.AtGaze, deserialized.ViewPosition);
    }

    [Fact]
    public void Settings_BackwardsCompatible_MissingViewPosition_UsesDefault()
    {
        // Old settings JSON without ViewPosition property
        var json = """
        {
          "ManualSelection": {
            "enabled": true,
            "circleRadiusPixels": 150,
            "magnificationLevel": 2.0
          },
          "actionMenu": {
            "visibleActions": [0, 1, 2]
          }
        }
        """;

        var settings = JsonSerializer.Deserialize<EyeCtrlSettings>(json, JsonOptions)!;

        // Should default to AtCenter (the new shared default)
        Assert.Equal(ViewPosition.AtCenter, settings.ViewPosition);
    }
```

- [ ] **Step 2: Run the tests to verify**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~SettingsSerializationTests"`
Expected: All PASS (including the 3 new ones)

- [ ] **Step 3: Commit**

```bash
git add tests/EyeCtrl.Core.Tests/Settings/SettingsSerializationTests.cs
git commit -m "test: update serialization tests for shared ViewPosition"
```

---

### Task 8: Update UI test tab expectations

**Files:**
- Modify: `tests/EyeCtrl.App.Tests/Tests/SettingsTests.cs:54-63`

- [ ] **Step 1: Add "Settings_Tab_Layout" to expected tabs array**

After `"Settings_Tab_ManualSelection"` on line 58, insert:
```csharp
            "Settings_Tab_Layout",
```

The array (lines 54-63) becomes:
```csharp
        var expectedTabs = new[]
        {
            "Settings_Tab_Cursor",
            "Settings_Tab_Dwell",
            "Settings_Tab_ManualSelection",
            "Settings_Tab_Layout",
            "Settings_Tab_Keyboard",
            "Settings_Tab_Toolbar",
            "Settings_Tab_Theme",
            "Settings_Tab_General"
        };
```

Note: The Actions tab appears to be omitted from this test already (it checks `Actions` not `ActionMenu`). We only add `Layout` and preserve the existing pattern.

- [ ] **Step 2: Commit**

```bash
git add tests/EyeCtrl.App.Tests/Tests/SettingsTests.cs
git commit -m "test: add Layout tab to UI tab expectations"
```

---

### Task 9: Full build and test suite

- [ ] **Step 1: Build everything**

Run: `dotnet build EyeCtrl.slnx`
Expected: PASS

- [ ] **Step 2: Run all unit tests**

Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: All PASS

- [ ] **Step 3: Run ViewModel tests**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests`
Expected: All PASS

- [ ] **Step 4: Run all tests**

Run: `dotnet test`
Expected: All PASS (UI tests skip if no display)

- [ ] **Step 5: Commit if anything changed**

```bash
git status
```
