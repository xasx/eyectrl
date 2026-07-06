# Unified Right-Edge Button Bar

**Date:** 2026-07-05
**Status:** Draft
**Author:** EyeCtrl Team
**Tracking:** GitHub issue #55
**Prerequisite:** PR #56 (Architecture remediation) merged

## Problem

Several features each render their own transient dwell-button surface at the right screen edge, each with its own overlay window, button layout, and dwell wiring:

- **Drag cancel/back** — rendered as bare ellipses inside `DragTrailOverlay`, with **no progress feedback** (`DragTrailViewModel.OnTargetProgress` is empty).
- **ManualSelection close/recapture** — `ManualSelectionSideButtonWindow`, its own window.
- **Scroll stop** — embedded in `ScrollOverlayWindow`'s HUD.

Each reimplements button rendering, dwell-target wiring, and positioning. PR #56 already consolidated the **rendering primitive** (`DwellArcButton`) and the **VM boilerplate** (`DwellOverlayViewModelBase`) — e.g. `ManualSelectionSideButtonWindow` now uses `DwellArcButton`, and the three VMs derive from the base. But each surface is still a separate window+VM; the **surface/wiring duplication** remains. This issue consolidates the surfaces themselves.

## Goals

- One right-edge button bar (VM + window) serves all transient right-edge dwell buttons across modes.
- Each mode declares its button set; the bar owns dwell detection and rendering for those buttons.
- Consistent visuals and progress feedback for all right-edge buttons (fixes drag's missing progress).
- A stable, predictable location for right-edge controls (the user always knows where cancel/stop/close/recapture live).
- New features declare buttons instead of building a new window.

## Non-Goals

- Touching the always-on docked `ToolbarWindow` (pause/keyboard/calibrate/settings). It stays as-is.
- Moving the cursor-following drag **release** (✋) button — it is not an edge button and stays in `DragTrailOverlay`.
- Changing the scroll HUD visuals (crosshair, anchor dot, velocity ring, cursor circle). Only the stop button leaves the HUD.
- Unifying dwell **detection** for non-edge buttons (release, etc.) — only the fixed right-edge buttons consolidate.

## Terminology

"Manual selection" is the term used throughout. The drag code currently uses "Zoom" for what is actually manual selection engaging during a drag; this work renames those symbols:

- `DragSubState.ZoomActive` → `ManualSelectionActive`
- `DragTrailViewModel.DismissZoom()` → `DismissManualSelection()`
- `DragDismissZoomMessage` → `DragDismissManualSelectionMessage`

Renames are performed with the graph-aware `rename` tool against the post-#56 graph.

Note the two distinct "manual selection" notions after rename: **standalone** mode (`ManualSelectionCloseMessage`/`ManualSelectionRecaptureMessage`, the close/recapture buttons) and **mid-drag engagement** (`DragDismissManualSelectionMessage`, the back button). The `Drag` prefix on the latter marks it as drag-context; don't shorten it to read as a standalone-mode message.

## Prerequisite: PR #56

This design **builds on PR #56**, which provides:

- `EyeCtrl.App.Controls.DwellArcButton` — factory creating the round background + arc-ring progress + symbol trio; `Create(center, radius, symbol, ...)`, `UpdateProgress(progress)`, `AddTo(canvas)`, static `UpdateArc(...)`.
- `EyeCtrl.ViewModels.DwellOverlayViewModelBase` (extends `MessengerViewModelBase`) — owns a `DwellDetector`, subscribes to `GazeCursorMovedMessage` (feeds detector only while `IsVisible`), wires `TargetActivated`/`TargetProgress`, handles disposal. Derived classes override `OnTargetActivated`/`OnTargetProgress` and add/clear targets.
- `MessengerViewModelBase` — messenger registration/disposal.
- `ScrollModeController` — extracted scroll-mode logic (App layer).

The three target VMs already derive from `DwellOverlayViewModelBase` post-#56. **#56 is merged on `origin/main`.** Implementation must branch from an up-to-date `main` (the GitNexus index should be re-run before editing — see #56's notes).

## Design

### Architecture

A single **`EdgeButtonBarWindow`** docked at the right screen edge (anchor Y per use case — see Positioning), backed by **`EdgeButtonBarViewModel`**. The active interaction context (drag / scroll / manual selection) declares its button set; the bar renders the buttons and runs dwell detection for them. When the context ends, the bar clears and hides. The four interaction states are mutually exclusive, so one bar instance serves all of them.

### Components

**`EdgeButtonDefinition`** (record, `EyeCtrl.ViewModels`): pure data, no WPF.

```
EdgeButtonDefinition(string Id, string Symbol, int DwellMs, Action<IMessenger> Activate, double VerticalOffset)
```

`Symbol` is the Unicode glyph rendered by `DwellArcButton` (named to match `DwellArcButton.Create`'s `symbol` parameter and `ToolbarButtonViewModel.Symbol`). `Activate` is a callback the bar invokes on dwell activation, e.g. `m => m.Send(new DragCancelMessage())`. (The plan refines the earlier `object ActivationMessage` form: `IMessenger.Send<TMessage>` is generic over the compile-time message type, so a stored `object` cannot be dispatched to type-specific handlers without reflection; an `Action<IMessenger>` closes over the concrete message type and is reflection-free.) All edge-button activations are parameterless — cancel, dismiss-manual-selection, stop, close, recapture — so the callback captures a pre-constructed message and sends it as-is. This keeps the bar decoupled from each mode's internals: it only knows how to forward an activation as a message.

`VerticalOffset` is a **signed physical-px offset from the anchor Y** (positive = below the anchor, negative = above). Each button is placed at `(edgeAnchor.X, edgeAnchor.Y + VerticalOffset)` — a **fixed slot**. This is the core positioning model: buttons do *not* re-stack or re-center when the set changes, so a surviving button's slot (and therefore its `DwellTarget` center) never moves when another button is added/removed. This preserves dwell progress on surviving buttons (a previous "re-center the cluster" model was rejected precisely because it moved targets out from under the user's gaze). The offset is relative, not an absolute screen coordinate, so callers stay decoupled from screen geometry (the App layer computes the anchor; definitions declare only "how far from the anchor").

**`EdgeButtonState`** (`EyeCtrl.ViewModels`): the observable runtime state the View binds to (one per visible button). Materially distinct from `EdgeButtonDefinition` — the definition is the immutable input spec; the state is the mutable, observed runtime.

```
EdgeButtonState(string Id, string Symbol) : ObservableObject
    [ObservableProperty] private double _dwellProgress
```

`DwellProgress` (0..1) raises `PropertyChanged` so the View drives `DwellArcButton.UpdateProgress` reactively.

**`EdgeButtonBarViewModel : DwellOverlayViewModelBase`** (`EyeCtrl.ViewModels`):

- Inherits the `DwellDetector`, gaze subscription, and disposal from the base.
- `ObservableCollection<EdgeButtonState>` for the View to bind against.
- The bar **owns button layout** — callers supply the anchor and definitions (each carrying its own `VerticalOffset`), never per-button centers. The VM computes each button's center as `(edgeAnchor.X, edgeAnchor.Y + def.VerticalOffset)` so seed centers are arithmetic-accurate from the first frame.
- `ShowButtons(IReadOnlyList<EdgeButtonDefinition> definitions, PhysicalPoint edgeAnchor)` — caches `edgeAnchor` as the slot origin, clears existing targets and buttons, computes each button's seed center from the anchor + its `VerticalOffset`, adds a `DwellTarget` per button (seeded at those centers), populates the `EdgeButtonState` collection, sets `IsVisible = true`. `edgeAnchor.X` is the right screen edge; `edgeAnchor.Y` is the **per-use-case anchor Y** (see Positioning). Seed centers are authoritative (see Window lifecycle wiring).
- `AddButton(EdgeButtonDefinition)` / `RemoveButton(string id)` — for the mid-drag dynamic change (back button). **One-arg** `AddButton` uses the anchor cached by the last `ShowButtons` (a caller passing a different anchor would misalign the new button relative to survivors — the very thing fixed slots prevent). Because each button occupies its own fixed slot (`anchor.Y + VerticalOffset`), adding/removing one does **not** move any other button; surviving `DwellTarget` centers and dwell progress are untouched. The View only adds/removes the one `DwellArcButton`.
- `Hide()` — clears all targets and the visible set, sets `IsVisible = false`. Called on every context exit to prevent stale buttons. Must run on the dispatcher thread so it cannot interleave mid-`DwellDetector.Update` (see Edge Cases).
- `override OnTargetActivated(string id)` — looks up the button's `Activate` callback and invokes it with `Messenger`.
- `override OnTargetProgress(string id, double progress)` — updates the matching `EdgeButtonState.DwellProgress`.
- Constructor passes `settings.Dwell.DwellCooldownMs` to the `DwellOverlayViewModelBase` base (same cooldown source as the other dwell overlay VMs).

Screen geometry (right-edge X, per-use-case anchor Y) is computed in the App layer (it already lives there) and passed in via `edgeAnchor`, keeping the VM framework-agnostic.

**`EdgeButtonBarWindow`** (`EyeCtrl.App.Overlays`):

- Right-edge overlay (`OverlayWindow` subclass), `IsClickThrough = true`, `Topmost`.
- Renders a vertical stack of `DwellArcButton` instances — **reuses PR #56's primitive; no new rendering code.** Converts the VM's physical seed centers to WPF `Point` (DIP) via `DpiHelper` at the boundary when calling `DwellArcButton.Create` (the same conversion `ManualSelectionSideButtonWindow.ShowAt` performs today — `DwellArcButton.Create` takes a DIP `Point`, the VM speaks `PhysicalPoint`).
- **Bounding-box window + window-relative canvas coords** (mirrors `ManualSelectionSideButtonWindow.ShowAt`, NOT `ToolbarWindow`). The window sizes itself to the buttons' bounding box and each button is drawn at `seedCenter - windowOrigin`, so the button's screen center equals its seed center exactly. No `SizeToContent` (it would override the explicit size).
- Subscribes to each `EdgeButtonState.PropertyChanged` to drive `DwellArcButton.UpdateProgress`. `SyncButtons()` is triggered by a single `Buttons.CollectionChanged` subscription, covering `ShowButtons`/`AddButton`/`RemoveButton`/`Hide`.

### Window lifecycle wiring

**Seed centers are authoritative — there is no View→VM feedback loop.** Unlike `ToolbarWindow` (whose buttons are laid out by a `StackPanel`, so the View must report positions back via `PointToScreen`), the edge bar computes button positions arithmetically (anchor + offset) and the window renders each button at its seed center. The detector target and the rendered button therefore coincide by construction. The `ToolbarWindow` lazy-`UpdateGaze`→`UpdateTargetPositions` machinery is deliberately **not** reproduced.

1. **Creation:** `OverlayManager` constructs the `EdgeButtonBarViewModel` (resolved via DI) and the `EdgeButtonBarWindow`, sets `window.DataContext = vm`, and calls `window.Show()`. The window exists for the app lifetime (like `ToolbarWindow`) — it is hidden, not recreated, per context.
2. **Sync:** `OverlayManager.Initialize` subscribes `vm.Buttons.CollectionChanged → window.SyncButtons()`. Every button-set mutation (`ShowButtons`/`AddButton`/`RemoveButton`/`Hide`) flows through `Buttons`, so one subscription covers all cases; Phases 2–4 never call `SyncButtons` explicitly.
3. **Progress binding:** `SyncButtons` subscribes each `EdgeButtonState.PropertyChanged` → `DwellArcButton.UpdateProgress` (instances kept in a `Dictionary<string, DwellArcButton>` keyed by id).
4. **Visibility:** the window is shown/hidden from `EdgeButtonBarViewModel.IsVisible` (e.g. via a `PropertyChanged` subscription in `EventCoordinator`/`OverlayManager`), not recreated.

### Positioning

All right-edge buttons share the right screen edge for X, but the **vertical anchor Y is flexible and per-use-case** — each context supplies its own anchor Y (and the bar stacks its buttons around that Y). This replaces a hardcoded "vertical center" so a use case can place its buttons higher, lower, or at center as appropriate. The App layer computes the `(X, Y)` anchor from primary-screen bounds via `DpiHelper` (the existing pattern used by `ComputeDragEdgeButtonCenters` and scroll's `stopButtonCenter`) and passes it to `ShowButtons`.

- The **anchor Y is a code-level per-use-case constant** in the App layer (not a user setting). It defaults to screen center; each context may offset. Surfacing it as a per-use-case setting is explicitly deferred (out of scope) unless desired later.
- **No "inter-button gap."** Each `EdgeButtonDefinition` carries its own `VerticalOffset` (signed physical px from the anchor Y), so spacing is per-button and per-use-case, not a single VM constant. The bar places buttons at `anchor.Y + offset`; it does **not** re-stack or re-center on add/remove (see Components).
- Drag cancel/back: `screenWidth - margin`, anchor Y = screen center; `Drag_cancel` at offset **+30px** (below center), `Drag_back` at offset **−30px** (above center) — preserves today's exact placement and vertical order (cancel below, back above). Both slots are fixed, so adding back when manual selection engages does not move cancel.
- Scroll stop: `screenWidth - margin`, anchor Y = screen center, offset **0** (preserves today's placement).
- Manual selection close/recapture: **relocated** to the right edge in **both** `AtCenter` and `AtGaze` modes (today they sit right-of-the-magnifier; this design moves them to the stable right-edge bar). Their offsets reuse `UIConstants.ManualSelectionSideButtonGap`-derived spacing. This is a deliberate behavior change (see Behavior changes).

The bar window sizes itself to encompass the stacked buttons (bounding-box approach, as `ManualSelectionSideButtonWindow.ShowAt` does today).

### Behavior changes (user-visible)

This refactor is mostly internal, with **one deliberate UX change** that deserves explicit callout:

- **Manual-selection close/recapture relocate to the right screen edge.** Today these buttons sit immediately right of the magnifier circle (content-relative), so their position depends on `ViewPosition` and, in `AtGaze` mode, on where the user is looking. After this change they move to the **fixed right-edge bar** in **both `AtCenter` and `AtGaze` modes**. Rationale: the user always finds close/recapture at the same stable right-edge location regardless of magnifier position — "don't chase buttons." Trade-off: in `AtGaze` mode, when the user is looking at the far left, the buttons are physically farther from the gaze point. Given dwell times of 500ms+ and the eye's rapid saccade ability, this is acceptable for the benefit of a predictable location. If user testing finds the distance objectionable in `AtGaze`, the per-use-case anchor Y (or a future per-use-case X) provides the escape hatch without redesign.

No other user-visible behavior changes. Drag cancel/back keep their current right-edge placement (anchor Y = screen center, cancel at +30px / back at −30px); scroll stop keeps its current right-edge placement.

### Per-context button sets

| Context | Buttons on the bar |
|---|---|
| Drag (free) | `Drag_cancel` ✕ |
| Drag (manual selection active) | `Drag_cancel` ✕, `Drag_back` ↩ (added dynamically when settle engages manual selection; removed when dismissed) |
| Scroll | `scroll-stop` ⏹ |
| Manual selection (standalone, any `ViewPosition`) | `ManualSelection_close` ✕, `ManualSelection_recapture` ↻ |

The bar supports a **dynamic** button set: when manual selection engages/disengages mid-drag, drag calls `AddButton(back_definition)` / `RemoveButton("Drag_back")`. Because each button occupies its own fixed slot, adding back does not move `Drag_cancel`; the target add/remove is atomic so no dwell fires on a half-removed target.

### Drag inversion

Today `DragTrailViewModel` owns `Drag_release`/`Drag_cancel`/`Drag_back` in one detector and **sends** `DragCancelMessage`/`DragDismissZoomMessage` from its own activation handlers. In the new design:

- The **edge bar** owns `Drag_cancel`/`Drag_back` dwell and sends the cancel/dismiss messages.
- `DragTrailViewModel` keeps **only** the cursor-following `Drag_release` target + settle detection (`FixationDetector`) + trail visuals.
- `DragTrailViewModel` is refactored to **consume** `DragCancelMessage`/`DragDismissManualSelectionMessage` for its cleanup (`IsActive = false`, reset detectors, `DragState = FreeDrag`), instead of originating them.

Bonus: the Escape-key path (`EventCoordinator.cs:398`) already sends `DragCancelMessage` directly. Making `DragTrailViewModel` a consumer of that message unifies cleanup across all trigger sources (edge dwell, Escape, future triggers) — today the Escape path relies on `EventCoordinator.OnDragCancel` and does not run `DragTrailViewModel`-local cleanup.

### Data flow

1. A mode starts. The **mode owner** computes the edge anchor, builds the `EdgeButtonDefinition` list, and calls `edgeBar.ShowButtons(definitions, edgeAnchor)`. The mode owner is **per-context**: `ScrollModeController` for scroll (it already owns `EnterScrollMode`/`stopButtonCenter`/lifecycle post-#56), and `EventCoordinator` for drag (`OnDragStarted`) and manual selection. Hiding is symmetric: the same owner calls `edgeBar.Hide()` on exit (`ScrollModeController.ExitScrollMode`, `OnDragCancel`/`OnDragDismissManualSelection`, manual-selection exit).
2. The bar VM adds `DwellTarget`s to its base `DwellDetector` at the arithmetic seed centers; `SyncButtons` lays out `DwellArcButton`s at those same centers (seed centers authoritative — no report-back).
3. Gaze flows in via `GazeCursorMovedMessage`; the base class feeds the detector (only while `IsVisible`).
4. On dwell activation, the VM sends the button's declared message; `EventCoordinator` and/or the mode VM react (existing handlers).
5. On context exit, the **mode owner** calls `edgeBar.Hide()` (see step 1 for the per-context owner).

## Migration Sequencing

Ordered simplest → hardest; each step is independently shippable and verified before the next.

1. **Add the bar (dormant).** New `EdgeButtonBarViewModel`, `EdgeButtonBarWindow`, `EdgeButtonDefinition`, `EdgeButtonState`. Register in DI / `OverlayManager`. Not wired to any context. Additive; no behavior change. Includes unit tests for the VM.
2. **Migrate scroll stop.** The stop button moves from the scroll HUD to the bar; `ScrollModeController` (the mode owner) drives the bar. Concretely, after migration:
   - `ScrollModeController.EnterScrollMode()` stops computing `stopButtonCenter` for the HUD and instead computes the edge anchor and calls `edgeBar.ShowButtons([{scroll-stop def}], edgeAnchor)`. It still creates the VM + HUD window and subscribes `TargetVelocityChanged`/`FeedbackChanged` for HUD-only visuals.
   - **New message:** introduce `ExitScrollModeMessage` (parameterless record). The bar's `scroll-stop` definition carries `Activate = m => m.Send(new ExitScrollModeMessage())`. `ScrollModeController` registers `IMessenger.Register<ScrollModeController, ExitScrollModeMessage>(...)` → `ExitScrollMode()`, and **drops** the `_scrollOverlayVm.ExitRequested += OnScrollExitRequested` subscription (the `ExitRequested` event and `OnTargetActivated → ExitRequested` wiring are removed from the VM with the stop target).
   - `ScrollModeController.ExitScrollMode()` calls `edgeBar.Hide()` in addition to the existing teardown.
   - `ScrollOverlayViewModel`: remove the `scroll-stop` `DwellTarget`, `StopButtonProgress`, `StopButtonCenter`, and the stop-dwell wiring in `OnTargetActivated`/`OnTargetProgress`. `ExitRequested` is now raised by the bar (via the stop activation message), not the VM.
   - `ScrollOverlayWindow.xaml`: remove `StopButton`, `StopButtonProgressArc`, `StopLabel`. `UpdateFeedback` loses its `stopProgress` parameter — the chain `ScrollOverlayVm.FeedbackChanged → ScrollModeController.OnScrollFeedbackChanged → ScrollOverlayWindow.UpdateFeedback` now carries HUD-only data (anchor dot offset, velocity).
   - The HUD keeps crosshair, anchor dot, velocity ring, cursor circle. Smallest, most isolated migration.
3. **Migrate manual-selection close/recapture.** Delete `ManualSelectionSideButtonWindow` (+ `.xaml`). Move `ManualSelection_close`/`ManualSelection_recapture` targets from `ManualSelectionOverlayViewModel` to the bar. Remove the side-button progress wiring in `EventCoordinator.OnManualSelectionSideButtonsPropertyChanged`. Relocate the buttons to the right edge (both `AtCenter` and `AtGaze`).
4. **Migrate drag cancel/back (highest risk, last).** Move `Drag_cancel`/`Drag_back` targets into the bar. Refactor `DragTrailViewModel` to keep only `Drag_release` + settle, and to consume `DragCancelMessage`/`DragDismissManualSelectionMessage` for cleanup. Rename `Zoom*` → `ManualSelection*` (`DragSubState.ZoomActive`, `DismissZoom()`, `DragDismissZoomMessage`) via graph-aware `rename`. Update `StartDragTrailMessage` so cancel/back centers come from the bar's edge anchor.
5. **Cleanup.** Delete dead code; run the full suite; re-run the GitNexus index.

## Testing

Per repo conventions: xUnit + NFluent, no mocking (private nested fakes), `XxxTests` names, namespaces mirror source.

- **New** `EdgeButtonBarViewModelTests` (headless, like `ToolbarViewModelTests`):
  - `ShowButtons_AddsTargetsAndRaisesVisibility`
  - `Hide_ClearsTargetsAndButtons`
  - `OnTargetActivated_SendsDeclaredMessage`
  - `OnTargetProgress_UpdatesButtonState`
  - `ShowButtons_DynamicAddRemove_ForDragBackButton` (add "back" mid-context; assert target added/removed atomically; no stale-target activation)
  - `AddButton_DoesNotMoveSurvivingButton_PreservesProgress` (seed cancel, advance its dwell partway, `AddButton(back)`, assert cancel's `DwellTarget` center is unchanged and its progress is intact — the regression the fixed-slot model exists to prevent)
- **New** `EdgeButtonBarWindow` control tests (if `DwellArcButtonTests` patterns apply) or coverage via existing UI-test category.
- **Update** `DragTrailViewModelTests` — cancel/back no longer driven here; assert cleanup runs on receiving `DragCancelMessage`/`DragDismissManualSelectionMessage`. Release + settle tests remain.
- **Update** `ScrollOverlayViewModelTests` — remove `scroll-stop` dwell assertions; verify the VM no longer owns a stop target.
- **Update** `ManualSelectionOverlayViewModelTests` — remove close/recapture dwell assertions.
- **Delete** `ManualSelectionSideButtonWindow`-specific tests (if any).
- Update `ViewModelDisposableTests` to cover `EdgeButtonBarViewModel`.

## Edge Cases & Error Handling

- **Mutual exclusivity:** the four states are exclusive, but `Hide()` defensively clears the button set + detector targets, so a context that fails to clean up cannot leave stale buttons. If two contexts ever overlap, log and apply last-writer-wins.
- **Dynamic set mid-drag:** adding "back" when manual selection engages must add/remove the `DwellTarget` atomically — no dwell firing on a half-removed target. Fixed per-button slots guarantee `Drag_cancel`'s center does not move when `Drag_back` is added, so its dwell progress is preserved. `AddButton(definition)`/`RemoveButton(id)` only touch the one button's target + visual; surviving buttons are untouched.
- **Late activation:** if a dwell completes as the context ends, `Hide()` clears targets *before* the window hides, so no stale activation message fires post-context.
- **Escape during drag:** already sends `DragCancelMessage` directly (`EventCoordinator.cs:398`); the inverted consumer model makes `DragTrailViewModel` cleanup run on this path too.
- **DPI / multi-monitor:** edge X from primary-screen bounds via `DpiHelper` (existing pattern). The bar window is created once in `OverlayManager` and shown/hidden per context (not recreated).
- **Bar lifecycle:** one instance, like `ToolbarWindow`. Shown/hidden as contexts activate; never two bars.
- **Hide() threading:** `Hide` (and `ShowButtons`/`AddButton`/`RemoveButton`) must run on the dispatcher thread so they cannot interleave mid-`DwellDetector.Update`. Several `EventCoordinator`/`ScrollModeController` paths already marshal via `Dispatcher.BeginInvoke`; the bar's mutating calls follow the same discipline.
- **Message identity:** the bar sends message types that already have handlers for drag (`DragCancelMessage`, `DragDismissManualSelectionMessage`) and manual selection (`ManualSelectionCloseMessage`, `ManualSelectionRecaptureMessage` — both already handled in `EventCoordinator`), so the **routing from bar activation to coordinator action requires no new handlers there**. **Scroll is the exception:** today scroll-stop is an `Action` event (`ScrollOverlayViewModel.ExitRequested` → `ScrollModeController.OnScrollExitRequested`), not a message. Since the bar only sends messages, migration step 2 introduces a new `ExitScrollModeMessage` and registers `ScrollModeController` for it. `DragTrailViewModel` also gains two new `Register` calls, but for its **cleanup path** (consuming cancel/dismiss per Drag Inversion), not activation routing.

## Out of Scope

- The always-on docked `ToolbarWindow`.
- The cursor-following drag release button.
- Scroll HUD non-button visuals.
- Centralizing dwell detection for non-edge buttons.
