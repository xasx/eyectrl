# Keyboard action-menu action + quick Top/Bottom switch — design

Date: 2026-09-10
Status: awaiting owner review (round 2 — owner feedback incorporated, see §5 #5–#6 and #9–#11)
Brainstorm session: owner dismissed both clarifying-question rounds; every open choice is
encoded in the decision ledger (§5) and is vetoable. Point review comments at ledger
numbers.

Owner round-1 feedback (2026-09-10), addressed in this revision: the flip button must not
rely on the toolbar being force-pinned while the keyboard is up — either the button lives
elsewhere, or the toolbar stays auto-hide and the keyboard slides to give a summoned
toolbar room. This revision takes the second path (ledger #9); the first (on-keyboard
frame button) is kept in §4.3 as the fallback.

## 1. Problem

Two gaps reported by the owner:

1. The action-menu ring cannot open the gaze keyboard. The only live path to the keyboard
   is the toolbar's Keyboard button (`ToolbarViewModel.OnTargetActivated` case
   `"Keyboard"` → `KeyboardToggledMessage`). `ShowKeyboardRequestMessage` /
   `HideKeyboardRequestMessage` exist in `KeyboardCoordinator` but have no production
   senders (reserved text-field seam).
2. Moving the keyboard between screen edges requires Settings → Keyboard → Screen Edge —
   several dwells each way, and the round trip is repeated whenever the keyboard occludes
   what the user is reading or typing into.

### 1.1 Constraints discovered in exploration

- **C1 — ring chain is fully tooled.** Adding an `InteractionAction` member automatically
  gets: a Settings visibility toggle and a "on first layer" toggle
  (`ActionMenuPageSource` enumerates the enum), the icon-map no-default throw in
  `ActionMenuViewModel.GetActionIcon` (and `ActionMenuViewModelSymbolTests.
  EveryAction_MapsToADistinctIcon`), and the dispatch switch in
  `KeyboardInteractionDispatcher.HandleActionRequested`.
- **C2 — one-shot migration pattern is established.** `ActionMenuSettings.DefaultsMigrated`
  / `PrimaryMigrated` and `ToolbarSettings.SizeMigrated` all migrate a pre-existing
  settings file exactly once; the migration adds things that did not exist when the file
  was written (so no "re-add what the user removed" hazard).
- **C3 — toolbar is force-pinned while the keyboard is up (behavior this design REMOVES).**
  `KeyboardCoordinator.ApplyModalKeyboardState` suspends auto-hide;
  `ToolbarAutoHideController.SetSuspended(true)` force-recovers from hidden "so the user
  isn't stranded". The owner rejected designs that depend on this pin: while typing, the
  pinned strip eats screen space. The stranding rationale does not survive scrutiny —
  the controller's `Feed` is subscribed directly to `GazeEngine.GazeSampled`
  (ToolbarLifecycleCoordinator.cs:202) independent of the modal flags, so with the
  suspension removed the toolbar edge-summons normally (~400 ms) and its Keyboard button
  remains reachable. The settings-window suspension (`SettingsUiCoordinator.cs:117`) is a
  separate caller and stays.
