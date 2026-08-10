# EventCoordinator Decomposition Plan

> Companion to [`ARCHITECTURE_ASSESSMENT_2026-08.md`](./ARCHITECTURE_ASSESSMENT_2026-08.md) (P1 finding).
> Grounded in a full read of `src/EyeCtrl.App/Services/EventCoordinator.cs` (1,654 lines, 21
> `Handle*`/`On*` handlers — 32 non-trivial methods counting `Setup*`/`Execute*` helpers). Line numbers
> refer to that file as of 2026-08-10 (commit `fde511c`).

## The problem

`EventCoordinator` is the **largest file in the codebase** (1,654 lines). It was extracted from
`App.xaml.cs` (which correctly shrank from 771 → 541) but absorbed that gravity: it now wires ~8 view
models plus drag, quick-actions, media, window-actions, keyboard, emoji, prediction, calibration,
backdrop, target-highlight, and settings through 21 `Handle*`/`On*` handler methods (32 non-trivial
methods counting `Setup*`/`Execute*` helpers). The March 2026
review (`docs/ARCHITECTURE_ISSUES_SUMMARY.md`) never flagged it — its hotspot map is stale.

It violates the Single Responsibility Principle it was created to restore: the class-level doc comment
says *"Extracted from App.xaml.cs to improve Single Responsibility Principle compliance."*

## Why it is safe to split

Two properties make a mechanical, low-risk decomposition possible:

1. **`EventCoordinationContext`** (passed to `Initialize`, `EventCoordinator.cs:95`) is already a bag
   of every participant — VMs, overlay manager, messenger, dispatcher, settings, gaze engine, input
   simulator, etc. Each sub-coordinator can take the context (or a narrowed slice) in its constructor.
2. **`EventCoordinator : IEventCoordinator`** is already an interface — but note that `App.xaml.cs:42`
   holds the **concrete** type (`private EventCoordinator _eventCoordinator = null!;`), and calls
   concrete-only members on it (`SetModelServices` :225, `ForceShowToolbar` :236,
   `SetRestartCoordinator` :306). So the surface the orchestrator must preserve is the concrete
   class's full public surface, not just the interface:

   - **`IEventCoordinator` (4 members):** `WordPredictor`, `Initialize`, `LoadKeyboard`, `Dispose`.
   - **Concrete-only (4 more):** `Orchestrator`, `SetModelServices`, `SetRestartCoordinator`,
     `ForceShowToolbar`.

   The thin orchestrator re-exposes all 8 and delegates internally — `WordPredictor` / `Orchestrator`
   forward to whichever sub-coordinator owns `_predictionFactory` (KeyboardCoordinator). Then **no
   caller changes are required**.

The messenger registration pattern (`_context.Messenger.Register<EventCoordinator, TMsg>(this, …)`)
becomes `Register<SubCoordinator, TMsg>(sub, …)` — the `this` argument changes per owner. Each
sub-coordinator owns its own registrations and its own `Dispose` unregistration.

---

## Responsibility clusters (with line ranges)

The file decomposes into 7 cohesive clusters. Each is largely self-contained and was identified by
grouping methods that share collaborators and change for the same reasons.

