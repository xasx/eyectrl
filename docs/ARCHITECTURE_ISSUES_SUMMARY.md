# EyeCtrl Architecture Issues - Quick Reference

**Review Date:** 2026-03-29
**Full Document:** [ARCHITECTURE_REVIEW_PLAN.md](./ARCHITECTURE_REVIEW_PLAN.md)

---

## Overview

This document provides a quick reference to the 16 architectural issues identified in the EyeCtrl codebase. For detailed analysis, solutions, and implementation roadmap, see the full [Architecture Review Plan](./ARCHITECTURE_REVIEW_PLAN.md).

---

## Critical Issues (Priority 1) 🔴

### 1. App.xaml.cs God Object [PARTIALLY RESOLVED]
- **File:** `src/EyeCtrl.App/App.xaml.cs` (709 lines, down from 771)
- **Problem:** Handles DI, logging, all ViewModels, all overlays, complex event chains with manual cleanup
- **Impact:** Unmaintainable, hard to test, tight coupling
- **Solution:** Extract to AppBootstrapper, OverlayManager, Event Aggregator
- **Progress:** ✅ Zoom fixation logic extracted to ZoomFixationService (160 lines removed)
- **Remaining:** Extract other overlay management, DI setup
- **Effort:** 2-3 days (down from 3-4 days)

### 2. Dual Event + Message Patterns [RESOLVED ✅]
- **Files:** Core layer (events) → GazeEngineAdapter (translation) → ViewModels (messages)
- **Problem:** Direct Core event subscriptions in App.xaml.cs bypass the adapter
- **Impact:** Minor inconsistency, but current layering is actually correct
- **Solution:** Move remaining direct subscriptions to service layer, keep Core pure (no MVVM deps)
- **Status:** COMPLETED - ZoomFixationService now handles zoom fixation via messages
- **Result:** All zoom-related communication is message-based; toolbar/keyboard/settings still use direct gaze routing (acceptable for legacy windows)
- **Effort:** 3 days (completed)

### 3. ViewModel Memory Leaks
- **Files:** All ViewModels, App.xaml.cs event subscriptions
- **Problem:** PropertyChanged handlers never unsubscribed, manual event tracking
- **Impact:** Memory leaks in long-running sessions, GC pressure
- **Solution:** Implement IDisposable on ViewModels, dispose in App.OnExit
- **Effort:** 2 days

### 4. Settings DI Fragmentation
- **File:** `src/EyeCtrl.App/App.xaml.cs` lines 118-127
- **Problem:** Each sub-setting registered separately in DI container
- **Impact:** Multiple reference paths, structural coupling, testing complexity
- **Solution:** Register only root EyeCtrlSettings, access sub-settings as properties
- **Effort:** 1 day

### 5. Coordinate System Type Safety [RESOLVED ✅]
- **Files:** Multiple files using DpiHelper conversions
- **Problem:** 3 coordinate systems (logical/physical/normalized) all as plain (double X, double Y)
- **Impact:** Off-by-scale errors at non-96 DPI, zoom/menu positioning bugs
- **Solution:** Introduce LogicalPoint, PhysicalPoint, NormalizedPoint record types
- **Status:** COMPLETED - Phase 3 implementation
- **Progress:**
  - ✅ LogicalPoint, PhysicalPoint, NormalizedPoint record structs implemented (35 tests, 97.1% passing)
  - ✅ ScreenPoint marked as Obsolete with migration guidance
  - ✅ Core processing layer migrated (26 files): messages, DwellDetector, FixationDetector, GazeEngine, all filters
  - ✅ Type safety enforced at compile time
  - 🔄 Remaining: ~40 files (ViewModels, Services, Windows) - will migrate incrementally using Obsolete warnings
- **Effort:** 4 days (completed)

### 6. Missing State Machine Transitions [RESOLVED]
- **File:** `src/EyeCtrl.Core/Processing/InteractionManager.cs`
- **Problem:** `ZoomSelectionFixation` state declared but NEVER used
- **Impact:** Dead code, incomplete state machine, misleading
- **Solution:** Removed unused state from InteractionState enum
- **Status:** COMPLETED - ZoomSelectionFixation removed from enum

