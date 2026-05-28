# Phase 3: Type Safety Implementation Plan

**Created:** 2026-04-04
**Status:** In Progress
**Phase:** 3 of 5 (Architecture Review Plan)

## Overview

Phase 3 focuses on implementing type safety improvements to prevent coordinate system bugs and add real-time property validation. This phase addresses Issues #5 and #7 from the Architecture Review Plan.

## Goals

1. **Issue #5: Coordinate Type Safety** - Prevent off-by-DPI bugs through strong typing
2. **Issue #7: Property Validation in Setters** - Provide real-time feedback for invalid settings

## Current State Analysis

### Coordinate System Issues

**Problem:** Three coordinate systems with no type-level safety:
- **Logical (DIPs):** WPF units, 1:1 at 96 DPI
- **Physical (Pixels):** Actual screen pixels, scaled by DPI
- **Normalized (0-65535):** Win32 mouse input range

**Current Implementation:**
- All coordinates use generic `ScreenPoint(double X, double Y)` record
- 30+ manual `DpiHelper` conversion calls across codebase
- Coordinate type tracked only in comments
- Easy to mix incompatible values during refactoring

**Key Files Affected:**
- `/src/EyeCtrl.Core/Models/ScreenPoint.cs` - Generic coordinate record
- `/src/EyeCtrl.Core/Services/DpiHelper.cs` - Manual conversions (30 usages)
- `/src/EyeCtrl.App/Overlays/*.xaml.cs` - Window positioning (mixed coordinates)
- `/src/EyeCtrl.App/Services/GazeEngineAdapter.cs` - Coordinate calculations
- `/src/EyeCtrl.Platform/Input/MouseCoordinateMapper.cs` - Normalization to 0-65535

### Property Validation Issues

**Problem:** Settings use `[ObservableProperty]` without setter validation:

**Current Implementation:**
```csharp
[ObservableProperty] private double _smoothingFactor = 0.5;
// User can set to -10 or 100 with no immediate feedback!
```

**Existing Validation:**
- ✅ `IValidatable` interface with `Validate()` method
- ✅ `ValidationHelper` with range/enum/color validation
- ✅ Validation runs at save time in `SettingsService`
- ❌ **No real-time validation in setters**
- ❌ UI can temporarily show invalid values
- ❌ User only learns of errors on save attempt

## Implementation Strategy

### Part A: Coordinate Type Safety (Issue #5)

#### A1: Design Coordinate Types (TDD: Tests First)

**Files to Create:**
- `/src/EyeCtrl.Core/Models/LogicalPoint.cs`
- `/src/EyeCtrl.Core/Models/PhysicalPoint.cs`
- `/src/EyeCtrl.Core/Models/NormalizedPoint.cs`
- `/tests/EyeCtrl.Core.Tests/Models/CoordinateTypeTests.cs`

**Test Cases to Write:**
```csharp
// CoordinateTypeTests.cs
- LogicalPoint_ToPhysical_ConvertsCorrectly
- PhysicalPoint_ToLogical_ConvertsCorrectly
- PhysicalPoint_ToNormalized_ConvertsCorrectly
- NormalizedPoint_ToPhysical_ConvertsCorrectly
- CoordinateConversion_RoundTrip_PreservesValue
- Equality_SameCoordinates_AreEqual
- Arithmetic_AddOffset_ReturnsCorrectValue
```

**Design:**
```csharp
// LogicalPoint.cs - WPF DIPs
public readonly record struct LogicalPoint(double X, double Y)
{
    public PhysicalPoint ToPhysical() =>
        new(DpiHelper.LogicalToPhysicalX(X), DpiHelper.LogicalToPhysicalY(Y));

    public LogicalPoint Add(double dx, double dy) => new(X + dx, Y + dy);
}

// PhysicalPoint.cs - Screen pixels
public readonly record struct PhysicalPoint(double X, double Y)
{
    public LogicalPoint ToLogical() =>
        new(DpiHelper.PhysicalToLogicalX(X), DpiHelper.PhysicalToLogicalY(Y));

    public NormalizedPoint ToNormalized(double screenWidth, double screenHeight) =>
        new(X / screenWidth * 65535, Y / screenHeight * 65535);

    public PhysicalPoint Add(double dx, double dy) => new(X + dx, Y + dy);
}

// NormalizedPoint.cs - Win32 0-65535 range
public readonly record struct NormalizedPoint(double X, double Y)
{
    public PhysicalPoint ToPhysical(double screenWidth, double screenHeight) =>
        new(X / 65535 * screenWidth, Y / 65535 * screenHeight);
}
```