| # | Cluster | Lines (approx) | Collaborators |
|---|---------|-----------------|---------------|
| 1 | **Overlay VM → window bindings** | `SetupPropertyChangedHandlers` 341–375; `OnCursorVM/Activation/ManualSelection/Indicator/Visibility/ActionMenu/Toolbar/Backdrop` 1012–1204; `SetupBackdrop`/`ShowBackdropIfEnabled`/`HideBackdrop` 865–871, 983–1010 | All overlay windows + their VMs |
| 2 | **Message → input dispatch** | `SetupMessageHandlers` 377–605 (the `ActionRequested/KeyActivated/Prediction/SpecialKey/ShowActionMenu/HideActionMenu/BackdropDismiss` regs); `HandleActionRequested/HandleKeyActivation/HandlePredictionSelected/HandleSpecialKey` 1319–1488; `HandleTaskSwitcherSelection` 152–159; `ExecuteQuickAction/ExecuteKeyboardShortcut/ExecuteMediaControl/ExecuteWindowAction` 165–329 | `IInputSimulator`, `IWindowManager`, `WindowsAppLauncher` |
| 3 | **Keyboard lifecycle** | `KeyboardToggled/ShowKeyboard/HideKeyboard` regs (437–548); `SetKeyboardVisible` 1543–1569; `LoadKeyboard` 1490–1537; `OnEmojiSkinToneChanged` 1571–1577 | `KeyboardWindow`, `PredictionStackFactory`, `SettingsService` |
| 4 | **Drag** | `SetupDragHandlers` 872–893; `OnThreadFilterMessage` 895–905; `OnDragStarted/OnDragDrop/OnDragCancel/CloseDragOverlay/OnDragTrailVMPropertyChanged` 907–981 | `InteractionManager.DragStarted`, `EdgeButtonBar`, `DragTrailOverlay`, `IInputSimulator` |
| 5 | **Quick Actions** | `QuickActionSelectionMessage` reg (137–149); `UpdateQuickActionsEffectiveEdge/AttachQuickActionsSettingsHandler/SetupQuickActionsSummon/ComputeActualSummonEdge` 700–852 | `QuickActionsSummonController`, `QuickActionsGridViewModel`, `QuickActionsStore` |
| 6 | **Toolbar auto-hide + gaze routing** | `SetupToolbarAutoHide`/`RefreshToolbarRect` 640–698, 1298–1305; `CaptureToolbarRect` 854–863; `SetupGazeRouting` 607–638; toolbar-rect branch of `OnSettingsChanged` 1272–1287 | `ToolbarAutoHideController`, `ToolbarVisibilityAdapter`, `GazeEngine` |
| 7 | **Settings UI lifecycle** | `SettingsRequested/CloseSettings` regs (442–469); `ShowSettingsWindow` 1208–1261; `OnSettingsChanged` 1263–1296; `OnSettingsWindowClosed` 1307–1317; `UpdateTrackStatusVisibility` 331–339 | `SettingsWindow`, `SettingsViewModel`, `SettingsWindowViewModel`, `IOverlayManager` |

The remaining structure is the thin orchestrator shell — fields (`30–65`), the public surface
(`70–93`), `Initialize` (`95–150`), `Dispose` (`1581–1654`) — roughly 190 lines. *(The cluster ranges
above are approximate bounding boxes, not partition cut-lines: `SetupMessageHandlers` 377–605
interleaves message registrations belonging to clusters 1/2/3/6/7, so the ranges overlap and do not
sum to the 1,654-line file length. Treat them as a map to where each responsibility lives, and verify
the exact spans against the file before cutting.)*

---

## Proposed target structure

```
EventCoordinator  (thin orchestrator, ~150 lines)  : IEventCoordinator
 ├── Initialize(context)      → construct + Initialize each sub-coordinator, in order
 ├── Dispose()                → dispose each (reverse order)
 ├── LoadKeyboard(...)        → delegates to KeyboardCoordinator
 ├── WordPredictor / Orchestrator → forward to KeyboardCoordinator (_predictionFactory owner)
 ├── SetModelServices(...)    → forwards to SettingsUiCoordinator + KeyboardCoordinator
 ├── SetRestartCoordinator()  → forwards to SettingsUiCoordinator
 └── ForceShowToolbar()       → forwards to ToolbarLifecycleCoordinator
 │
 ├── OverlayBindingCoordinator   (cluster 1)   ~260 lines
 ├── InputActionDispatcher       (cluster 2)   ~330 lines
 ├── KeyboardCoordinator         (cluster 3)   ~150 lines
 ├── DragCoordinator             (cluster 4)   ~140 lines
 ├── QuickActionsCoordinator     (cluster 5)   ~180 lines
 ├── ToolbarLifecycleCoordinator (cluster 6)   ~130 lines   (auto-hide + per-sample gaze routing)
 └── SettingsUiCoordinator       (cluster 7)   ~170 lines
```

Each sub-coordinator:

- Implements a small interface (e.g. `IOverlayBindingCoordinator`) or is constructed directly.
- Takes the slices of `EventCoordinationContext` it needs (narrow the context per coordinator — see
  "context narrowing" below).
