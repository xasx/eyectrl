# Shared Dwell Control Library & SettingsWindow Refactor

**Date:** 2026-04-09
**Status:** Approved

## Problem

The SettingsWindow (1192 lines) contains repetitive control builder methods (`AddSlider`, `AddToggle`, `AddColorPicker`, `AddHorizontalList`, `AddResetButton`, `MakeControlButton`) and visual update methods that are duplicated concepts across multiple overlays. Every overlay independently reimplements button creation, dwell registration, and visual feedback. There is no shared library of dwell-enabled UI controls.

## Goals

1. Extract reusable dwell-enabled controls into a shared library
2. Reduce SettingsWindow from 1192 to ~826 lines by eliminating builder and visual update methods
3. Enable reuse across SettingsWindow, KeyboardWindow (prediction bar + emoji category tabs), ToolbarWindow, ActionMenuWindow, and future overlays
4. Consolidate the SettingsWindowViewModel's fragmented dwell registration API
5. Maintain a clear upgrade path to full WPF UserControls with XAML support

## Non-Goals

- XAML-based page definitions (deferred; procedural page builders retained)
- Refactoring the keyboard's main key grid (keeps its custom nearest-key algorithm)
- Auto-discovery container panel (`DwellControlHost` from original plan)

## Architecture

### Project Structure

Shared dwell registration abstractions live in `EyeCtrl.Core` so overlays and view models can depend on them without introducing an `App` ↔ `ViewModels` cycle. Reusable dwell-enabled controls live in the existing `EyeCtrl.App.Controls` namespace:

```
src/EyeCtrl.Core/
  Processing/
    IDwellRegistrar.cs        (~25 lines; includes DwellCategory)

src/EyeCtrl.App/
  Controls/
    DwellControl.cs           (~60 lines, abstract base)
    IDwellComposite.cs        (~10 lines)
    DwellButton.cs            (~35 lines)
    DwellToggle.cs            (~40 lines)
    DwellSlider.cs            (~50 lines, composes DwellButtons)
    DwellComboBox.cs          (~60 lines, composes DwellButtons)
    DwellColorPicker.cs       (~50 lines, composes DwellButtons)
    DwellLabel.cs             (~10 lines, no dwell)
```

### IDwellRegistrar Interface

Single interface for dwell target registration. Replaces the 12 separate methods on SettingsWindowViewModel.

```csharp
public enum DwellCategory { Tab, Control, Pagination, Modal }

public interface IDwellRegistrar
{
    void Register(string id, DwellCategory category, double radiusPhysical, int dwellMs, Action onActivated);
    void Unregister(string id, DwellCategory category);
    void UpdatePosition(string id, DwellCategory category, PhysicalPoint center);
    double GetProgress(string id, DwellCategory category);
}
```

The SettingsWindowViewModel implements this interface. Internally it routes to the correct `DwellDetector` based on `DwellCategory`. Other overlays that use dwell detection (KeyboardWindow, ToolbarWindow, ActionMenuWindow) each implement `IDwellRegistrar` independently, wrapping their own `DwellDetector` instance(s). The interface is the shared contract; the detector topology is per-overlay. Four independent DwellDetector instances are preserved because each detector selects the single nearest target for dwell accumulation while resetting all others. If all categories shared one detector, a tab button near a control button would compete for nearest-target selection, clearing controls on tab switch would require filtering instead of a simple ClearAll, and modal buttons would compete with controls underneath instead of taking exclusive focus.

### DwellControl Base Class

Abstract base class handling the dwell lifecycle: registration, position updates, and visual feedback.

```csharp
public abstract class DwellControl
{
    public string DwellId { get; }
    public Border Root { get; }
    protected IDwellRegistrar Registrar { get; }
    protected DwellCategory Category { get; }
    protected double RadiusPhysical { get; }
    protected int DwellMs { get; }

    protected DwellControl(IDwellRegistrar registrar, string dwellId,
        DwellCategory category, double radiusPhysical, int dwellMs);

    // Adds Root to panel, registers dwell target
    public void Attach(Panel parent);

    // Removes Root from panel, unregisters dwell target
    public void Detach(Panel parent);

    // Reads screen position from Root, updates registrar
    public void UpdatePosition();

    // Reads progress from registrar, calls ApplyProgress
    public void UpdateVisual();

    // Subclass: what happens when dwell completes
    protected abstract void OnActivated();

    // Subclass: how to show dwell progress (color blend, arc, etc.)
    protected abstract void ApplyProgress(double progress);
}
```

**Lifecycle:** Explicit `Attach`/`Detach` rather than WPF Loaded/Unloaded events, because the page-rebuild pattern creates and tears down controls on tab/page switches and explicit lifecycle is easier to reason about.

