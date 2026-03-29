# EyeCtrl Architecture Review & Improvement Plan

**Review Date:** 2026-03-29
**Status:** Pending Implementation
**Priority:** High - Architectural Technical Debt

## Executive Summary

This document presents a comprehensive architectural review of the EyeCtrl application, identifying 16 key architectural issues ranging from critical to minor. The review was conducted through deep analysis of the codebase structure, design patterns, dependencies, and code quality.

**Overall Assessment:**
- **Architecture Style:** 3-layer MVVM with message-based communication
- **Cohesion:** Medium (good layer separation, but internal layers need refactoring)
- **Coupling:** Medium-High (tight coupling between App layer and ViewModels)
- **Testability:** Medium (excellent Core layer tests, weak UI/integration coverage)
- **Technical Debt:** Medium-High

---

## Priority Classification

| Priority | Count | Description |
|----------|-------|-------------|
| 🔴 **CRITICAL** | 6 | Architectural flaws causing maintainability/reliability issues |
| 🟡 **SIGNIFICANT** | 4 | Important issues affecting code quality and maintenance |
| 🟢 **MINOR** | 6 | Code quality improvements and consistency issues |

---

## Critical Issues (Priority 1)

### Issue #1: App.xaml.cs God Object Antipattern
**Severity:** 🔴 CRITICAL
**Location:** `src/EyeCtrl.App/App.xaml.cs` (771 lines)
**Category:** Single Responsibility Principle Violation

#### Problem Description
The `App.xaml.cs` class violates the Single Responsibility Principle by handling:
- Dependency injection configuration (109 lines)
- Settings management and persistence
- Logging infrastructure setup
- All 8 ViewModel instantiation and event subscription
- 8 overlay window lifecycle management
- Complex gaze processing event chains (100+ lines of nested handlers)
- Tray icon and system integration
- Application lifecycle events

**Code Smell Example:**
```csharp
// Lines 275-434: Complex zoom fixation logic with 4 nested lambdas
EventHandler<ScreenPoint> zoomGazeHandler = null;
PropertyChangedEventHandler zoomVisibilityHandler = null;

zoomGazeHandler = (_, gazePoint) => {
    zoomVisibilityHandler = (_, e2) => {
        if (e2.PropertyName == nameof(ZoomOverlayViewModel.IsVisible) && !zoomVM.IsVisible)
            gazeEngine.CursorMoved -= zoomGazeHandler;
    };
    zoomVM.PropertyChanged += zoomVisibilityHandler;
};
// Manual event cleanup is fragile and error-prone
```

#### Impact
- **Maintainability:** Extremely difficult to understand full dependency graph
- **Testability:** Virtually impossible to unit test app initialization
- **Coupling:** All components tightly coupled through this central class
- **Risk:** Changes to one overlay can break unrelated functionality
- **Onboarding:** New developers overwhelmed by 771-line file

#### Recommended Solution
**Phase 1: Extract Service Configuration**
1. Create `AppBootstrapper.cs` to handle DI container setup
2. Create `LoggingInitializer.cs` for Serilog configuration
3. Move service registration to separate methods by concern

**Phase 2: Extract Overlay Management**
1. Create `IOverlayManager` interface with responsibilities:
   - Window lifecycle (create, show, hide, dispose)
   - Event subscription coordination
   - Inter-overlay communication
2. Implement `OverlayManager` class to manage all 8 overlays
3. Move complex event handler logic to dedicated classes

**Phase 3: Introduce Event Aggregator Pattern**
1. Replace manual event subscription/unsubscription with centralized mediator
2. Use dependency injection for handler registration
3. Implement automatic cleanup on component disposal

#### Success Metrics
- App.xaml.cs reduced to < 200 lines
- Each class has single, clear responsibility
- Unit tests can verify bootstrapping logic
- Event handler lifecycle managed automatically

#### Effort Estimate
- **Time:** 3-4 days
- **Risk:** Medium (requires careful refactoring and testing)
- **Benefits:** High (dramatically improves maintainability)

---

