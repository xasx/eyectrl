# Zoom → ManualSelection Rename Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rename the Zoom Mode interaction concept to Manual Selection Mode across the entire codebase. ~520 references in 44 C# files + 3 XAML files.

**Architecture:** Global text replacement of `Zoom` → `ManualSelection` across all source, test, and XAML files. Exclusions: visual magnification terms (`MagnificationLevel`, `magnification`, `zoom level` in comments). File renames via `git mv`.

**Tech Stack:** PowerShell / ripgrep for text replacement, git mv for file renames, dotnet build + test for verification.

---

## Identifier Mapping (complete)

All identifiers follow the pattern `XxxZoomYyy` → `XxxManualSelectionYyy` or `ZoomXxx` → `ManualSelectionXxx`:

| Old | New |
|-----|-----|
| `ZoomSettings` | `ManualSelectionSettings` |
| `ZoomOverlayWindow` | `ManualSelectionOverlayWindow` |
| `ZoomGazeIndicatorWindow` | `ManualSelectionGazeIndicatorWindow` |
| `ZoomSideButtonWindow` | `ManualSelectionSideButtonWindow` |
| `ZoomDwellService` | `ManualSelectionDwellService` |
| `ZoomOverlayViewModel` | `ManualSelectionOverlayViewModel` |
| `ZoomGazeIndicatorViewModel` | `ManualSelectionGazeIndicatorViewModel` |
| `ZoomLayoutHelper` | `ManualSelectionLayoutHelper` |
| `ZoomDwellServiceTests` | `ManualSelectionDwellServiceTests` |
| `ZoomOverlayViewModelTests` | `ManualSelectionOverlayViewModelTests` |
| `ZoomHandlerMemoryLeakTests` | `ManualSelectionHandlerMemoryLeakTests` |
| `ZoomDwellTargetId` | `ManualSelectionDwellTargetId` |
| `ZoomActionMenuDwellMs` | `ManualSelectionActionMenuDwellMs` |
| `ZoomGracePeriodMs` | `ManualSelectionGracePeriodMs` |
| `ZoomDwellCompleted` | `ManualSelectionDwellCompleted` |
| `ZoomDwellCompletedMessage` | `ManualSelectionDwellCompletedMessage` |
| `ZoomCloseMessage` | `ManualSelectionCloseMessage` |
| `ZoomRecaptureMessage` | `ManualSelectionRecaptureMessage` |
| `ZoomHiddenMessage` | `ManualSelectionHiddenMessage` |
| `ZoomToggledMessage` | `ManualSelectionToggledMessage` |
| `ZoomCenter` | `ManualSelectionCenter` |
| `ZoomRadius` | `ManualSelectionRadius` |
| `ZoomDisplayRadius` | `ManualSelectionDisplayRadius` |
| `ZoomEnabled` | `ManualSelectionEnabled` |
| `ZoomDwellPoint` | `ManualSelectionDwellPoint` |
| `ZoomSelectionActive` | `ManualSelectionActive` |
| `ZoomSelectionActionMenuVisible` | `ManualSelectionActionMenuVisible` |
| `ZoomIndicator` | `ManualSelectionIndicator` |
| `ZoomIndicatorVM` | `ManualSelectionIndicatorVM` |
| `ZoomVM` | `ManualSelectionVM` |
| `ZoomOverlay` | `ManualSelectionOverlay` |
| `ZoomSideButtons` | `ManualSelectionSideButtons` |
| `ZoomBrush` | `ManualSelectionBrush` |
| `ZoomActiveButtonAlpha` | `ManualSelectionActiveButtonAlpha` |
| `ZoomGazeIndicator` | `ManualSelectionGazeIndicator` |
| `ZoomSideButtonDwellMs` | `ManualSelectionSideButtonDwellMs` |
| `ZoomSideButtonGap` | `ManualSelectionSideButtonGap` |
| `Zoom` (enum value) | `ManualSelection` |
| `Zoom` (property, local vars) | `ManualSelection` |
| `OnZoomStarted` | `OnManualSelectionStarted` |
| `OnZoomEnded` | `OnManualSelectionEnded` |
| `OnZoomDwell` | `OnManualSelectionDwell` |
| `OnZoomBack` | `OnManualSelectionBack` |
| `IsWithinZoomBounds` | `IsWithinManualSelectionBounds` |
| `BuildZoomPages` | `BuildManualSelectionPages` |

---

### Task 1: Rename Core Symbols (settings, models, enum)

**Files affected:** `src/EyeCtrl.Core/`

> **Critical:** Use two-step replacement to avoid `ManualSelectionSelection*` — first replace `ZoomSelection`, then remaining `Zoom`.

- [ ] **Step 1: Replace Zoom → ManualSelection in Core (two-step)**

