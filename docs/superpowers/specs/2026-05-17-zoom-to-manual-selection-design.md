# Rename Zoom Mode → Manual Selection Mode

**Date:** 2026-05-17
**Status:** Approved

## Purpose

Rename the "Zoom Mode" interaction concept to "Manual Selection Mode" throughout the codebase. The new name describes user intent (manual cursor positioning and action selection) rather than the visual implementation detail (magnification).

## Scope

~520 references across 44 C# files and 3 XAML files. No behavioral changes.

### Files to rename (filename contains "Zoom")

- `ZoomSettings.cs` → `ManualSelectionSettings.cs`
- `ZoomOverlayWindow.xaml/.cs` → `ManualSelectionOverlayWindow.xaml/.cs`
- `ZoomGazeIndicatorWindow.xaml/.cs` → `ManualSelectionGazeIndicatorWindow.xaml/.cs`
- `ZoomSideButtonWindow.xaml/.cs` → `ManualSelectionSideButtonWindow.xaml/.cs`
- `ZoomDwellService.cs` → `ManualSelectionDwellService.cs`
- `ZoomOverlayViewModel.cs` → `ManualSelectionOverlayViewModel.cs`
- `ZoomGazeIndicatorViewModel.cs` → `ManualSelectionGazeIndicatorViewModel.cs`
- `ZoomLayoutHelper.cs` → `ManualSelectionLayoutHelper.cs`
- `ZoomDwellServiceTests.cs` → `ManualSelectionDwellServiceTests.cs`
- `ZoomOverlayViewModelTests.cs` → `ManualSelectionOverlayViewModelTests.cs`
- `ZoomHandlerMemoryLeakTests.cs` → `ManualSelectionHandlerMemoryLeakTests.cs`

### Symbols to rename

| Category | Pattern |
|----------|---------|
| Classes | `Zoom*` → `ManualSelection*` |
| Enum values | `Zoom`, `ZoomSelectionActive` → `ManualSelection`, `ManualSelectionActive` |
| Messages | `StartZoomMessage`, `EndZoomMessage`, `ZoomHiddenMessage`, `UpdateZoomIndicator*`, `ResetZoomIndicator*` → `StartManualSelectionMessage`, etc. |
| Properties | `ZoomEnabled`, `ZoomDwellPoint`, `ZoomActionMenuDwellMs`, `ZoomDwellService` → `ManualSelection*` |
| Methods | `OnZoomStarted`, `OnZoomEnded`, `OnZoomDwell`, `IsWithinZoomBounds`, `BuildZoomPages` → `ManualSelection*` |
| DI registration | `ZoomDwellService`, `ZoomLayoutHelper` → `ManualSelection*` |

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