### Issue #2: Dual Event + Message Communication Pattern
**Severity:** 🔴 CRITICAL
**Location:** Multiple files (Core layer, App layer, ViewModels layer)
**Category:** Architectural Inconsistency

#### Problem Description
The codebase uses **both** raw .NET events **and** MVVM Toolkit messages for the same data flows, creating cognitive overhead and code duplication.

**Dual Pattern Example:**
```csharp
// GazeEngine.cs - Fires raw events
public event EventHandler<ScreenPoint>? CursorMoved;

// GazeEngineAdapter.cs - Translates to messages
_gazeEngine.CursorMoved += (_, point) =>
    _messenger.Send(new GazeCursorMovedMessage(point));

// ViewModels - Subscribe to messages
messenger.Register<GazeCursorMovedMessage>(this, (r, m) => { /* ... */ });

// App.xaml.cs - ALSO subscribes to raw events in some places
_gazeEngine.CursorMoved += (_, point) => { /* ... */ };
```

**Classes with Dual Patterns:**
- `GazeEngine` (events) → `GazeEngineAdapter` (translates) → ViewModels (messages)
- `InteractionManager` (events) → `GazeEngineAdapter` (translates) → ViewModels (messages)
- Some ViewModels still subscribed to raw events in App.xaml.cs

#### Impact
- **Cognitive Load:** Developers must understand two communication mechanisms
- **Code Duplication:** Event data converted to messages after already flowing
- **Maintenance:** Changes require updates in multiple places
- **Debugging:** Message flow harder to trace through translation layer
- **Inconsistency:** Some flows use events, some messages, no clear pattern

#### Recommended Solution
**Option A: Full Migration to Messages (Preferred)**
1. Modify Core layer classes (`GazeEngine`, `InteractionManager`) to send messages directly
2. Remove `GazeEngineAdapter` translation layer
3. Update Core layer to depend on `IMessenger` interface
4. All communication flows through single message bus

**Option B: Full Migration to Events**
1. Remove message-based architecture
2. Use raw events throughout
3. Simpler but loses benefits of weak references and decoupling

**Recommendation:** Choose Option A
- Aligns with existing MVVM Community Toolkit usage
- Provides better decoupling and testability
- WeakReferenceMessenger prevents memory leaks
- Consistent with modern MVVM practices

#### Implementation Steps
1. Add `IMessenger` dependency to `GazeEngine` and `InteractionManager`
2. Replace `EventHandler<T>` declarations with `messenger.Send<T>()` calls
3. Remove `GazeEngineAdapter` class
4. Update unit tests to verify message sending
5. Remove event subscriptions from App.xaml.cs
6. Verify all ViewModels receive messages correctly

#### Success Metrics
- Zero event handlers remaining for gaze/interaction data
- GazeEngineAdapter class deleted
- All communication through `IMessenger`
- Test coverage for message sending in Core layer

#### Effort Estimate
- **Time:** 2-3 days
- **Risk:** Medium (requires careful coordination)
- **Benefits:** High (eliminates architectural inconsistency)

---

### Issue #3: ViewModel Lifecycle and Memory Leak Risk
**Severity:** 🔴 CRITICAL
**Location:** `src/EyeCtrl.ViewModels/` and `src/EyeCtrl.App/App.xaml.cs`
**Category:** Resource Management

#### Problem Description
ViewModels have unclear lifecycle with multiple subscription patterns that risk memory leaks:

1. **ViewModels register message handlers in constructors** (permanent subscriptions)
2. **App.xaml.cs subscribes to ViewModel PropertyChanged events** (never unsubscribed)
3. **Manual event handler tracking** (fragile cleanup)
4. **SettingsWindow kept in memory** after closing

**Leak Pattern Example:**
```csharp
// ActivationButtonViewModel constructor - permanent message registration
messenger.RegisterAll(this);

// App.xaml.cs lines 248-271 - never unsubscribed
activationVM.PropertyChanged += (_, e) => { /* ... */ };

// Lines 421-426 - manual tracking of handlers
zoomVisibilityHandler = (_, e2) => {
    if (e2.PropertyName == nameof(ZoomOverlayViewModel.IsVisible) && !zoomVM.IsVisible)
        gazeEngine.CursorMoved -= zoomGazeHandler;
};
zoomVM.PropertyChanged += zoomVisibilityHandler;
// No corresponding -= when zoom ends!
```