```powershell
$files = rg -l "Zoom" --type cs src/EyeCtrl.Core/
foreach ($f in $files) {
    $content = Get-Content $f -Raw
    $content = $content -replace 'ZoomSelection', 'ManualSelection'   # step 1: eat combined prefix
    $content = $content -replace 'Zoom', 'ManualSelection'            # step 2: remaining standalone
    Set-Content $f -Value $content -NoNewline
}
```

- [ ] **Step 2: Verify replacement — check for unintended double-"Selection"**

Run: `rg "ManualSelectionSelection" --type cs src/EyeCtrl.Core/`
Expected: Zero results

Run: `rg "\bZoom\w" --type cs src/EyeCtrl.Core/`
Expected: Zero results (all `Zoom*` identifiers replaced)

- [ ] **Step 3: Build Core to verify**

Run: `dotnet build src/EyeCtrl.Core/EyeCtrl.Core.csproj`
Expected: SUCCESS (0 errors)

---

### Task 2: Rename Platform project references

**Files affected:** `src/EyeCtrl.Platform/`

- [ ] **Step 1: Replace Zoom → ManualSelection in Platform (two-step)**

```powershell
$files = rg -l "Zoom" --type cs src/EyeCtrl.Platform/
foreach ($f in $files) {
    $content = Get-Content $f -Raw
    $content = $content -replace 'ZoomSelection', 'ManualSelection'
    $content = $content -replace 'Zoom', 'ManualSelection'
    Set-Content $f -Value $content -NoNewline
}
```

- [ ] **Step 2: Build Platform**

Run: `dotnet build src/EyeCtrl.Platform/EyeCtrl.Platform.csproj`
Expected: SUCCESS

---

### Task 3: Rename ViewModels project

**Files affected:** `src/EyeCtrl.ViewModels/`

- [ ] **Step 1: Replace Zoom → ManualSelection in ViewModels (two-step)**

```powershell
$files = rg -l "Zoom" --type cs src/EyeCtrl.ViewModels/
foreach ($f in $files) {
    $content = Get-Content $f -Raw
    $content = $content -replace 'ZoomSelection', 'ManualSelection'
    $content = $content -replace 'Zoom', 'ManualSelection'
    Set-Content $f -Value $content -NoNewline
}
```

- [ ] **Step 2: Build ViewModels**

Run: `dotnet build src/EyeCtrl.ViewModels/EyeCtrl.ViewModels.csproj`
Expected: SUCCESS

---

### Task 4: Rename App project (services, overlays, views)

**Files affected:** `src/EyeCtrl.App/`

- [ ] **Step 1: Replace Zoom → ManualSelection in App C# files (two-step)**

```powershell
$files = rg -l "Zoom" --type cs src/EyeCtrl.App/
foreach ($f in $files) {
    $content = Get-Content $f -Raw
    $content = $content -replace 'ZoomSelection', 'ManualSelection'
    $content = $content -replace 'Zoom', 'ManualSelection'
    Set-Content $f -Value $content -NoNewline
}
```

- [ ] **Step 2: Update XAML files**

Replace in 3 XAML files:
```powershell
$files = rg -l "Zoom" --type-add 'xaml:*.xaml' --type xaml src/EyeCtrl.App/
foreach ($f in $files) {
    (Get-Content $f -Raw) -replace 'ZoomGazeIndicatorWindow', 'ManualSelectionGazeIndicatorWindow' `
                          -replace 'ZoomSideButtonWindow', 'ManualSelectionSideButtonWindow' `
                          -replace 'ZoomOverlayWindow', 'ManualSelectionOverlayWindow' `
                          -replace 'ZoomBrush', 'ManualSelectionBrush' `
                          -replace 'ZoomGazeIndicator_Window', 'ManualSelectionGazeIndicator_Window' |
        Set-Content $f -NoNewline
}
```

- [ ] **Step 3: Build App**

Run: `dotnet build src/EyeCtrl.App/EyeCtrl.App.csproj`
Expected: SUCCESS

- [ ] **Step 4: Fix any remaining "Zoom" references missed by the batch replace**

Run: `rg "Zoom" --type cs src/EyeCtrl.App/`
Review results. Any `Zoom` identifiers left should be manually fixed.

---

### Task 5: Rename test projects

**Files affected:** `tests/EyeCtrl.Core.Tests/`, `tests/EyeCtrl.Platform.Tests/`, `tests/EyeCtrl.ViewModels.Tests/`, `tests/EyeCtrl.App.Tests/`

- [ ] **Step 1: Replace Zoom → ManualSelection in all test files (two-step)**

```powershell
$testDirs = @(
    "tests/EyeCtrl.Core.Tests",
    "tests/EyeCtrl.Platform.Tests",
    "tests/EyeCtrl.ViewModels.Tests",
    "tests/EyeCtrl.App.Tests"
)
foreach ($dir in $testDirs) {
    $files = rg -l "Zoom" --type cs $dir
    foreach ($f in $files) {
        $content = Get-Content $f -Raw
        $content = $content -replace 'ZoomSelection', 'ManualSelection'
        $content = $content -replace 'Zoom', 'ManualSelection'
        Set-Content $f -Value $content -NoNewline
    }
}
```