#### A2: Migrate ScreenPoint to PhysicalPoint

**Strategy:** ScreenPoint represents physical coordinates throughout the codebase.

**Files to Update (55 total):**

**Priority 1 - Core Message Types:**
1. `/src/EyeCtrl.ViewModels/Messages/GazeMessages.cs` - `GazeCursorMovedMessage`
2. `/src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` - Action messages
3. `/src/EyeCtrl.Core/Models/DwellTarget.cs` - Physical center point

**Priority 2 - Processing Layer:**
4. `/src/EyeCtrl.Core/Processing/DwellDetector.cs` - Physical gaze input
5. `/src/EyeCtrl.Core/Processing/FixationDetector.cs` - Physical coordinates
6. `/src/EyeCtrl.Core/Processing/InteractionManager.cs` - State machine

**Priority 3 - ViewModels:**
7. All ViewModel properties storing screen positions (physical)

**Priority 4 - Windows:**
8. Window positioning code (receives physical, converts to logical)

**Approach:**
1. Create type alias: `using ScreenPoint = PhysicalPoint;` (temporary)
2. Gradually replace with explicit `PhysicalPoint` type
3. Remove alias once all references updated
4. Update method signatures to use `PhysicalPoint`

#### A3: Update DpiHelper for Coordinate Types

**File:** `/src/EyeCtrl.Core/Services/DpiHelper.cs`

**Changes:**
```csharp
// Add overloads that work with coordinate types
public static PhysicalPoint ToPhysical(LogicalPoint point) =>
    new(LogicalToPhysicalX(point.X), LogicalToPhysicalY(point.Y));

public static LogicalPoint ToLogical(PhysicalPoint point) =>
    new(PhysicalToLogicalX(point.X), PhysicalToLogicalY(point.Y));

// Keep existing methods for incremental migration
```

#### A4: Update Overlay Windows

**Files:**
- `/src/EyeCtrl.App/Overlays/ActionMenuWindow.xaml.cs`
- `/src/EyeCtrl.App/Overlays/ZoomOverlayWindow.xaml.cs`
- `/src/EyeCtrl.App/Overlays/ZoomGazeIndicatorWindow.xaml.cs`
- All other window classes

**Pattern:**
```csharp
// Before: Ambiguous coordinate type
public void UpdatePosition(ScreenPoint center, double radius)
{
    Left = DpiHelper.PhysicalToLogicalX(center.X) - radius / 2;
    // Is radius physical or logical? Not clear!
}

// After: Explicit coordinate types
public void UpdatePosition(PhysicalPoint center, LogicalPoint radiusInDips)
{
    var centerInDips = center.ToLogical();
    Left = centerInDips.X - radiusInDips.X / 2;
    // Crystal clear which coordinate system each value uses
}
```

#### A5: Update Platform Layer

**File:** `/src/EyeCtrl.Platform/Input/MouseCoordinateMapper.cs`

**Changes:**
```csharp
// Before
public (int normalizedX, int normalizedY) NormalizeScreenCoordinate(
    ScreenPoint point, double screenWidth, double screenHeight)

// After
public NormalizedPoint NormalizeScreenCoordinate(
    PhysicalPoint point, double screenWidth, double screenHeight)
{
    // ... validation ...
    return point.ToNormalized(screenWidth, screenHeight);
}
```

### Part B: Property Validation in Setters (Issue #7)

#### B1: Research MVVM Toolkit Partial Methods (TDD: Tests First)

**Test Cases to Write:**
```csharp
// SettingsPropertyValidationTests.cs
- OnChanging_InvalidValue_ThrowsArgumentOutOfRange
- OnChanging_ValidValue_AllowsChange
- OnChanging_BoundaryValue_AllowsChange
- OnChanged_AfterValidValue_PropertyChangedFired
```

**Files to Create:**
- `/tests/EyeCtrl.Core.Tests/Settings/SettingsPropertyValidationTests.cs`

#### B2: Design Validation Strategy

**Option 1: Partial Property Methods (Recommended)**
```csharp
[ObservableProperty] private double _smoothingFactor = 0.5;

partial void OnSmoothingFactorChanging(double value)
{
    if (value < 0.0 || value > 1.0)
        throw new ArgumentOutOfRangeException(
            nameof(SmoothingFactor),
            value,
            "SmoothingFactor must be between 0.0 and 1.0");
}
```