#### Impact
- **Memory Leaks:** PropertyChanged handlers accumulate over time
- **Resource Exhaustion:** Long-running sessions consume increasing memory
- **GC Pressure:** Old objects kept alive unnecessarily
- **Instability:** May lead to crashes after extended use

#### Recommended Solution
**Phase 1: Implement Proper Disposal**
1. Make all ViewModels implement `IDisposable`
2. Unregister message handlers in `Dispose()`
3. Unsubscribe from all event handlers
4. Call `Dispose()` in `App.OnExit()`

**Phase 2: Use Weak Event Pattern**
1. Replace `PropertyChanged +=` with `WeakEventManager.AddHandler()`
2. Or use `IMessenger` for property change notifications
3. Eliminates need for explicit unsubscription

**Phase 3: Fix SettingsWindow Lifecycle**
1. Dispose window when closed (lines 538-571)
2. Create new instance on each open (don't cache)
3. Or implement proper disposal before creating new instance

#### Code Example
```csharp
// ViewModelBase.cs
public abstract class ViewModelBase : ObservableObject, IDisposable
{
    protected readonly IMessenger _messenger;
    private bool _disposed;

    protected ViewModelBase(IMessenger messenger)
    {
        _messenger = messenger;
        _messenger.RegisterAll(this);
    }

    public void Dispose()
    {
        if (_disposed) return;

        _messenger.UnregisterAll(this);
        OnDispose();
        _disposed = true;
    }

    protected virtual void OnDispose() { }
}

// App.xaml.cs OnExit
protected override void OnExit(ExitEventArgs e)
{
    _cursorCircleVM?.Dispose();
    _activationVM?.Dispose();
    // ... dispose all ViewModels

    base.OnExit(e);
}
```

#### Success Metrics
- All ViewModels implement `IDisposable`
- Memory profiler shows no leaked ViewModels after 1 hour
- Event handlers properly cleaned up
- No manual event tracking needed

#### Effort Estimate
- **Time:** 2 days
- **Risk:** Low (additive changes, doesn't break existing code)
- **Benefits:** High (prevents memory leaks)

---

### Issue #4: Settings Object Fragmentation in DI Container
**Severity:** 🔴 CRITICAL
**Location:** `src/EyeCtrl.App/App.xaml.cs` lines 118-127
**Category:** Dependency Injection Misuse

#### Problem Description
The settings hierarchy is fragmented by registering each sub-settings object separately:

```csharp
services.AddSingleton(settings);           // Root settings
services.AddSingleton(settings.Cursor);    // Sub-setting
services.AddSingleton(settings.Dwell);     // Sub-setting
services.AddSingleton(settings.Zoom);      // Sub-setting
// ... 5 more sub-settings
```

This creates multiple reference paths to the same objects and tight coupling.

#### Impact
- **Multiple Reference Paths:** Components can access settings through different routes
- **Structural Coupling:** Changes to `EyeCtrlSettings` structure break DI configuration
- **Inconsistency:** Some components take root, others take sub-settings
- **Circular Dependency Risk:** Settings modify themselves via PropertyChanged events
- **Testing Complexity:** Must mock multiple settings objects

#### Recommended Solution
**Option A: Register Only Root Settings (Recommended)**
```csharp
services.AddSingleton<EyeCtrlSettings>(settings);
// Components request root and access properties: settings.Cursor, settings.Zoom, etc.
```

**Option B: Use Settings Interfaces**
```csharp
services.AddSingleton<ICursorSettings>(sp => sp.GetRequiredService<EyeCtrlSettings>().Cursor);
services.AddSingleton<IZoomSettings>(sp => sp.GetRequiredService<EyeCtrlSettings>().Zoom);
// Components depend on interfaces, provides better abstraction
```

**Recommendation:** Use Option A for simplicity
- Single registration point
- Clear ownership
- Components take only what they need from root
- No additional abstractions needed

#### Implementation Steps
1. Remove individual sub-settings registrations
2. Update component constructors to take `EyeCtrlSettings` root
3. Access sub-settings as properties: `_settings.Cursor.SmoothingFactor`
4. Update unit tests to use single settings instance
5. Verify no compilation errors

#### Success Metrics
- Single settings registration in DI container
- All components accessing settings through root
- No structural coupling to settings hierarchy
- Simplified test setup

#### Effort Estimate
- **Time:** 1 day
- **Risk:** Low (straightforward refactoring)
- **Benefits:** Medium (reduces coupling, simplifies DI)

---

### Issue #5: Coordinate System Type Safety
**Severity:** 🔴 CRITICAL
**Location:** Multiple files using `DpiHelper` conversions
**Category:** Type Safety / Correctness

#### Problem Description
Coordinates flow through 3 different systems without type-level safety:
- **Logical coordinates (DIPs):** WPF window positions, element sizes
- **Physical coordinates (pixels):** Gaze tracker data, screen capture
- **Normalized coordinates (0-65535):** Win32 mouse input

All represented as `(double X, double Y)` or plain `ScreenPoint`, making it easy to mix incompatible values.

**Examples of Confusion:**
```csharp
// ActionMenuWindow.xaml.cs:62-63
var physicalRingRadius = DpiHelper.LogicalToPhysicalX(EffectiveRingRadius);
// But EffectiveRingRadius comes from CircleSize setting - is it really logical?

// ZoomOverlayWindow.xaml.cs:41
Left = DpiHelper.PhysicalToLogicalX(center.X) - displayDiameter / 2.0;
// center.X is physical, Left is logical, displayDiameter is... ?

// App.xaml.cs:143-145
SystemParameters.PrimaryScreenWidth * DpiHelper.ScaleX;
// Multiplying logical width by scale factor - but result used as physical?
```

#### Impact
- **Off-by-Scale Errors:** Multi-monitor or high-DPI setups show incorrect positions
- **Zoom Magnification Issues:** Wrong coordinate type causes incorrect zoom scale
- **Action Menu Positioning:** Unreliable at non-96 DPI
- **Debugging Difficulty:** Hard to trace where conversion errors occur
- **Maintenance Risk:** Developers can't tell which coordinate system a value uses

#### Recommended Solution
**Introduce Strong Typing for Coordinates**

```csharp
// Core/Models/Coordinates.cs
public readonly record struct LogicalPoint(double X, double Y)
{
    public PhysicalPoint ToPhysical() =>
        new(DpiHelper.LogicalToPhysicalX(X), DpiHelper.LogicalToPhysicalY(Y));
}

public readonly record struct PhysicalPoint(double X, double Y)
{
    public LogicalPoint ToLogical() =>
        new(DpiHelper.PhysicalToLogicalX(X), DpiHelper.PhysicalToLogicalY(Y));

    public NormalizedPoint ToNormalized(double screenWidth, double screenHeight) =>
        new(X / screenWidth * 65535, Y / screenHeight * 65535);
}

public readonly record struct NormalizedPoint(double X, double Y)
{
    public PhysicalPoint ToPhysical(double screenWidth, double screenHeight) =>
        new(X / 65535 * screenWidth, Y / 65535 * screenHeight);
}
```

**Migration Strategy:**
1. Create new coordinate types
2. Update `ScreenPoint` to be strongly typed or replace with `PhysicalPoint`
3. Update method signatures to specify coordinate type
4. Replace manual `DpiHelper` calls with `.ToLogical()` / `.ToPhysical()`
5. Compiler enforces correct conversions

#### Success Metrics
- All coordinate variables have explicit types
- Compilation errors when mixing incompatible types
- DpiHelper usage reduced by 80%
- Zero off-by-DPI bugs in testing

#### Effort Estimate
- **Time:** 3-4 days
- **Risk:** Medium-High (touches many files)
- **Benefits:** Very High (prevents entire class of bugs)

---

### Issue #6: InteractionManager Missing State Transitions
**Severity:** 🔴 CRITICAL
**Location:** `src/EyeCtrl.Core/Processing/InteractionManager.cs`
**Category:** State Machine Correctness

#### Problem Description
The `InteractionState` enum declares a state that is **never set**:

```csharp
public enum InteractionState
{
    FreeLook, Fixation, ActionMenuVisible, ZoomSelectionActive,
    ZoomSelectionFixation,  // DECLARED BUT NEVER USED!
    ZoomSelectionActionMenuVisible, Paused
}
```

Additionally, state transitions don't match UI behavior:
- No way to go directly from `Fixation` → `ZoomSelectionFixation`
- Only path: Fixation → ZoomSelectionActive → ZoomSelectionActionMenuVisible
- Back button handling assumes FixationPoint still valid (may be stale)

#### Impact
- **Dead Code:** Enum value never used, misleading for developers
- **Missing Functionality:** Can't distinguish "fixating in zoom" from "menu visible"
- **State Inconsistency:** Actual behavior doesn't match state machine design
- **Bug Risk:** Fixation events during zoom not properly handled

#### Test Case Revealing Issue
```csharp
// What happens if user dwells in zoom but never selects action?
// FixationEnded event fires → InteractionManager.OnFixationEnded()
// But current code only handles Fixation state, not ZoomSelectionFixation!
// Result: Fixation end during zoom is silently ignored
```

#### Recommended Solution
**Option A: Implement Missing State (If Needed)**
1. Add transition: `ZoomSelectionActive` → `ZoomSelectionFixation` on fixation in zoom
2. Handle `FixationEnded` in `ZoomSelectionFixation` state
3. Update state diagram documentation

**Option B: Remove Unused State (Simpler)**
1. Delete `ZoomSelectionFixation` from enum
2. Document that fixations during zoom don't trigger menu
3. Simplify state machine

**Recommendation:** Choose Option B unless there's a requirement for fixation detection during zoom selection.

#### Implementation Steps
1. Analyze requirements: Should fixations be detected during zoom?
2. If no: Remove `ZoomSelectionFixation` from enum
3. If yes: Implement state transitions and handlers
4. Update state machine diagram
5. Add comprehensive state transition tests

#### Success Metrics
- All enum values are used in code
- State machine matches actual behavior
- State transition tests cover all paths
- Documentation reflects implementation

#### Effort Estimate
- **Time:** 1-2 days
- **Risk:** Low (clarification + cleanup)
- **Benefits:** Medium (eliminates confusion)

---

## Significant Issues (Priority 2)

### Issue #7: No Property Validation in Observable Setters
**Severity:** 🟡 SIGNIFICANT
**Location:** All settings classes
**Category:** Data Validation

#### Problem Description
Settings properties use `[ObservableProperty]` without validation in setters:

```csharp
[ObservableProperty] private double _smoothingFactor = 0.5;
// User can set to -10 or 100 with no immediate error!
```

Validation only occurs at save time via `Validate()` method, allowing temporarily invalid state.

#### Impact
- **UX Issue:** No real-time feedback when user enters invalid value
- **Inconsistent State:** UI can display invalid values temporarily
- **Save Failures:** User doesn't know value is invalid until save attempt
- **Testing:** Harder to verify validation rules

#### Recommended Solution
Use MVVM Toolkit's partial property methods for validation:

```csharp
[ObservableProperty] private double _smoothingFactor = 0.5;

partial void OnSmoothingFactorChanging(double value)
{
    if (value < 0.0 || value > 1.0)
        throw new ArgumentOutOfRangeException(nameof(SmoothingFactor),
            "Must be between 0.0 and 1.0");
}
```

Or use custom setter with validation:
```csharp
public double SmoothingFactor
{
    get => _smoothingFactor;
    set
    {
        if (value < 0.0 || value > 1.0)
            return; // Or throw, or set to clamped value
        SetProperty(ref _smoothingFactor, value);
    }
}
```

#### Effort Estimate
- **Time:** 1-2 days
- **Risk:** Low
- **Benefits:** Medium (improves UX)

---

### Issue #8: DwellDetector Thread Safety
**Severity:** 🟡 SIGNIFICANT
**Location:** `src/EyeCtrl.Core/Processing/DwellDetector.cs`
**Category:** Concurrency

#### Problem Description
```csharp
private readonly ConcurrentDictionary<string, DwellTarget> _targets = new();
private long _cooldownMicroseconds;
private long _lastTimestamp;
private long _lastActivationTimestamp;
```

Uses `ConcurrentDictionary` but other fields are not thread-safe. The `Update()` method reads/writes `_lastTimestamp`, `_lastActivationTimestamp` without synchronization.

#### Impact
- **Race Condition:** If gaze provider fires events from background thread
- **Data Corruption:** Dwell progress corrupted with concurrent updates
- **Rare Bug:** Only manifests under specific timing conditions

#### Recommended Solution
**Option A: Make fields volatile**
```csharp
private volatile long _cooldownMicroseconds;
private volatile long _lastTimestamp;
```

**Option B: Add locking**
```csharp
private readonly object _lock = new();

public void Update(ScreenPoint point, long timestampUs)
{
    lock (_lock)
    {
        // existing logic
    }
}
```

**Recommendation:** Option B (safer, clearer)

#### Effort Estimate
- **Time:** 0.5 days
- **Risk:** Low
- **Benefits:** Medium (prevents race conditions)

---

### Issue #9: Large Procedural Window Classes
**Severity:** 🟡 SIGNIFICANT
**Location:** `SettingsWindow.xaml.cs` (1,004 lines), `KeyboardWindow.xaml.cs` (897 lines)
**Category:** Code Organization

#### Problem Description
- `SettingsWindow`: Manually builds UI with nested loops, manages 7 tab categories
- `KeyboardWindow`: Manually manages 40+ emoji grid elements
- All event subscriptions manual (no MVVM binding)
- Presentation logic mixed with UI code

#### Impact
- **Maintainability:** Hard to modify UI behavior
- **Testability:** Cannot unit test UI logic
- **Memory Leaks:** Manual event subscriptions may not be cleaned up

#### Recommended Solution
Convert to proper MVVM:
1. Create `SettingsViewModel` with tab/item collections
2. Create `KeyboardViewModel` with key grid
3. Use data binding instead of manual UI construction
4. Move logic out of code-behind

#### Effort Estimate
- **Time:** 5-7 days
- **Risk:** Medium
- **Benefits:** High (improves maintainability)

---

### Issue #10: Tight Adapter Coupling
**Severity:** 🟡 SIGNIFICANT
**Location:** `src/EyeCtrl.App/Services/GazeEngineAdapter.cs:73-124`
**Category:** Separation of Concerns

#### Problem Description
```csharp
private void OnShowActionMenu(ScreenPoint point) =>
    _dispatcher.BeginInvoke(() =>
    {
        var im = _interactionManager;
        var showBack = im.CurrentState == InteractionState.ZoomSelectionActionMenuVisible;
        var dwellMs = showBack ? _settings.Dwell.ZoomActionMenuDwellMs : _settings.Dwell.ActionMenuDwellMs;
        var zoomDisplayRadius = showBack ? (int)(_settings.Zoom.CircleRadiusPixels * _settings.Zoom.MagnificationLevel) : 0;
        // ... 15 more lines of coordinate calculations
    });
```

Adapter knows too much: state machine logic, settings structure, coordinate math.

#### Impact
- **Coupling:** Changes to zoom settings affect adapter
- **Testability:** Difficult to test this logic
- **Separation of Concerns:** Should be in ViewModel

#### Recommended Solution
Move coordinate calculations and layout logic to ViewModels.

#### Effort Estimate
- **Time:** 2 days
- **Risk:** Low
- **Benefits:** Medium

---

## Minor Issues (Priority 3)

### Issue #11: Magic Numbers and Hardcoded Constants
**Severity:** 🟢 MINOR
**Location:** Multiple files

Examples:
- `App.xaml.cs:259` - `buttonOffset = circleRadius + 55` (what is 55?)
- `ActionMenuWindow.xaml.cs:17` - `ButtonRadius = 50` (hardcoded in 3 places)
- `App.xaml.cs:344` - `gracePeriodUs = settings.Zoom.ZoomGracePeriodMs * 1000L` (magic 1000)

#### Recommended Solution
Extract to named constants:
```csharp
private const int ActivationButtonOffsetPixels = 55;
private const int ActionMenuButtonRadiusPixels = 50;
private const long MicrosecondsPerMillisecond = 1000L;
```

#### Effort Estimate: 0.5 days

---

### Issue #12: Missing Algorithm Documentation
**Severity:** 🟢 MINOR
**Location:** `OneEuroFilter.cs`, `FuzzyEditDistance.cs`

Academic algorithms lack explanatory comments about purpose and parameter tuning.

#### Recommended Solution
Add XML documentation with references to papers.

#### Effort Estimate: 0.5 days

---

### Issue #13: Inconsistent Naming Conventions
**Severity:** 🟢 MINOR
**Location:** Various

Some inconsistency in private field naming and parameter naming.

#### Recommended Solution
Add .editorconfig with naming rules, run cleanup.

#### Effort Estimate: 0.5 days

---

### Issue #14: No Overlay Factory Pattern
**Severity:** 🟢 MINOR
**Location:** App.xaml.cs

Overlay windows hardcoded, no factory for creating/managing them.

#### Recommended Solution
Create `IOverlayFactory` and centralized management (part of Issue #1 fix).

#### Effort Estimate: Included in Issue #1

---

### Issue #15: No Resource Cleanup Strategy
**Severity:** 🟢 MINOR
**Location:** ViewModels, App.xaml.cs

No consistent IDisposable implementation on ViewModels.

#### Recommended Solution
Implement IDisposable pattern (part of Issue #3 fix).

#### Effort Estimate: Included in Issue #3

---

### Issue #16: No Retry Logic or Circuit Breaker
**Severity:** 🟢 MINOR
**Location:** GazeProviderFactory, SettingsService

Gaze provider failures cause immediate fallback with no retry. Settings file corruption silently returns defaults.

#### Recommended Solution
Add retry logic with exponential backoff for transient failures.

#### Effort Estimate: 1 day

---

## Implementation Roadmap

### Phase 1: Foundation (Week 1)
**Goal:** Establish better patterns without breaking changes

- [ ] Issue #4: Fix Settings DI fragmentation (1 day)
- [ ] Issue #8: Add DwellDetector thread safety (0.5 days)
- [ ] Issue #3: Implement ViewModel disposal pattern (2 days)
- [ ] Issue #11: Extract magic numbers to constants (0.5 days)
- [ ] Issue #12: Add algorithm documentation (0.5 days)

**Total:** ~5 days
**Risk:** Low - mostly additive changes

### Phase 2: Communication Architecture (Week 2)
**Goal:** Unify message-based architecture

- [ ] Issue #2: Migrate to message-only communication (3 days)
- [ ] Issue #6: Fix InteractionManager state machine (2 days)

**Total:** ~5 days
**Risk:** Medium - requires coordination

### Phase 3: Type Safety (Week 3)
**Goal:** Prevent coordinate system bugs

- [ ] Issue #5: Implement coordinate type safety (4 days)
- [ ] Issue #7: Add property validation (1 day)

**Total:** ~5 days
**Risk:** Medium - touches many files

### Phase 4: Major Refactoring (Week 4-5)
**Goal:** Break up large classes

- [ ] Issue #1: Refactor App.xaml.cs (4 days)
- [ ] Issue #10: Simplify GazeEngineAdapter (2 days)
- [ ] Issue #9: Convert SettingsWindow to MVVM (4 days)

**Total:** ~10 days
**Risk:** Medium-High - requires careful testing

### Phase 5: Polish (Week 6)
**Goal:** Minor improvements

- [ ] Issue #13: Standardize naming conventions (0.5 days)
- [ ] Issue #16: Add retry logic (1 day)
- [ ] Issue #14: Implement overlay factory (included in Issue #1)
- [ ] Issue #15: Resource cleanup (included in Issue #3)

**Total:** ~2 days
**Risk:** Low

---

## Total Effort Estimate

| Phase | Duration | Risk Level |
|-------|----------|------------|
| Phase 1 | 5 days | Low |
| Phase 2 | 5 days | Medium |
| Phase 3 | 5 days | Medium |
| Phase 4 | 10 days | Medium-High |
| Phase 5 | 2 days | Low |
| **TOTAL** | **~5.5 weeks** | **Medium** |

---

## Success Metrics

### Code Quality Metrics
- [ ] App.xaml.cs reduced from 771 to <200 lines
- [ ] Zero manual event handler tracking
- [ ] All ViewModels implement IDisposable
- [ ] 100% of coordinates use strong types
- [ ] Zero unused enum values
- [ ] All magic numbers extracted to constants

### Testing Metrics
- [ ] Unit test coverage >80% for new code
- [ ] Memory leak tests pass after 1 hour
- [ ] No regression in existing tests
- [ ] State machine tests cover all transitions

### Performance Metrics
- [ ] No performance degradation
- [ ] Memory usage stable over 24 hours
- [ ] No new GC pressure from coordinate types

### Maintainability Metrics
- [ ] Cyclomatic complexity reduced by 30%
- [ ] Average method length reduced by 25%
- [ ] Number of classes with SRP violations reduced from 3 to 0

---

## Risk Mitigation

### High-Risk Changes
1. **Coordinate Type Refactoring (Issue #5)**
   - **Risk:** Touches many files, potential for regression
   - **Mitigation:** Incremental migration, comprehensive testing, feature flags

2. **App.xaml.cs Refactoring (Issue #1)**
   - **Risk:** Complex dependencies, may break initialization
   - **Mitigation:** Extract in small pieces, verify after each change

3. **Message Architecture Migration (Issue #2)**
   - **Risk:** May miss event subscriptions during conversion
   - **Mitigation:** Comprehensive testing, parallel implementation initially

### Testing Strategy
- Unit tests for all refactored logic
- Integration tests for communication flows
- Memory leak detection tests
- Manual testing of all features after each phase
- Regression test suite runs after every change

---

## Dependencies and Blockers

### External Dependencies
- None - all changes are internal refactoring

### Internal Dependencies
- Issue #2 should be done before Issue #1 (message architecture should be solid before refactoring App.xaml.cs)
- Issue #3 should be done before Issue #1 (disposal pattern needed for overlay manager)
- Issue #4 should be done first (simplifies DI in later refactorings)

---

## Conclusion

The EyeCtrl codebase has a solid foundation in the Core layer but suffers from architectural technical debt in the App layer. The main issues are:

1. **Monolithic App.xaml.cs** acting as god object
2. **Inconsistent communication patterns** (events + messages)
3. **Lack of resource management** (memory leak risk)
4. **Weak type safety** for coordinates
5. **Large procedural UI classes**

**Recommended Approach:**
- Start with low-risk foundational improvements (Phase 1)
- Progress to medium-risk architectural changes (Phases 2-3)
- Finally tackle high-risk major refactorings (Phase 4)

**Expected Outcome:**
After completing all phases, the codebase will have:
- Clear separation of concerns
- Consistent architectural patterns
- Better testability
- Reduced technical debt
- Lower maintenance burden
- More reliable resource management

**Business Value:**
- Faster feature development
- Fewer bugs
- Easier onboarding for new developers
- More stable application
- Better long-term maintainability

---

## Next Steps

1. **Review and Prioritize:** Stakeholders review this plan and adjust priorities
2. **Create Tracking Issues:** Break down into GitHub issues for each improvement
3. **Begin Phase 1:** Start with low-risk foundational changes
4. **Iterate:** Complete one phase before starting next
5. **Measure Progress:** Track metrics after each phase

---

**Document Status:** Draft - Pending Review
**Last Updated:** 2026-03-29
**Author:** Architecture Review Team