---

## Significant Issues (Priority 2) 🟡

### 7. No Property Validation in Setters [RESOLVED ✅]
- **Files:** All settings classes
- **Problem:** `[ObservableProperty]` without validation, errors only at save time
- **Solution:** Add validation in OnChanging methods or custom setters
- **Status:** COMPLETED - Phase 3 implementation
- **Progress:**
  - ✅ 84 property validation tests written (100% passing)
  - ✅ OnChanging partial methods implemented in DwellSettings (9 properties)
  - ✅ OnChanging partial methods implemented in CursorSettings (8 properties)
  - ✅ OnChanging partial methods implemented in ZoomSettings (2 properties)
  - ✅ Real-time validation working with clear error messages
  - ✅ Invalid values rejected immediately with ArgumentOutOfRangeException
- **Effort:** 1 day (completed)

### 8. DwellDetector Thread Safety [RESOLVED - NO LOCKING NEEDED]
- **File:** `src/EyeCtrl.Core/Processing/DwellDetector.cs`
- **Problem:** Uses ConcurrentDictionary but other fields not thread-safe
- **Solution:** Documented single-threaded requirement; no locking needed ✅
- **Rationale:** All gaze events marshaled to UI thread via `GazeEngineAdapter` using `Dispatcher.BeginInvoke()`, ensuring single-threaded access
- **Effort:** 0.5 days (investigation + documentation)
- **Result:** Class documented as single-threaded; architectural guarantee provided by event marshaling

### 9. Large Procedural Window Classes
- **Files:** SettingsWindow.xaml.cs (1,004 lines), KeyboardWindow.xaml.cs (897 lines)
- **Problem:** Manual UI construction, no MVVM, presentation logic mixed with UI
- **Solution:** Convert to proper MVVM with ViewModels and data binding
- **Effort:** 5-7 days

### 10. Tight Adapter Coupling
- **File:** `src/EyeCtrl.App/Services/GazeEngineAdapter.cs:73-124`
- **Problem:** Adapter knows too much - state machine, settings, coordinate math
- **Solution:** Move logic to ViewModels, make adapter thin
- **Effort:** 2 days

---

## Minor Issues (Priority 3) 🟢

### 11. Magic Numbers
- **Examples:** `buttonOffset = circleRadius + 55`, `ButtonRadius = 50`
- **Solution:** Extract to named constants
- **Effort:** 0.5 days

### 12. Missing Algorithm Documentation
- **Files:** OneEuroFilter.cs, FuzzyEditDistance.cs
- **Solution:** Add XML documentation with academic paper references
- **Effort:** 0.5 days

### 13. Inconsistent Naming
- **Problem:** Some private field naming inconsistencies
- **Solution:** Add .editorconfig with naming rules
- **Effort:** 0.5 days