- **C4 — same-edge collision is handled by the keyboard yielding, not by moving the
  toolbar.** Keyboard and toolbar both position via `OverlayPositioning.PlaceAtEdge`;
  at the same vertical edge they overlap. With the pin removed, the toolbar is only on
  screen while gaze-summoned; when it appears onto the keyboard's edge the keyboard
  slides toward screen center by the toolbar's extent and slides back when the toolbar
  hides (owner's explicit direction). The toolbar never relocates.
- **C5 — snapshot discipline.** `KeyboardStackSnapshot` includes `ScreenEdge`
  (`SettingsSnapshots.cs`). Mutating `KeyboardSettings.ScreenEdge` without refreshing
  `KeyboardCoordinator._keyboardSnapshot` plants a phantom diff: the next Settings apply
  rebuilds the whole keyboard + prediction stack for nothing (the exact bug class the
  `ApplyEmojiSkinTone` remarks document).
- **C6 — settings saves don't notify.** `SettingsService.SaveDebounced` writes the file;
  only the Settings window's own `SettingsChanged` event reaches
  `SettingsUiCoordinator.OnSettingsChanged`. A runtime toggle that saves debounced and
  applies live itself triggers no rebuild path.
- **C7 — toolbar button wiring is drift-guarded.** `ToolbarButtonDriftTests` forces every
  view-model button tuple to be rendered by `ToolbarBand` (or listed in
  `ReachableElsewhere`) and to have an activation `case`. Adding a button is safe against
  half-wiring.

## 2. Goals / non-goals

**Goals**

- G1: Open the gaze keyboard from the action-menu ring (one activation, any state the ring
  can be opened from).
- G2: Flip the keyboard Top ↔ Bottom in one dwell, from a surface that is reachable while
  the keyboard is visible.
- G3: Both changes persist and live-apply without restart and without a keyboard rebuild.

**Non-goals**

- NG1: Left/Right keyboard edges (the setting is a validated Top/Bottom string enum;
  widening it is a separate question).
- NG2: A per-window/situational automatic edge chooser.
- NG3: Moving the toolbar's Left/Right dock options anywhere — Settings keeps them.
- NG4: Any change to `InteractionManager`'s state machine. The keyboard action needs no
  Core state, exactly like PinNote.

## 3. Feature A — action-menu "Keyboard" action

### 3.1 What the user sees

A "Keyboard" target on the ring, behind **More** by default (layer 2), with a drawn
keyboard glyph. Activating it closes the menu and shows the gaze keyboard at its current
edge, with the same modal semantics as the toolbar toggle (fixation suppression,
quick-actions summon suspended). If the keyboard is already visible, the action is an
idempotent no-op (it never hides).

### 3.2 Changes, in dependency order

1. **`InteractionAction.Keyboard`** (`src/EyeCtrl.Core/Models/InteractionAction.cs`):
   new member, summary "Show the on-screen gaze keyboard."
2. **`ActionIcon.Keyboard`** (`src/EyeCtrl.Core/Models/ActionIcon.cs`) + drawn path in
   `EyeCtrl.Controls/Icons/ActionIcons.cs`: rounded-rect keyboard outline, two dot rows,
   space bar — same hand-drawn path style as the rest of the set.
3. **`ActionMenuViewModel`**: `GetActionIcon` maps `Keyboard → ActionIcon.Keyboard`
   (no-default throw until done; symbol test enforces), `FriendlyName` → `"Keyboard"`.
4. **`ActionMenuSettings`**: add `Keyboard` to the `VisibleActions` initializer and to the
   `EnsureDefaults` array. New one-shot flag `_keyboardActionMigrated` +
   `EnsureKeyboardActionOnce()` (C2 pattern): adds `Keyboard` to `VisibleActions` if
   absent, once per settings file. **Not** added to `PrimaryActions` — the four-slot cap
   (`MaxPrimaryActions`, ring aimability) is full by default and Validate() rejects five.
   Startup caller wires `EnsureKeyboardActionOnce()` next to the existing
   `EnsureDefaultsOnce()` / `MigratePrimaryOnce()` calls.
5. **`KeyboardShowRequestedMessage`** (new record, `EyeCtrl.ViewModels/Messages/
   InteractionMessages.cs`, beside `KeyboardToggledMessage`; no payload).
6. **`KeyboardCoordinator.OnInitialize`** registers it:
   `SetKeyboardVisible(true, manageSuppression: true)` guarded by an
   `if (_modal.KeyboardVisible) return;` early-out (idempotent show).
   `manageSuppression: true` is load-bearing: a show that leaves free-look active lets the
   next fixation pop an action menu over the keyboard. This deliberately does NOT reuse
   `ShowKeyboardRequestMessage`, whose handler is `manageSuppression: false` (reserved
   text-field seam — see ledger #2).
7. **`KeyboardInteractionDispatcher.HandleActionRequested`**: intercept
   `msg.Action == InteractionAction.Keyboard` before the click switch (PinNote
   precedent), `Messenger.Send(new KeyboardShowRequestedMessage())`, then fall through to
   the shared target-highlight cleanup at the bottom of the method (unlike PinNote, which
   duplicates it before `return`). Core `InteractionManager.OnActionSelected` needs no
   change: `Keyboard` flows through `ActionRequested` like a click; the point is unused.
8. **`ActionMenuPageSource`**: add
   `InteractionAction.Keyboard => "Opens the gaze keyboard for typing."` to
   `ActionDescription` — `SettingsRowDescriptionCompletenessTests` fails on the null arm.
   Add the explicit `FriendlyName` case ("Keyboard") for symmetry with the other ten
   (the `_ => action.ToString()` arm already yields the same string).

### 3.3 Rejected surface variants (see ledger #1)

Toggle semantics (would hide when already visible); reusing `KeyboardToggledMessage`;
Core-side special-casing like `ScrollMode` (needs no gaze anchor, so no state).

## 4. Feature B — quick keyboard Top/Bottom switch

### 4.1 What the user sees

A new toolbar button directly beside Keyboard: a keyboard glyph with up/down swap arrows.
One dwell flips the keyboard between Top and Bottom immediately. While the keyboard is
up, the toolbar is **no longer force-pinned**: it hides when gaze leaves its edge and
edge-summons on demand, exactly as it behaves without the keyboard. When the toolbar is
summoned onto the keyboard's edge, the keyboard slides toward screen center to make room
and slides back when the toolbar hides. The flip works whether the keyboard is visible
or hidden (pre-positioning), and in emoji mode (window-level).

### 4.2 Changes, in dependency order

**B1 — the flip itself**

1. **`KeyboardEdgeToggledMessage`** (new record, no payload, `InteractionMessages.cs`
   beside `KeyboardToggledMessage`).
2. **`ToolbarViewModel`**: button tuple `("KeyboardEdge", "KeyboardEdge")` immediately
   after `("Keyboard", "Keyboard")`; activation `case "KeyboardEdge": Messenger.Send(new
   KeyboardEdgeToggledMessage()); break;`.
3. **`ToolbarIconKey.KeyboardEdge`** + `ToolbarIcons` path: keyboard outline with up/down
   arrows (swap motif; distinct from the plain `Keyboard` glyph).
4. **`ToolbarBand`**: render tuple in the matching position (C7 guard forces VM/band/case
   consistency).
5. **`KeyboardCoordinator`** registers `KeyboardEdgeToggledMessage` →
   `HandleKeyboardEdgeToggled`:
   a. Pure, headless-testable flip: `internal static string FlipScreenEdge(string edge)`
      → `"Top"` becomes `"Bottom"`, anything else reads as Bottom and becomes Top (same
      normalization as `KeyboardWindow.RepositionOnScreen`).
   b. Sets `settings.Keyboard.ScreenEdge` (observable — `SetScreenEdge` repositions; the
      `SizeChanged → RepositionOnScreen` hook at KeyboardWindow.xaml.cs:86 covers the
      never-shown-this-session case at first layout).
   c. `Context.SettingsService.SaveDebounced(settings)` (C6 — no notification path fires,
      so no rebuild).
   d. Refreshes `_keyboardSnapshot = KeyboardStackSnapshot.Capture(settings)` (C5 —
      kills the phantom rebuild on the next Settings apply; same discipline as
      `ApplyEmojiSkinTone`).

**B2 — un-pin the toolbar while the keyboard is up** (owner direction)

6. **`KeyboardCoordinator.ApplyModalKeyboardState`**: delete the
   `_toolbar.SetAutoHideSuspended(visible)` call. The method keeps the quick-actions
   summon suspension (`_quickActions.SetSummonSuspended(visible)` — the hot-edge grid
   must not pop over the keyboard) and the `_modal.KeyboardVisible` flag (the gaze-routing
   perf early-out and per-sample routing in `ToolbarLifecycleCoordinator.SetupGazeRouting`
   keep working: while `KeyboardVisible`, samples still dispatch to
   `toolbar.UpdateGaze` + `keyboard.UpdateGaze`, so toolbar buttons — including the
   keyboard-hide button and the new flip — remain dwellable while summoned).
   Incidentally removes a pre-existing interleaving: both Keyboard and Settings
   coordinators write the same un-refcounted suspension bool, so closing the keyboard
   while the Settings window was open used to un-pin the toolbar out from under it.
   The Settings-window suspension (`SettingsUiCoordinator.cs:117`) is untouched.

**B3 — the keyboard yields to a summoned toolbar** (owner direction)

7. **Toolbar visibility transitions become observable.**
   `ToolbarVisibilityAdapter` raises a `ToolbarVisibilityChangedMessage(bool Visible)` on
   each show/hide transition it executes — the adapter, not the auto-hide controller, is
   the single point every visibility driver funnels through (auto-hide, settings-open
   suspension, tray `ForceShow`), so the message reflects actual on-screen state
   regardless of who caused it.
8. **`KeyboardWindow.SetToolbarOccupancy(bool occupied, string edge, double
   extentDip)`**: stores the occupancy; `RepositionOnScreen` applies a yield offset when
   `occupied && edge` equals the keyboard's effective edge — `Top += extent` for the top
   case, `Top -= extent` for bottom. The extent is the toolbar's on-screen thickness
   read from the toolbar window (`ActualHeight` for vertical edges). Instant
   reposition; no animation in v1.
9. **`KeyboardCoordinator`** subscribes to the transition message and forwards occupancy
   to `KeyboardWindow`; on keyboard show it queries the current toolbar state once so a
   keyboard opened while the toolbar is already summoned lands pre-yielded.
10. **Safety of moving the keyboard under gaze:** `KeyboardWindow.UpdateGaze` recomputes
    key screen positions (`UpdateKeyScreenPositions`) and prediction target positions on
    **every sample**, so dwell targets track window moves by construction — no target
    re-registration pass is needed. A slide under an in-progress key dwell lets the
    nearest-key logic re-acquire naturally (documented, accepted; the yield fires when
    gaze is at the screen edge summoning, the return when gaze is on keys — a top-row
    dwell may re-acquire once).

### 4.3 Rejected variants (see ledger #5–#6, #9)

Toolbar relocation on collision (round-1 design — the toolbar never moves now);
on-keyboard frame button (owner's alternative — viable fallback if the yield seam proves
brittle, but it keeps the pinned-toolbar behavior the owner objected to); always-swapping
keyboard + toolbar together; static nudge below a pinned toolbar; a ring action (position
is a global UI concern, not point-targeted); a new QuickActionType (heavier editor +
preset surface).

## 5. Decision ledger (owner-vetoable)

| # | Decision | Rationale | Rejected |
|---|----------|-----------|----------|
| 1 | Ring action semantics: **show, idempotent** (never hides) | "click and open the keyboard"; an accidental hide from the ring strands the typing flow; hiding already costs one dwell on the toolbar | Toggle semantics; reuse of `KeyboardToggledMessage` |
| 2 | New `KeyboardShowRequestedMessage` handled with `manageSuppression: true` | Must match the toolbar toggle's modal semantics (fixation suppressed, summon suspended) or the next fixation opens a menu over the keyboard | Reusing `ShowKeyboardRequestMessage` — its handler is deliberately `manageSuppression: false` for the text-field seam |
| 3 | Default **on** (layer 2, behind More) + one-shot migration `KeyboardActionMigrated` | The action never existed, so no user could have removed it — the one-shot add is the established safe pattern (C2); layer 2 because the primary cap (4) is a validated aimability constraint and is full | Opt-in only (feature ships invisible to existing users); primary-layer default (Validate rejects 5) |
| 4 | Dispatch intercepts in `KeyboardInteractionDispatcher` before the click switch | PinNote precedent exactly; zero Core state-machine changes; the target point is genuinely unused | A Core special-case like ScrollMode (which exists because scroll needs an anchor point) |
| 5 | Collision handled by **keyboard yielding** to a summoned toolbar; the toolbar never relocates | Owner round-1 direction ("the keyboard can slide to give room to the toolbar"); no persisted toolbar change, no DockedEdge writes, summon edge stays where the user learned it | Toolbar relocation to the opposite edge (round-1 design, superseded); static nudge below a pinned toolbar |
| 6 | Quick switch surface: **toolbar button beside Keyboard**, reachable via normal edge-summon | Owner round-1 accepts the toolbar surface provided it is not force-pinned; toolbar stays the app's single control surface; button system is drift-guarded (C7) | On-keyboard frame button (owner's other alternative — kept as fallback, §4.3); ring action (not point-targeted); a new QuickActionType (heavier editor + preset surface) |
| 7 | Persistence: mutate the live settings object + `SaveDebounced` + live apply + snapshot refresh | C5/C6: correct across restart, no rebuild now, no phantom rebuild later | Settings-window round trip (the thing being removed); direct window calls without persisting (reverts on restart) |
| 8 | Left/Right-docked toolbar: **no yield**; orthogonal edges overlap only at extreme key sizes | Yield only makes sense for a toolbar parallel to the keyboard's edge; a side strip crossing a wide keyboard is transient (toolbar visible only while summoned) | Horizontal yield for side-docked toolbars |
| 9 | **Un-pin the toolbar while the keyboard is up** (remove the keyboard path's auto-hide suspension only) | Owner round-1: the pinned strip eats typing space; summon still works because `Feed` subscribes to `GazeSampled` unconditionally, so the hide path is never lost; also removes the keyboard/settings suspension interleaving | Keep the pin and put the button elsewhere (owner's alternative #1); keep the pin and relocate the toolbar (round-1 design) |
| 10 | Keyboard yield: **instant reposition, no animation, occupancy consulted on keyboard show** | Minimum that satisfies "slides to give room"; per-sample target recompute (B3 #10) makes the move safe; animation is polish, not function | Animated slide in v1; yield only after show (would mis-place a keyboard opened onto a summoned toolbar) |
| 11 | Quick-actions hot-edge summon stays **suspended** while the keyboard is up | Un-pinning applies to the toolbar only; the quick-actions grid popping over the keyboard is unwanted and unrelated to the owner's complaint | Also un-suspending the quick-actions summon |

## 6. Testing

- **Core (`EyeCtrl.Core.Tests`)**
  - `ActionMenuSettings.EnsureKeyboardActionOnce`: adds `Keyboard` when absent; skips when
    already present (no duplicate); never re-runs after the flag sets; default
    `VisibleActions` contains `Keyboard`; default `PrimaryActions` does not.
  - `Validate()` unchanged behavior (4-cap still enforced).
- **ViewModels (`EyeCtrl.ViewModels.Tests`)**
  - Existing `ActionMenuViewModelSymbolTests.EveryAction_MapsToADistinctIcon` covers the
    new mapping automatically (fails until the icon case exists).
  - `FriendlyName(Keyboard)` = "Keyboard".
- **App (`EyeCtrl.App.Tests`)**
  - `FlipScreenEdge` truth table: `"Top"` → `"Bottom"`, `"Bottom"` → `"Top"`,
    non-`"Top"` garbage reads as Bottom.
  - `HandleKeyboardEdgeToggled` thin-apply seam: assert settings mutation + save + snapshot
    refresh via the same headless style as `ApplyEmojiSkinTone` tests (real window objects
    stay out of reach — the pure function carries the logic).
  - `ApplyModalKeyboardState` no longer touches the toolbar auto-hide suspension but still
    suspends the quick-actions summon and still publishes `KeyboardVisible`. Existing
    tests that assert keyboard-visible pins the toolbar must be inverted/removed — grep
    `SetSuspended` in the coordinator tests first.
  - Yield math as a pure function: given `(keyboardEdge, occupied, toolbarEdge, extent)`
    → expected `Top` offset (0 when edges differ or not occupied).
  - `KeyboardShowRequestedMessage` handler: idempotent early-out when
    `ModalInteractionState.KeyboardVisible` already true.
  - `ToolbarButtonDriftTests` (existing) enforces the new button's VM/band/case wiring.
  - `ActionMenuPageSource` description completeness gate (existing) fails until the
    description string lands.
- **Manual / UI-gated (owner-run)**: ring → Keyboard opens the keyboard with suppression;
  toolbar button flips the visible keyboard; keyboard up → toolbar auto-hides and
  edge-summons; summoning the toolbar onto the keyboard's edge slides the keyboard aside
  and back; hide path (toolbar Keyboard button) reachable via summon while keyboard up.

## 7. Risks

- **R1 — icon quality.** Two new hand-drawn paths (ring + toolbar). Both sets are
  hand-authored geometry; the plan should render-check at real size (the repo's
  established visual-review step).
- **R2 — toolbar button count.** The band gains one button (currently ~6 rendered). At
  default Size 120 with 12-px margins this fits the band; extreme Size + narrow screens
  were already tight before. No action taken (YAGNI) beyond noting it.
- **R3 — settings window staleness.** If the Settings window is open while the toolbar
  toggle fires, its Screen Edge / Docked Edge rows show stale values until rebuilt. Same
  staleness class as every other runtime mutation (e.g. skin tone); accepted.
- **R4 — `FlipScreenEdge` normalization.** "anything not Top is Bottom" mirrors
  `KeyboardWindow.RepositionOnScreen`; the function must not invent a third state.
- **R5 — un-pinning is a behavior change for every keyboard user.** The pin's original
  rationale ("stranded while a modal is up") is void because the summon path stays live
  (`Feed` is unconditionally wired), but the round trip to hide the keyboard is now
  gaze-to-edge + ~400 ms summon + dwell instead of a pinned button. Manual testing must
  confirm the summon path feels acceptable; the round-1 fallback (on-keyboard frame
  button, §4.3) exists if it does not.
- **R6 — the keyboard slides under an active dwell.** Yield fires while gaze is at the
  screen edge (safe); the return fires at toolbar idle, when gaze may be on the top key
  row — nearest-key re-acquisition handles it, but a mid-dwell key can shift once.
  Accepted (documented in B3 #10); revisit if struggle-telemetry flags it.
- **R7 — yield seam coupling.** `KeyboardWindow` learns about toolbar extent/edge via the
  coordinator; the seam must be defensive (clamp extent, ignore unknown edges) so a
  pathological toolbar measurement cannot push the keyboard off-screen.