**Option 2: Clamping Instead of Throwing**
```csharp
partial void OnSmoothingFactorChanging(double oldValue, double newValue)
{
    // Silently clamp to valid range
    if (newValue < 0.0) _smoothingFactor = 0.0;
    else if (newValue > 1.0) _smoothingFactor = 1.0;
    else _smoothingFactor = newValue;
}
```

**Decision:** Use **Option 1** for consistency with existing `Validate()` approach.

#### B3: Implement Validation in Settings Classes

**Files to Update:**
- `/src/EyeCtrl.Core/Settings/DwellSettings.cs` (9 properties)
- `/src/EyeCtrl.Core/Settings/CursorSettings.cs` (8 properties)
- `/src/EyeCtrl.Core/Settings/ZoomSettings.cs`
- `/src/EyeCtrl.Core/Settings/ActionMenuSettings.cs`
- `/src/EyeCtrl.Core/Settings/ToolbarSettings.cs`
- `/src/EyeCtrl.Core/Settings/KeyboardSettings.cs`
- `/src/EyeCtrl.Core/Settings/ThemeSettings.cs`
- `/src/EyeCtrl.Core/Settings/GeneralSettings.cs`

**Pattern:**
```csharp
public partial class DwellSettings : ObservableObject, IValidatable
{
    [ObservableProperty] private int _fixationDetectionMs = 300;

    partial void OnFixationDetectionMsChanging(int value)
    {
        if (value < 100 || value > 1000)
            throw new ArgumentOutOfRangeException(
                nameof(FixationDetectionMs),
                value,
                "FixationDetectionMs must be between 100 and 1000");
    }

    // Keep existing Validate() for batch validation
    public IReadOnlyList<string> Validate() { ... }
}
```

## TDD Workflow

### Red-Green-Refactor Cycle

**1. RED - Write Failing Tests:**
```bash
# Create test file first
dotnet test --filter CoordinateTypeTests
# All tests fail - types don't exist yet
```

**2. GREEN - Implement Minimum Code:**
```bash
# Implement LogicalPoint, PhysicalPoint, NormalizedPoint
dotnet test --filter CoordinateTypeTests
# All tests pass
```

**3. REFACTOR - Clean Up:**
```bash
# Extract common patterns, improve API
dotnet test --filter CoordinateTypeTests
# All tests still pass after refactoring
```

### Test Organization

```
tests/EyeCtrl.Core.Tests/
├── Models/
│   └── CoordinateTypeTests.cs          # New - coordinate type conversions
├── Settings/
│   ├── SettingsPropertyValidationTests.cs  # New - setter validation
│   └── SettingsValidationTests.cs      # Existing - Validate() method tests
└── Services/
    └── DpiHelperCoordinateTests.cs     # New - DpiHelper with typed coords
```

## Migration Plan

### Iteration 1: Foundation (Day 1)
- ✅ Write coordinate type tests (RED)
- ✅ Implement `LogicalPoint`, `PhysicalPoint`, `NormalizedPoint` (GREEN)
- ✅ Run tests, verify all pass
- ✅ Write property validation tests (RED)

### Iteration 2: Core Types (Day 2)
- ✅ Add `PhysicalPoint` alias for `ScreenPoint`
- ✅ Update `DpiHelper` to support coordinate types (GREEN)
- ✅ Migrate message types to use `PhysicalPoint`
- ✅ Run tests, fix failures

### Iteration 3: Processing Layer (Day 2-3)
- ✅ Update `DwellDetector` to use `PhysicalPoint`
- ✅ Update `FixationDetector` to use `PhysicalPoint`
- ✅ Update `InteractionManager` state machine
- ✅ Run tests, verify no regressions

### Iteration 4: ViewModels (Day 3)
- ✅ Migrate ViewModel properties to typed coordinates
- ✅ Update coordinate calculations to use type methods
- ✅ Run tests, fix compilation errors

### Iteration 5: Windows (Day 4)
- ✅ Update overlay window positioning logic
- ✅ Use explicit `PhysicalPoint` → `LogicalPoint` conversions
- ✅ Remove manual `DpiHelper` calls in favor of type methods
- ✅ Test on multiple DPI settings (96, 120, 144)