- Owns its own `Messenger.Register<TMsg>` / `PropertyChanged +=` in an `Initialize()` and unsubscribes
  in a `Dispose()`.
- Has **its own test project fixture path**: today `EventCoordinator` is exercised through
  `EyeCtrl.App.Tests`; after the split, each coordinator is independently unit-testable with the
  private nested fakes the repo already favors.

### Context narrowing

`EventCoordinationContext` is currently a fat bag. As a follow-on (not blocking), narrow each
sub-coordinator's constructor to just what it touches, so the compiler enforces the cluster's real
dependencies. Example:

```csharp
internal sealed class DragCoordinator(
    InteractionManager interactionManager,
    IOverlayManager overlays,
    DragTrailViewModel dragTrailVm,
    IMessenger messenger,
    Dispatcher dispatcher,
    IInputSimulator inputSimulator,
    EyeCtrlSettings settings) : IDisposable { ... }
```

This is optional polish — the first pass can pass the whole `EventCoordinationContext` to keep the
diff mechanical.

### Shared cross-cluster state

A few fields are read across clusters and must be lifted to a tiny shared object rather than duplicated:

| Field | Read by | Lift to |
|---|---|---|
| `_keyboardVisible` | KeyboardCoordinator (writes), GazeRouting (reads), InputDispatcher (no) | `KeyboardState` shared record |
| `_settingsOpen` | SettingsUiCoordinator (writes), OverlayBindingCoordinator (cursor show guard), GazeRouting (reads) | `ModalState` shared record |
| `_toolbarAutoHide` / `_toolbarVisibility` | ToolbarLifecycleCoordinator (owns), SettingsUiCoordinator + KeyboardCoordinator (`SetSuspended`) | ToolbarLifecycleCoordinator exposes `Suspend()`/`Resume()` |

Introduce a small `CoordinatorState` object held by the orchestrator and passed to the coordinators
that need it. This keeps the cross-cluster coupling explicit and tiny (3 booleans + 2 controller
refs), rather than scattered through a 1,654-line file.

---

## Phased migration (extract one cluster per PR)

Each phase is independently shippable. The orchestrator façade (`IEventCoordinator`) is unchanged at
every step, so **no caller or test needs to change until phase 7**. Run `dotnet test` after each phase.

**Ordering rationale:** start with the cluster that has the fewest cross-cluster dependencies (drag),
then the most self-contained (quick-actions), then the purely imperative dispatchers, leaving the
orchestrator-glue clusters (overlay bindings, settings UI) for last because they touch the shared
modal-state flags.

| Phase | Extract | Est. | Risk | Why this order |
|------|--------|------|------|----------------|
| 1 | `DragCoordinator` (cluster 4) | 0.5 d | Low | Fewest shared fields; clean events; contains its own escape-key hook + trail overlay lifecycle |
| 2 | `QuickActionsCoordinator` (cluster 5) | 0.5 d | Low | Self-contained controller + one VM; only touches shared state via suspend calls |
| 3 | `InputActionDispatcher` (cluster 2) | 1 d | Low | Pure message → `IInputSimulator`/`IWindowManager` translation; no overlay state |
| 4 | `ToolbarLifecycleCoordinator` (cluster 6) | 1 d | Med | Owns auto-hide + gaze routing; introduces `Suspend()`/`Resume()` — must precede `KeyboardCoordinator`, whose `SetKeyboardVisible` (`EventCoordinator.cs:1547`) calls `_toolbarAutoHide?.SetSuspended` |
| 5 | `KeyboardCoordinator` (cluster 3) | 1 d | Med | Owns `_keyboardVisible` + `PredictionStackFactory` + `LoadKeyboard`; uses `ToolbarLifecycle`'s `Suspend()`/`Resume()` (phase 4); introduces the `KeyboardState` shared flag |
| 6 | `OverlayBindingCoordinator` (cluster 1) | 1 d | Med | Largest cluster; pure VM→window glue once modal flags are shared |
| 7 | `SettingsUiCoordinator` (cluster 7) | 0.5 d | Low | Once its suspend-calls (phases 2 & 4 — QuickActions + Toolbar) are settled, this is mechanical |
| 8 | Final cleanup | 0.5 d | Low | `EventCoordinator` is now ~150 lines; remove dead fields; update `ARCHITECTURE.md` flows |