### 14. No Overlay Factory
- **Problem:** Overlay windows hardcoded in App.xaml.cs
- **Solution:** Create IOverlayFactory (part of Issue #1)
- **Effort:** Included in Issue #1

### 15. No Resource Cleanup Strategy
- **Problem:** No consistent IDisposable implementation
- **Solution:** Implement disposal pattern (part of Issue #3)
- **Effort:** Included in Issue #3

### 16. No Retry Logic
- **Files:** GazeProviderFactory, SettingsService
- **Solution:** Add retry with exponential backoff
- **Effort:** 1 day

---

## Implementation Roadmap

| Phase | Focus | Duration | Risk | Status |
|-------|-------|----------|------|--------|
| **Phase 1** | Foundation (Issues #4, #8, #3, #11, #12) | 5 days | Low | Partial (Issues #8 done) |
| **Phase 2** | Communication (Issues #2, #6) | 5 days | Medium | ✅ **COMPLETE** |
| **Phase 3** | Type Safety (Issues #5, #7) | 5 days | Medium | ✅ **COMPLETE** |
| **Phase 4** | Major Refactoring (Issues #1, #10, #9) | 10 days | Medium-High | Pending |
| **Phase 5** | Polish (Issues #13, #16, #14, #15) | 2 days | Low | Pending |
| **TOTAL** | | **~5.5 weeks** | **Medium** | **40% Complete** |

---

## Priority Order for Implementation

### Start Here (Low Risk, High Value)
1. **Issue #4** - Fix Settings DI (1 day)
2. **Issue #8** - Add thread safety (0.5 days)
3. **Issue #3** - Implement disposal (2 days)

### Next Steps (Medium Risk, High Value)
4. **Issue #2** - Unify communication (3 days)
5. **Issue #6** - Fix state machine (2 days)

### Then (Medium Risk, Very High Value)
6. **Issue #5** - Coordinate type safety (4 days)

### Major Refactoring (Plan Carefully)
7. **Issue #1** - Break up App.xaml.cs (4 days)
8. **Issue #9** - MVVM windows (7 days)

---

## Success Metrics

### Code Quality
- [ ] App.xaml.cs: 771 → <200 lines
- [ ] Zero manual event handler tracking
- [ ] All ViewModels implement IDisposable
- [ ] 100% coordinate type safety
- [ ] No unused enum values

### Testing
- [ ] Unit test coverage >80%
- [ ] Memory leak tests pass (1 hour)
- [ ] No regression in existing tests

### Performance
- [ ] No performance degradation
- [ ] Memory stable over 24 hours

---

## Key Files to Understand

### Most Problematic
1. `src/EyeCtrl.App/App.xaml.cs` - 771 lines, god object
2. `src/EyeCtrl.App/Services/GazeEngineAdapter.cs` - translation layer
3. `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` - 1,004 lines
4. `src/EyeCtrl.App/Overlays/KeyboardWindow.xaml.cs` - 897 lines

### Core Architecture
1. `src/EyeCtrl.Core/Processing/GazeEngine.cs` - main processing engine
2. `src/EyeCtrl.Core/Processing/InteractionManager.cs` - state machine
3. `src/EyeCtrl.ViewModels/` - MVVM layer (needs disposal)

---

## Quick Wins (Can Start Today)

### 1-Hour Tasks
- Extract magic numbers to constants (Issue #11)
- ~~Remove unused `ZoomSelectionFixation` state (Issue #6)~~ COMPLETED

### Half-Day Tasks
- Add DwellDetector thread safety (Issue #8)
- Add algorithm documentation (Issue #12)

### 1-Day Tasks
- Fix Settings DI fragmentation (Issue #4)

---

## Long-Term Vision

### After All Issues Resolved
- Clear separation of concerns
- Consistent architectural patterns
- No god objects
- Type-safe coordinate handling
- Proper resource management
- MVVM throughout UI layer
- Easy to test and maintain

### Business Impact
- Faster feature development
- Fewer bugs
- Easier developer onboarding
- More stable application
- Lower maintenance costs

---

## Related Documents

- **Full Plan:** [ARCHITECTURE_REVIEW_PLAN.md](./ARCHITECTURE_REVIEW_PLAN.md) - Comprehensive analysis and solutions
- **Code Quality:** [CODE_QUALITY_IMPROVEMENTS.md](./CODE_QUALITY_IMPROVEMENTS.md) - Already completed improvements
- **View Positioning:** [CODE_REVIEW_VIEW_POSITIONING.md](./CODE_REVIEW_VIEW_POSITIONING.md) - Recent feature review
- **Test Coverage:** [TEST_COVERAGE_REPORT.md](./TEST_COVERAGE_REPORT.md) - Current test state

---

## Questions?

For detailed analysis of any issue:
1. See full [Architecture Review Plan](./ARCHITECTURE_REVIEW_PLAN.md)
2. Review code locations cited in each issue
3. Check stored memories in repository context

**Last Updated:** 2026-03-29