- [ ] **Step 2: Build all tests**

Run: `dotnet build tests/EyeCtrl.Core.Tests && dotnet build tests/EyeCtrl.Platform.Tests && dotnet build tests/EyeCtrl.ViewModels.Tests && dotnet build tests/EyeCtrl.App.Tests`
Expected: All SUCCESS

---

### Task 6: Rename files (git mv)

- [ ] **Step 1: Rename all files with "Zoom" in their filename**

```powershell
git mv src/EyeCtrl.Core/Settings/ZoomSettings.cs src/EyeCtrl.Core/Settings/ManualSelectionSettings.cs
git mv src/EyeCtrl.App/Services/ZoomDwellService.cs src/EyeCtrl.App/Services/ManualSelectionDwellService.cs
git mv src/EyeCtrl.App/Overlays/ZoomOverlayWindow.xaml src/EyeCtrl.App/Overlays/ManualSelectionOverlayWindow.xaml
git mv src/EyeCtrl.App/Overlays/ZoomOverlayWindow.xaml.cs src/EyeCtrl.App/Overlays/ManualSelectionOverlayWindow.xaml.cs
git mv src/EyeCtrl.App/Overlays/ZoomGazeIndicatorWindow.xaml src/EyeCtrl.App/Overlays/ManualSelectionGazeIndicatorWindow.xaml
git mv src/EyeCtrl.App/Overlays/ZoomGazeIndicatorWindow.xaml.cs src/EyeCtrl.App/Overlays/ManualSelectionGazeIndicatorWindow.xaml.cs
git mv src/EyeCtrl.App/Overlays/ZoomSideButtonWindow.xaml src/EyeCtrl.App/Overlays/ManualSelectionSideButtonWindow.xaml
git mv src/EyeCtrl.App/Overlays/ZoomSideButtonWindow.xaml.cs src/EyeCtrl.App/Overlays/ManualSelectionSideButtonWindow.xaml.cs
git mv src/EyeCtrl.ViewModels/ZoomOverlayViewModel.cs src/EyeCtrl.ViewModels/ManualSelectionOverlayViewModel.cs
git mv src/EyeCtrl.ViewModels/ZoomGazeIndicatorViewModel.cs src/EyeCtrl.ViewModels/ManualSelectionGazeIndicatorViewModel.cs
git mv src/EyeCtrl.ViewModels/Helpers/ZoomLayoutHelper.cs src/EyeCtrl.ViewModels/Helpers/ManualSelectionLayoutHelper.cs
git mv tests/EyeCtrl.App.Tests/Services/ZoomDwellServiceTests.cs tests/EyeCtrl.App.Tests/Services/ManualSelectionDwellServiceTests.cs
git mv tests/EyeCtrl.App.Tests/Tests/ZoomHandlerMemoryLeakTests.cs tests/EyeCtrl.App.Tests/Tests/ManualSelectionHandlerMemoryLeakTests.cs
git mv tests/EyeCtrl.ViewModels.Tests/ZoomOverlayViewModelTests.cs tests/EyeCtrl.ViewModels.Tests/ManualSelectionOverlayViewModelTests.cs
```

---

### Task 7: Fix string literals and user-facing text

- [ ] **Step 1: Update "Zoom" mode references in settings/labels**

Check `SettingsViewModel.cs` and `SettingsWindow.xaml.cs` for tab labels, setting descriptions, and user-facing strings that reference "Zoom" mode. Replace with "Manual Selection" where the mode is referenced (not magnification level).

- [ ] **Step 2: Update AutomationProperties and accessibility IDs**

Verify no `AutomationId` contains `Zoom` outside of the XAML already updated.

- [ ] **Step 3: Verify no "Zoom" identifiers remain**

Run: `rg "\bZoom\w+" --type cs src/ tests/`
Expected: Zero results (no Zoom-prefixed identifiers remain). Lowercase "zoom" in comments is fine.

---

### Task 8: Build and test

- [ ] **Step 1: Full solution build**

Run: `dotnet build EyeCtrl.slnx`
Expected: SUCCESS (0 errors, 0 warnings)

- [ ] **Step 2: Run unit tests (no display)**

```powershell
dotnet test tests/EyeCtrl.Core.Tests
dotnet test tests/EyeCtrl.Platform.Tests
dotnet test tests/EyeCtrl.ViewModels.Tests
```
Expected: All pass

- [ ] **Step 3: Run gitnexus_detect_changes**

Run and verify only expected symbols changed.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "refactor: rename Zoom Mode to Manual Selection Mode

Rename all Zoom-prefixed identifiers, classes, files, and messages
to ManualSelection across the entire codebase (~520 refs in 47 files).
Visual magnification terminology (MagnificationLevel, magnify) unchanged."
```