**Per-frame updates:** `UpdatePosition` and `UpdateVisual` are called by the overlay's existing update loop, not by internal timers. The overlay already iterates controls for this purpose.

### IDwellComposite Interface

For controls that own multiple dwell targets (sliders, combo boxes, color pickers):

```csharp
public interface IDwellComposite
{
    void Attach(Panel parent);
    void Detach(Panel parent);
    IEnumerable<DwellControl> Children { get; }
}
```

The overlay's update loop handles both types:

```csharp
foreach (var item in _activeItems)
{
    if (item is DwellControl single) { single.UpdatePosition(); single.UpdateVisual(); }
    if (item is IDwellComposite comp)
        foreach (var child in comp.Children) { child.UpdatePosition(); child.UpdateVisual(); }
}
```

### Individual Controls

#### DwellButton (~35 lines)
- **Params:** registrar, id, label, action, baseColor
- **Visual:** Single Border with TextBlock
- **Dwell:** One target
- **ApplyProgress:** Color blend from baseColor toward highlight
- **Usage:** Standalone (reset, toolbar, modal) and composed inside other controls

#### DwellToggle (~40 lines)
- **Params:** registrar, id, label, getter, setter
- **Visual:** Label + toggle box with check indicator (unicode checkbox)
- **Dwell:** One target
- **OnActivated:** Flip bool via setter, update indicator
- **ApplyProgress:** Color blend on toggle border

#### DwellSlider (~50 lines, implements IDwellComposite)
- **Params:** registrar, idPrefix, label, min, max, step, decimals, getter, setter
- **Visual:** Label + [MinusButton | ValueDisplay | PlusButton]
- **Composition:** Owns two DwellButton children ("{id}_dn", "{id}_up")
- **OnActivated (per child):** Decrement or increment, call setter, update display
- **Not itself a DwellControl** — manages lifecycle of its DwellButton children

#### DwellComboBox (~60 lines, implements IDwellComposite)
- **Params:** registrar, idPrefix, label, displayOptions, codeOptions, getter, setter
- **Visual:** Label + WrapPanel of option borders with radio indicators
- **Composition:** One DwellButton per option ("{idPrefix}_{index}")
- **OnActivated (per child):** Select that option, call setter, update all indicators

#### DwellColorPicker (~50 lines, implements IDwellComposite)
- **Params:** registrar, idPrefix, label, getter, setter, presets (default: 8 standard colors)
- **Visual:** Label + WrapPanel of color swatches with selection border
- **Composition:** One DwellButton per swatch ("{idPrefix}_{colorName}")
- **OnActivated (per child):** Select color, call setter, update selection indicators

#### DwellLabel (~10 lines, not a DwellControl)
- **Params:** text
- **Visual:** Gray TextBlock
- **Usage:** Section headers between controls, no dwell behavior

## SettingsWindow Changes

### What Gets Deleted (~386 lines)

| Method | Lines | Replacement |
|--------|-------|-------------|
| `AddSlider()` | ~35 | `DwellSlider` |
| `AddHorizontalList()` x2 | ~85 | `DwellComboBox` |
| `AddToggle()` | ~49 | `DwellToggle` |
| `AddColorPicker()` | ~50 | `DwellColorPicker` |
| `AddResetButton()` | ~28 | `DwellButton` |
| `AddLabel()` | ~9 | `DwellLabel` |
| `MakeControlButton()` | ~26 | `DwellButton` |
| `UpdateControlVisuals()` | ~10 | `DwellControl.ApplyProgress` |
| `UpdatePaginationVisuals()` | ~10 | `DwellControl.ApplyProgress` |
| `UpdateModalVisuals()` | ~11 | `DwellControl.ApplyProgress` |
| `UpdateTabVisuals()` | ~17 | `DwellControl.ApplyProgress` |
| `UpdateButtonPositions()` | ~18 | `DwellControl.UpdatePosition` |
| `UpdateAllTargetPositions()` | ~29 | Simplified loop |
| `Blend()` | ~9 | Moved to base class |

### What Gets Added (~20 lines)

```csharp
private IDwellRegistrar _registrar;
private List<object> _activeItems = new();  // each item is DwellControl or IDwellComposite; update loop pattern-matches on type

private void Add(object item);       // Attach to ContentPanel, track
private void ClearContent();         // Detach all, clear list
private void UpdateAllPositionsAndVisuals();  // Single loop
```

### Page Builder Pattern (After)

```csharp
private List<List<Action>> BuildCursorPages()
{
    return [
        [
            () => Add(new DwellToggle(_registrar, "highContrast", "High Contrast",
                () => _settings.Cursor.CircleHighContrast,
                v => { _settings.Cursor.CircleHighContrast = v; ApplyAndSave(); })),
            () => Add(new DwellSlider(_registrar, "circleSize", "Circle Size",
                10, 500, 10, 0,
                () => _settings.Cursor.CircleSize,
                v => { _settings.Cursor.CircleSize = v; ApplyAndSave(); })),
            // ...
        ]
    ];
}
```