**Total: ~6 days**, shippable as 7–8 small PRs, each leaving the build green and tests passing.

---

## Sequencing constraint: `Initialize` ordering

`EventCoordinator.Initialize` (95–150) currently calls the `Setup*` methods in a specific order with
real dependencies — e.g. `SetupGazeRouting` reads flags set by keyboard/settings, and
`SetupToolbarAutoHide` must run before `ShowOverlays` caches the toolbar rect (see the comment at
640–698). The orchestrator must preserve this order:

```
OverlayBindingCoordinator.Initialize()   // wires VM→window BEFORE gaze starts
ToolbarLifecycleCoordinator.Initialize() // captures toolbar rect before first sample
QuickActionsCoordinator.Initialize()
KeyboardCoordinator.Initialize()
DragCoordinator.Initialize()
InputActionDispatcher.Initialize()
SettingsUiCoordinator.Initialize()
```

Capture the current call order from `Initialize` verbatim and reproduce it in the orchestrator; do
not reorder.

---

## Risks and mitigations

| Risk | Mitigation |
|------|-----------|
| Messenger `Register` owner-arg change breaks delivery | Each sub-coordinator registers with **itself** as the recipient/owner; `Dispose` calls `UnregisterAll(this)` on that coordinator. Add one test per coordinator asserting it receives its messages. |
| Cross-cluster shared flags (`_keyboardVisible`, `_settingsOpen`) drift | Lift to a single `CoordinatorState` object owned by the orchestrator; pass by reference. Only 3 fields — keep it that way. |
| `Dispose` ordering — auto-hide gaze handler must unsubscribe | Each coordinator owns its own handler fields and unsubscribes in its own `Dispose`; orchestrator disposes in reverse-init order. The existing careful `Dispose` (1581–1654) is the template. |
| The `CalibrationStateChangedMessage` handler (551–599) reads `_toolbarAutoHide` + `_toolbarVisibility` + cursor + calibration window | Move it into `OverlayBindingCoordinator` and have it call `ToolbarLifecycleCoordinator.ReassertVisibility()` via the shared interface — do **not** duplicate the controller refs. |
| `EventCoordinatorTests` (in `EyeCtrl.App.Tests`) currently asserts end-to-end wiring | Keep those tests pointing at the `IEventCoordinator` façade; they become the integration tests for the orchestrator. Add narrower unit tests per sub-coordinator. |

---

## Acceptance criteria

- [ ] `EventCoordinator.cs` ≤ 200 lines (orchestrator + `IEventCoordinator` impl).
- [ ] No sub-coordinator file > 350 lines.
- [ ] The **concrete** `EventCoordinator` public surface preserved (all 8 members: `WordPredictor`,
      `Orchestrator`, `Initialize`, `LoadKeyboard`, `Dispose`, `SetModelServices`,
      `SetRestartCoordinator`, `ForceShowToolbar`) — `App.xaml.cs:42` holds the concrete type, not the
      interface, so this is the surface that must survive unchanged for zero caller edits.
- [ ] All existing `EyeCtrl.App.Tests` pass unmodified (the integration safety net).
- [ ] Each sub-coordinator has at least one unit test exercising its primary message/property path.
- [ ] The new layering test (`tests/EyeCtrl.Architecture.Tests`) still passes — the split is all
      within `EyeCtrl.App`, so no cross-project invariant is touched.
- [ ] `ARCHITECTURE.md §4.5` (Action Selection) and §4.10 (Settings flow) flow references updated to
      point at the new coordinator names after phase 7.

---

## Non-goals

- This plan does **not** touch `SettingsWindow.xaml.cs` (1,630 lines) — that is the separate P1 item
  #3 in the assessment (re-MVVM the settings window). The two can proceed independently; doing
  `SettingsUiCoordinator` first actually *helps* a later SettingsWindow MVVM extraction by isolating
  the construction wiring.
- It does **not** change the `WeakReferenceMessenger` choice or move to a different pub/sub model.
- It does **not** introduce a mocking library — sub-coordinators stay testable with the repo's existing
  private-nested-fake convention.
