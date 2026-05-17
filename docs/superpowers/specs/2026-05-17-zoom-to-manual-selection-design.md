# Rename Zoom Mode → Adjust Target Mode

**Date:** 2026-05-17
**Status:** Approved

## Purpose

Rename the "Zoom Mode" interaction concept to "Adjust Target Mode" throughout the codebase. The new name describes user intent (precisely adjusting cursor position before acting) rather than the visual implementation detail (magnification).

## Scope

~520 references across 44 C# files and 3 XAML files. No behavioral changes.

### Files to rename (filename contains "Zoom")

- `ZoomSettings.cs` → `AdjustTargetSettings.cs`
- `ZoomOverlayWindow.xaml/.cs` → `AdjustTargetOverlayWindow.xaml/.cs`
- `ZoomGazeIndicatorWindow.xaml/.cs` → `AdjustTargetGazeIndicatorWindow.xaml/.cs`
- `ZoomSideButtonWindow.xaml/.cs` → `AdjustTargetSideButtonWindow.xaml/.cs`
- `ZoomDwellService.cs` → `AdjustTargetDwellService.cs`
- `ZoomOverlayViewModel.cs` → `AdjustTargetOverlayViewModel.cs`
- `ZoomGazeIndicatorViewModel.cs` → `AdjustTargetGazeIndicatorViewModel.cs`
- `ZoomLayoutHelper.cs` → `AdjustTargetLayoutHelper.cs`
- `ZoomDwellServiceTests.cs` → `AdjustTargetDwellServiceTests.cs`
- `ZoomOverlayViewModelTests.cs` → `AdjustTargetOverlayViewModelTests.cs`
- `ZoomHandlerMemoryLeakTests.cs` → `AdjustTargetHandlerMemoryLeakTests.cs`

### Symbols to rename

| Category | Pattern |
|----------|---------|
| Classes | `Zoom*` → `AdjustTarget*` |
| Enum values | `Zoom`, `ZoomSelectionActive` → `AdjustTarget`, `AdjustTargetSelectionActive` |
| Messages | `StartZoomMessage`, `EndZoomMessage`, `ZoomHiddenMessage`, `UpdateZoomIndicator*`, `ResetZoomIndicator*` → `StartAdjustTargetMessage`, etc. |
| Properties | `ZoomEnabled`, `ZoomDwellPoint`, `ZoomActionMenuDwellMs`, `ZoomDwellService` → `AdjustTarget*` |
| Methods | `OnZoomStarted`, `OnZoomEnded`, `OnZoomDwell`, `IsWithinZoomBounds`, `BuildZoomPages` → `AdjustTarget*` |
| DI registration | `ZoomDwellService`, `ZoomLayoutHelper` → `AdjustTarget*` |

### What stays

- Visual magnification terminology (`MagnificationLevel`, `magnify`, `zoom level` in comments) — these describe the visual effect, not the mode
- User-facing label "Magnify a screen region" — unchanged

## Approach

1. Use `gitnexus_rename` for high-confidence class/enum/method renames
2. Grep for remaining `Zoom` references and replace in bulk
3. Rename files (git mv)
4. Update XAML bindings and resource references
5. Build and run full test suite
6. `gitnexus_detect_changes` to verify scope