### Net Result

- **SettingsWindow:** 1192 -> ~826 lines (31% reduction)
- **New shared code:** ~340 lines across 10 files
- **Reusable** by KeyboardWindow, ToolbarWindow, ActionMenuWindow, future overlays

## ViewModel Changes

`SettingsWindowViewModel` implements `IDwellRegistrar`. The 12 existing public methods for target management consolidate into 4:

**Removed:**
- `AddTabTarget`, `UpdateTabButtonPosition`
- `AddControlTarget`, `UpdateControlPosition`, `RegisterControlAction`
- `AddPaginationTarget`, `UpdatePaginationButtonPosition`
- `AddModalTarget`, `UpdateModalButtonPosition`
- `ClearControlTargets`
- `SimulateControlActivated`, `SimulateModalButtonActivated`

**Added:**
- `IDwellRegistrar.Register(id, category, radiusPhysical, dwellMs, onActivated)`
- `IDwellRegistrar.Unregister(id, category)`
- `IDwellRegistrar.UpdatePosition(id, category, center)`
- `IDwellRegistrar.GetProgress(id, category)`
- `ClearCategory(DwellCategory category)` (replaces `ClearControlTargets`)
- `SimulateActivated(string id, DwellCategory category)` (test helper, replaces two methods)

Internal routing:
```csharp
private DwellDetector GetDetector(DwellCategory category) => category switch
{
    DwellCategory.Tab => _tabDwell,
    DwellCategory.Control => _controlDwell,
    DwellCategory.Pagination => _pagDwell,
    DwellCategory.Modal => _modalDwell,
};
```

Existing tests updated to use new API. Assertions unchanged since behavior is identical.

## Reuse in Other Overlays

### KeyboardWindow — Prediction Bar & Emoji Tabs

Current prediction buttons and emoji category tabs use `DwellDetector` with manual button creation. Replace with:

```csharp
// Prediction bar
var predButton = new DwellButton(registrar, $"pred_{i}", prediction.Text,
    () => OnPredictionSelected(prediction), predictionColor);

// Emoji category tab
var catTab = new DwellButton(registrar, $"cat_{name}", categoryEmoji,
    () => ShowEmojiCategory(name), tabColor);
```

The keyboard's main key grid is **not affected** — it keeps its custom nearest-key algorithm.

### ToolbarWindow

Current `AddButton()` method builds buttons manually. Replace with `DwellButton`.

### ActionMenuWindow

Current radial button creation can use `DwellButton` with custom positioning (the control doesn't dictate layout — the overlay positions the `Root` border).

## Upgrade Path to UserControls

The design explicitly supports a future migration to full WPF UserControls:

1. **Same public API** — constructor params become dependency properties
2. **Same lifecycle** — `Attach`/`Detach` map to WPF `Loaded`/`Unloaded` events
3. **Same composition** — `IDwellComposite` children become XAML child elements
4. **`IDwellRegistrar` unchanged** — stable contract regardless of control implementation
5. **Page builders remain valid** — they construct controls the same way
6. **XAML pages become additive** — an additional option alongside procedural builders

The migration is mechanical: add dependency properties, extract XAML template, wire Loaded/Unloaded. No redesign required.

## Risks & Mitigations

**Risk 1: Breaking existing functionality during migration**
Mitigation: Migrate one tab at a time (start with Zoom — smallest, 6 controls on 1 page). Keep old code paths until each tab is verified.

**Risk 2: DwellButton composition adds indirection**
Mitigation: DwellSlider/DwellComboBox/DwellColorPicker own their children directly. No deep nesting. The composite pattern is one level deep.

**Risk 3: Position update correctness**
Mitigation: `DwellControl.UpdatePosition()` uses the same `PointToScreen` + `DpiHelper` pattern as the current code. Extract, don't rewrite.

**Risk 4: ViewModel test suite breakage**
Mitigation: Tests verify dwell behavior (activation, progress, cooldown), not API shape. Updating method calls is mechanical; assertions stay the same.

## Line Count Summary

| Component | Lines |
|-----------|-------|
| IDwellRegistrar + DwellCategory | ~25 |
| DwellControl base | ~60 |
| IDwellComposite | ~10 |
| DwellButton | ~35 |
| DwellToggle | ~40 |
| DwellSlider | ~50 |
| DwellComboBox | ~60 |
| DwellColorPicker | ~50 |
| DwellLabel | ~10 |
| **Total new shared code** | **~340** |
| **SettingsWindow after** | **~826** |
| **SettingsWindow reduction** | **~366 lines (31%)** |
