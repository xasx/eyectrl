# EyeCtrl Architecture Issues - Quick Reference

**Review Date:** 2026-03-29
**Full Document:** [ARCHITECTURE_REVIEW_PLAN.md](./ARCHITECTURE_REVIEW_PLAN.md)

---

## Overview

This document provides a quick reference to the 16 architectural issues identified in the EyeCtrl codebase. For detailed analysis, solutions, and implementation roadmap, see the full [Architecture Review Plan](./ARCHITECTURE_REVIEW_PLAN.md).

---

## Critical Issues (Priority 1) 🔴

### 1. App.xaml.cs God Object
- **File:** `src/EyeCtrl.App/App.xaml.cs` (771 lines)
- **Problem:** Handles DI, logging, all ViewModels, all overlays, complex event chains with manual cleanup
- **Impact:** Unmaintainable, hard to test, tight coupling
- **Solution:** Extract to AppBootstrapper, OverlayManager, Event Aggregator
- **Effort:** 3-4 days

### 2. Dual Event + Message Patterns
- **Files:** Core layer (events) → GazeEngineAdapter (translation) → ViewModels (messages)
- **Problem:** Inconsistent communication - both raw events AND MVVM messages for same data
- **Impact:** Code duplication, cognitive load, difficult to trace flow
- **Solution:** Migrate to message-only architecture, remove GazeEngineAdapter
- **Effort:** 2-3 days

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

### 5. Coordinate System Type Safety
- **Files:** Multiple files using DpiHelper conversions
- **Problem:** 3 coordinate systems (logical/physical/normalized) all as plain (double X, double Y)
- **Impact:** Off-by-scale errors at non-96 DPI, zoom/menu positioning bugs
- **Solution:** Introduce LogicalPoint, PhysicalPoint, NormalizedPoint record types
- **Effort:** 3-4 days

### 6. Missing State Machine Transitions
- **File:** `src/EyeCtrl.Core/Processing/InteractionManager.cs`
- **Problem:** `ZoomSelectionFixation` state declared but NEVER used
- **Impact:** Dead code, incomplete state machine, misleading
- **Solution:** Remove unused state or implement transitions
- **Effort:** 1-2 days

---

## Significant Issues (Priority 2) 🟡

### 7. No Property Validation in Setters
- **Files:** All settings classes
- **Problem:** `[ObservableProperty]` without validation, errors only at save time
- **Solution:** Add validation in OnChanging methods or custom setters
- **Effort:** 1-2 days

### 8. DwellDetector Thread Safety
- **File:** `src/EyeCtrl.Core/Processing/DwellDetector.cs`
- **Problem:** Uses ConcurrentDictionary but other fields not thread-safe
- **Solution:** Add locking around Update() method
- **Effort:** 0.5 days

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

| Phase | Focus | Duration | Risk |
|-------|-------|----------|------|
| **Phase 1** | Foundation (Issues #4, #8, #3, #11, #12) | 5 days | Low |
| **Phase 2** | Communication (Issues #2, #6) | 5 days | Medium |
| **Phase 3** | Type Safety (Issues #5, #7) | 5 days | Medium |
| **Phase 4** | Major Refactoring (Issues #1, #10, #9) | 10 days | Medium-High |
| **Phase 5** | Polish (Issues #13, #16, #14, #15) | 2 days | Low |
| **TOTAL** | | **~5.5 weeks** | **Medium** |

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
- Remove unused `ZoomSelectionFixation` state (Issue #6 - if removing)

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