### Iteration 6: Property Validation (Day 4-5)
- ✅ Implement `OnChanging` methods in `DwellSettings` (GREEN)
- ✅ Implement validation in remaining settings classes
- ✅ Update UI to catch and display validation exceptions
- ✅ Test real-time feedback in SettingsWindow

### Iteration 7: Cleanup (Day 5)
- ✅ Remove `ScreenPoint` type or mark obsolete
- ✅ Remove temporary type aliases
- ✅ Run full test suite
- ✅ Manual testing on high-DPI display
- ✅ Code review and documentation

## Success Metrics

### Coordinate Type Safety
- [ ] All coordinate variables have explicit types
- [ ] Zero manual `DpiHelper` conversion calls (all through type methods)
- [ ] Compiler errors when mixing incompatible coordinate types
- [ ] All tests pass on 96, 120, and 144 DPI
- [ ] Zero off-by-DPI positioning bugs in manual testing

### Property Validation
- [ ] All settings properties validate on change
- [ ] Invalid values rejected immediately with clear error
- [ ] SettingsWindow shows real-time validation feedback
- [ ] Existing `Validate()` tests still pass
- [ ] New property validation tests pass

### Code Quality
- [ ] Test coverage >80% for new coordinate types
- [ ] Test coverage 100% for property validation
- [ ] No performance degradation from coordinate types
- [ ] All existing tests pass (no regressions)

## Risk Mitigation

### High-Risk Changes

**1. Coordinate Type Migration**
- **Risk:** Touches 55+ files, potential for subtle bugs
- **Mitigation:**
  - Incremental migration using type alias
  - Comprehensive test suite before changes
  - Manual testing at each iteration
  - Pair programming for complex conversions

**2. Property Validation Breaking Changes**
- **Risk:** May throw exceptions in existing code paths
- **Mitigation:**
  - Add try-catch in SettingsWindow UI
  - Keep existing `Validate()` method for batch validation
  - Test all settings UI workflows
  - Document validation behavior

### Testing Strategy

**Unit Tests:**
- Coordinate type conversions (all permutations)
- Property validation (boundary values, invalid values)
- DpiHelper integration with coordinate types

**Integration Tests:**
- Message flow with typed coordinates
- Window positioning at various DPI settings
- Settings persistence with validation

**Manual Tests:**
- All overlays on 96 DPI display
- All overlays on 120 DPI display
- All overlays on 144 DPI display
- Settings UI with invalid input
- Settings save/load round-trip

## Obstacles & Solutions

### Potential Obstacles

**1. Circular Dependencies**
- `DpiHelper` in Core needs coordinate types, but types need DpiHelper
- **Solution:** Coordinate types use static DpiHelper methods, no circular dependency

**2. Performance of Record Structs**
- Creating many coordinate instances per frame
- **Solution:** Record structs are value types, no heap allocation

**3. Breaking Existing Code**
- Many files use `ScreenPoint` implicitly
- **Solution:** Type alias for gradual migration, compiler catches errors

**4. UI Exception Handling**
- Property setters throwing exceptions in data binding
- **Solution:** Add validation UI layer, show friendly error messages

### Lessons Learned (To Document)

- Best practices for coordinate type migration
- MVVM Toolkit partial method usage
- Test-first approach benefits
- DPI testing strategies

## Dependencies

### External Dependencies
- None - all internal refactoring

### Internal Dependencies
- Should complete before Issue #1 (App.xaml.cs refactoring)
- Independent of Issue #3 (ViewModel disposal)
- Independent of Issue #4 (Settings DI)

## Documentation Updates

### Files to Create/Update
- [ ] `COORDINATE_TYPES.md` - Guide to coordinate system types
- [ ] `SETTINGS_VALIDATION.md` - Property validation guide
- [ ] Update `ARCHITECTURE_REVIEW_PLAN.md` - Mark Phase 3 complete
- [ ] Update `ARCHITECTURE_ISSUES_SUMMARY.md` - Mark issues #5 and #7 complete

### Code Comments
- [ ] XML docs for all coordinate types
- [ ] XML docs for validation partial methods
- [ ] Update DpiHelper documentation

## Next Steps After Phase 3

1. **Phase 4:** Major refactoring (Issues #1, #10, #9)
2. **Phase 5:** Polish and minor improvements

---

**Document Version:** 1.0
**Last Updated:** 2026-04-04
**Status:** Ready for implementation
